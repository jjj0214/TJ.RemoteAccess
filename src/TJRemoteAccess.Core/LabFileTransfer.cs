// SPDX-License-Identifier: GPL-2.0-or-later
// Bounded, authenticated-session file transfer. Network policy/consent lives at the call site.
// The receiver refuses traversal, reparse points, overwrites, corrupt hashes, and oversized data.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TJ.RemoteAccess.Lab;

internal sealed record LabFileOffer(Guid Id, string FileName, long Length, byte[] Sha256, string TargetFolder = "");

internal static class LabFileTransfer
{
    internal const long MaxFileBytes = 4L * 1024 * 1024 * 1024;
    internal const int MaxChunkBytes = 48 * 1024;
    internal const int MaxPacketBytes = MaxChunkBytes + 32;
    internal const int MaxNameBytes = 180;

    internal static string SafeFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 180 ||
            fileName is "." or ".." ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileName.Contains('/') || fileName.Contains('\\') ||
            fileName.Contains(':') || fileName.TrimEnd(' ', '.') != fileName ||
            Path.GetFileName(fileName) != fileName ||
            Encoding.UTF8.GetByteCount(fileName) > MaxNameBytes)
            throw new InvalidDataException("Unsafe transfer filename.");
        var stem = Path.GetFileNameWithoutExtension(fileName).TrimEnd('.');
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3",
                    "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                    "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
                    "LPT8", "LPT9" }.Contains(stem, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Reserved transfer filename.");
        return fileName;
    }

    internal static LabFileOffer Validate(LabFileOffer offer)
    {
        if (offer.Id == Guid.Empty || offer.Length < 0 || offer.Length > MaxFileBytes ||
            offer.Sha256 is not { Length: 32 })
            throw new InvalidDataException("Invalid transfer offer.");
        SafeFileName(offer.FileName);
        LabRemoteFileWire.CheckRelative(offer.TargetFolder);
        return offer;
    }

    internal static byte[] OfferBytes(LabFileOffer offer)
    {
        Validate(offer);
        var name = Encoding.UTF8.GetBytes(offer.FileName);
        byte[] folder = Encoding.UTF8.GetBytes(offer.TargetFolder);
        var packet = new byte[58 + name.Length + (folder.Length == 0 ? 0 : 2 + folder.Length)];
        if (folder.Length != 0)
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(58 + name.Length, 2), (ushort)folder.Length);
        offer.Id.TryWriteBytes(packet.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(packet.AsSpan(16, 8), offer.Length);
        offer.Sha256.CopyTo(packet, 24);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(56, 2), (ushort)name.Length);
        name.CopyTo(packet, 58);
        if (folder.Length != 0) folder.CopyTo(packet, 60 + name.Length);
        return packet;
    }

    internal static LabFileOffer ParseOffer(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 59 || payload.Length > 60 + MaxNameBytes + LabRemoteFileWire.MaxRelativePathBytes)
            throw new InvalidDataException("Invalid file offer header.");
        Guid id = new(payload[..16]);
        if (id == Guid.Empty) throw new InvalidDataException("Invalid file offer ID.");
        int nameSize = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(56, 2));
        int afterName = 58 + nameSize;
        if (nameSize < 1 || nameSize > MaxNameBytes || payload.Length < afterName)
            throw new InvalidDataException("Invalid filename size.");
        string name;
        string folder = "";
        try
        {
            var strictUtf8 = new UTF8Encoding(false, true);
            name = strictUtf8.GetString(payload.Slice(58, nameSize));
            if (payload.Length != afterName)
            {
                if (payload.Length < afterName + 3)
                    throw new InvalidDataException("Invalid destination folder length.");
                int folderSize = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(afterName, 2));
                if (folderSize < 1 || payload.Length != afterName + 2 + folderSize)
                    throw new InvalidDataException("Invalid destination folder size.");
                folder = strictUtf8.GetString(payload[(afterName + 2)..]);
            }
        }
        catch (DecoderFallbackException ex)
        { throw new InvalidDataException("Invalid UTF-8 file or folder.", ex); }
        return Validate(new LabFileOffer(id, name,
            BinaryPrimitives.ReadInt64BigEndian(payload.Slice(16, 8)),
            payload.Slice(24, 32).ToArray(), folder));
    }

    internal static byte[] Decision(Guid id, bool accepted)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Invalid transfer ID.");
        var payload = new byte[17];
        id.TryWriteBytes(payload);
        payload[16] = accepted ? (byte)1 : (byte)0;
        return payload;
    }

    internal static (Guid Id, bool Accepted) ParseDecision(ReadOnlySpan<byte> data)
    {
        if (data.Length != 17 || data[16] > 1) throw new InvalidDataException("Invalid transfer decision.");
        Guid id = new(data[..16]);
        if (id == Guid.Empty) throw new InvalidDataException("Missing transfer ID.");
        return (id, data[16] == 1);
    }

    internal static byte[] Chunk(Guid id, long offset, ReadOnlySpan<byte> data)
    {
        if (id == Guid.Empty || offset < 0 || data.IsEmpty || data.Length > MaxChunkBytes)
            throw new InvalidDataException("Invalid transfer chunk.");
        var payload = new byte[24 + data.Length];
        id.TryWriteBytes(payload);
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16, 8), offset);
        data.CopyTo(payload.AsSpan(24));
        return payload;
    }

    internal static (Guid Id, long Offset, byte[] Data) ParseChunk(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 25 || payload.Length > 24 + MaxChunkBytes)
            throw new InvalidDataException("Invalid transfer chunk length.");
        Guid id = new(payload[..16]);
        long offset = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(16, 8));
        if (id == Guid.Empty || offset < 0) throw new InvalidDataException("Invalid chunk identity.");
        return (id, offset, payload[24..].ToArray());
    }

    internal static byte[] TransferId(Guid id)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Invalid transfer ID.");
        return id.ToByteArray();
    }

    internal static Guid ParseTransferId(ReadOnlySpan<byte> data)
    {
        if (data.Length != 16) throw new InvalidDataException("Invalid transfer completion.");
        Guid id = new(data);
        if (id == Guid.Empty) throw new InvalidDataException("Empty transfer ID.");
        return id;
    }

    internal static async Task<LabFileOffer> OfferFromFileAsync(string path, CancellationToken stop)
    {
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            file.Length > MaxFileBytes)
            throw new InvalidDataException("File missing, linked, or exceeds 4 GiB.");
        string name = SafeFileName(file.Name);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] sha = await SHA256.HashDataAsync(stream, stop).ConfigureAwait(false);
        return Validate(new LabFileOffer(Guid.NewGuid(), name, file.Length, sha));
    }

    internal static async Task SendChunksAsync(string path, LabFileOffer offer,
        Func<LabMessage, ReadOnlyMemory<byte>, CancellationToken, Task> send,
        CancellationToken stop)
    {
        Validate(offer);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked source files cannot be transferred.");
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length != offer.Length) throw new IOException("File changed after offer.");
        // Send fixed-size chunks and verify the exact bytes sent before completion.
        using var sentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[MaxChunkBytes];
        long offset = 0;
        while (offset < offer.Length)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0,
                (int)Math.Min(buffer.Length, offer.Length - offset)), stop).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("File truncated during transfer.");
            sentHash.AppendData(buffer.AsSpan(0, read));
            await send(LabMessage.FileChunk, Chunk(offer.Id, offset, buffer.AsSpan(0, read)), stop)
                .ConfigureAwait(false);
            offset += read;
        }
        if (!CryptographicOperations.FixedTimeEquals(sentHash.GetHashAndReset(), offer.Sha256))
            throw new IOException("Source file changed during transfer; completion was not sent.");
        await send(LabMessage.FileComplete, TransferId(offer.Id), stop).ConfigureAwait(false);
    }
}

// File path is selected by the receiving user or restricted to the current-user Received folder.
// A .partial-<GUID> is created exclusively and never becomes visible until SHA-256 succeeds.
internal sealed class LabFileReceiver : IDisposable
{
    private readonly LabFileOffer _offer;
    private readonly string _target;
    private readonly string _partial;
    private readonly FileStream _file;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _received;
    private bool _committed;
    private bool _disposed;

    internal long Received => _received;
    internal long Total => _offer.Length;
    internal Guid Id => _offer.Id;

    internal static string ServerReceivedDirectory => Path.Combine(
        LabCertificates.ConfigDirectory, "Received");

    internal static LabFileReceiver ForServer(LabFileOffer offer)
    {
        // A Viewer may choose only an existing subfolder inside this user's Received root.
        // The Server operator still must approve every incoming file separately.
        string folder = LabRemoteFileStore.ForCurrentUser().UploadFolder(offer.TargetFolder);
        string safe = LabFileTransfer.SafeFileName(offer.FileName);
        // Preserve readable names for the dual-pane manager. Never overwrite an
        // existing file: choose a numbered name, and still atomically commit with
        // File.Move(overwrite:false) to fail safely on a concurrent race.
        string target = Path.Combine(folder, safe);
        string stem = Path.GetFileNameWithoutExtension(safe);
        string suffix = Path.GetExtension(safe);
        int index = 1;
        while (File.Exists(target) || Directory.Exists(target))
        {
            if (index > 1000) throw new IOException("Too many files with the same name.");
            target = Path.Combine(folder, stem + " (" + index++ + ")" + suffix);
        }
        return new LabFileReceiver(offer, target);
    }

    internal LabFileReceiver(LabFileOffer offer, string destination)
    {
        _offer = LabFileTransfer.Validate(offer);
        if (string.IsNullOrWhiteSpace(destination))
            throw new InvalidDataException("File destination required.");
        _target = Path.GetFullPath(destination);
        string parent = Path.GetDirectoryName(_target) ??
            throw new InvalidDataException("Missing destination directory.");
        if (!Directory.Exists(parent) ||
            (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 ||
            File.Exists(_target) || Directory.Exists(_target))
            throw new InvalidDataException("Destination missing, linked, or already occupied.");
        _partial = Path.Combine(parent, ".tjra-" + Guid.NewGuid().ToString("N") + ".partial");
        _file = new FileStream(_partial, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough);
    }

    internal void AddChunk(Guid id, long offset, byte[] data)
    {
        if (_disposed || _committed || id != _offer.Id || offset != _received ||
            data.Length == 0 || data.Length > LabFileTransfer.MaxChunkBytes ||
            data.Length > _offer.Length - _received)
            throw new InvalidDataException("Out-of-order or oversized transfer chunk.");
        _file.Write(data);
        _hash.AppendData(data);
        _received += data.Length;
    }

    internal string Finish(Guid id)
    {
        if (_disposed || _committed || id != _offer.Id || _received != _offer.Length ||
            !CryptographicOperations.FixedTimeEquals(_offer.Sha256, _hash.GetHashAndReset()))
            throw new InvalidDataException("Transfer incomplete or SHA-256 mismatch.");
        _file.Flush(true);
        _file.Dispose();
        File.Move(_partial, _target, overwrite: false);
        _committed = true;
        return _target;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hash.Dispose();
        _file.Dispose();
        if (!_committed)
        {
            try { File.Delete(_partial); } catch (IOException) { }
        }
    }
}
