// SPDX-License-Identifier: GPL-2.0-or-later
// Authenticated, explicitly approved file copy over the EXISTING TJRA TLS stream.
// A bounded preview capability, not a filesystem RPC. No path comes from the peer.
using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed record LabFileOffer(Guid Id, string FileName, long Size, byte[] Sha256);

internal static class LabFileTransferWire
{
    internal const int BlockSize = 32768;
    internal const int MaxPacketBytes = BlockSize + 25;
    internal const long MaxFileBytes = 512L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsFilePacket(LabMessage type) => type is
        LabMessage.FileRequest or LabMessage.FileOffer or LabMessage.FileDecision
        or LabMessage.FileBlock or LabMessage.FileDone or LabMessage.FileCancel;

    internal static bool SafeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 120 ||
            value.EndsWith(' ') || value.EndsWith('.') ||
            value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') || value.Contains('\\') ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
            return false;
        string baseName = value.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return baseName is not ("CON" or "PRN" or "AUX" or "NUL" or
            "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or
            "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or
            "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9");
    }

    internal static byte[] Offer(LabFileOffer offer)
    {
        if (!SafeFileName(offer.FileName) || offer.Id == Guid.Empty ||
            offer.Size < 0 || offer.Size > MaxFileBytes || offer.Sha256.Length != 32)
            throw new InvalidDataException("Unsafe or oversize file offer.");
        var name = StrictUtf8.GetBytes(offer.FileName);
        if (name.Length is < 1 or > 240)
            throw new InvalidDataException("Invalid UTF-8 filename length.");
        var output = new byte[58 + name.Length];
        offer.Id.TryWriteBytes(output.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(16, 8), offer.Size);
        offer.Sha256.CopyTo(output.AsSpan(24, 32));
        BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(56, 2), (ushort)name.Length);
        name.CopyTo(output.AsSpan(58));
        return output;
    }

    internal static LabFileOffer ParseOffer(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 59 || packet.Length > 298)
            throw new InvalidDataException("Invalid file offer length.");
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(56, 2));
        if (length < 1 || length > 240 || packet.Length != 58 + length)
            throw new InvalidDataException("Invalid file offer filename length.");
        string name;
        try { name = StrictUtf8.GetString(packet[58..]); }
        catch (DecoderFallbackException e) { throw new InvalidDataException("Invalid file name UTF-8.", e); }
        var result = new LabFileOffer(new Guid(packet[..16]),
            name, BinaryPrimitives.ReadInt64BigEndian(packet.Slice(16, 8)),
            packet.Slice(24, 32).ToArray());
        _ = Offer(result); // Canonical roundtrip enforces policy on received metadata.
        return result;
    }

    internal static byte[] Decision(Guid id, bool accept)
    {
        var bytes = new byte[17];
        id.TryWriteBytes(bytes);
        bytes[16] = accept ? (byte)1 : (byte)0;
        return bytes;
    }

    internal static (Guid Id, bool Accepted) ParseDecision(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 17 || packet[16] > 1)
            throw new InvalidDataException("Invalid file decision.");
        return (new Guid(packet[..16]), packet[16] == 1);
    }

    internal static byte[] Id(Guid id) => id.ToByteArray();
    internal static Guid ParseId(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != 16) throw new InvalidDataException("Invalid transfer ID.");
        return new Guid(packet);
    }

    internal static byte[] Block(Guid id, long position, ReadOnlySpan<byte> data)
    {
        if (position < 0 || data.Length is < 1 or > BlockSize)
            throw new InvalidDataException("Invalid file block.");
        var output = new byte[24 + data.Length];
        id.TryWriteBytes(output.AsSpan(0, 16));
        BinaryPrimitives.WriteInt64BigEndian(output.AsSpan(16, 8), position);
        data.CopyTo(output.AsSpan(24));
        return output;
    }

    internal static (Guid Id, long Offset, ReadOnlyMemory<byte> Data) ParseBlock(byte[] packet)
    {
        if (packet.Length < 25 || packet.Length > 24 + BlockSize)
            throw new InvalidDataException("Invalid file block length.");
        long offset = BinaryPrimitives.ReadInt64BigEndian(packet.AsSpan(16, 8));
        if (offset < 0) throw new InvalidDataException("Negative file block offset.");
        return (new Guid(packet.AsSpan(0, 16)), offset, packet.AsMemory(24));
    }
}

internal sealed class LabFileTransferPeer : IDisposable
{
    private sealed class Outgoing
    {
        internal required LabFileOffer Offer;
        internal required FileStream Stream;
        internal bool Started, Cancelled;
    }
    private sealed class Incoming
    {
        internal required LabFileOffer Offer;
        internal required FileStream Stream;
        internal required string TemporaryPath, Destination;
        internal readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        internal long Received;
    }

    private readonly SslStream _tls;
    private readonly SemaphoreSlim _writer;
    private readonly CancellationToken _token;
    private readonly Func<bool> _authorized;
    private readonly Func<LabFileOffer, Task<string?>> _chooseDestination;
    private readonly Func<Task<string?>>? _chooseSource;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Outgoing? _outgoing;
    private Incoming? _incoming;
    private bool _disposed;

    internal LabFileTransferPeer(SslStream tls, SemaphoreSlim writer,
        CancellationToken token, Func<bool> authorized,
        Func<LabFileOffer, Task<string?>> chooseDestination,
        Func<Task<string?>>? chooseSource, Action<string> log)
    {
        _tls = tls; _writer = writer; _token = token;
        _authorized = authorized; _chooseDestination = chooseDestination;
        _chooseSource = chooseSource; _log = log;
    }

    private void DemandPermission()
    {
        if (_disposed || _token.IsCancellationRequested || !_authorized())
            throw new AuthenticationException("File transfer requires an authenticated controller on a normal desktop.");
    }

    private Task SendAsync(LabMessage type, byte[] body) =>
        LabWire.WriteAsync(_tls, _writer, type, body, _token);

    internal Task RequestDownloadAsync()
    {
        DemandPermission();
        if (_chooseSource is not null)
            throw new InvalidOperationException("Only Viewer can request a Server file.");
        return SendAsync(LabMessage.FileRequest, Array.Empty<byte>());
    }

    internal async Task OfferFileAsync(string path)
    {
        DemandPermission();
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        Outgoing? transfer = null;
        try
        {
            if (file.Length < 0 || file.Length > LabFileTransferWire.MaxFileBytes)
                throw new InvalidDataException("File exceeds preview 512 MiB transfer limit.");
            var name = Path.GetFileName(file.Name);
            if (!LabFileTransferWire.SafeFileName(name))
                throw new InvalidDataException("Filename cannot be sent safely.");
            byte[] sha = await SHA256.HashDataAsync(file, _token);
            file.Position = 0;
            transfer = new Outgoing {
                Offer = new LabFileOffer(Guid.NewGuid(), name, file.Length, sha),
                Stream = file
            };
            lock (_gate)
            {
                DemandPermission();
                if (_outgoing is not null)
                    throw new InvalidOperationException("Another file send is in progress.");
                _outgoing = transfer;
            }
            await SendAsync(LabMessage.FileOffer,
                LabFileTransferWire.Offer(transfer.Offer));
            _log("File offered: " + name + " (awaiting destination approval)");
        }
        catch
        {
            lock (_gate) { if (ReferenceEquals(_outgoing, transfer)) _outgoing = null; }
            file.Dispose();
            throw;
        }
    }

    internal async Task HandleAsync(LabPacket packet)
    {
        if (!LabFileTransferWire.IsFilePacket(packet.Type))
            throw new InvalidDataException("Not a file transfer packet.");
        DemandPermission();
        switch (packet.Type)
        {
            case LabMessage.FileRequest:
                if (packet.Payload.Length != 0 || _chooseSource is null)
                    throw new InvalidDataException("Unexpected file request.");
                var source = await _chooseSource();
                if (source is not null) await OfferFileAsync(source);
                break;
            case LabMessage.FileOffer:
                await AcceptOfferAsync(LabFileTransferWire.ParseOffer(packet.Payload));
                break;
            case LabMessage.FileDecision:
                var answer = LabFileTransferWire.ParseDecision(packet.Payload);
                OnDecision(answer.Id, answer.Accepted);
                break;
            case LabMessage.FileBlock:
                await ReceiveBlockAsync(packet.Payload);
                break;
            case LabMessage.FileDone:
                await FinishReceiveAsync(LabFileTransferWire.ParseId(packet.Payload));
                break;
            case LabMessage.FileCancel:
                CancelTransfer(LabFileTransferWire.ParseId(packet.Payload));
                break;
            default:
                throw new InvalidDataException("Unknown transfer message.");
        }
    }

    private async Task AcceptOfferAsync(LabFileOffer offer)
    {
        lock (_gate)
        {
            if (_incoming is not null)
                throw new InvalidOperationException("Another receive is in progress.");
        }
        string? dest = await _chooseDestination(offer);
        if (dest is null)
        {
            await SendAsync(LabMessage.FileDecision, LabFileTransferWire.Decision(offer.Id, false));
            return;
        }
        DemandPermission();
        var target = Path.GetFullPath(dest); // Local chooser, never a remote path.
        var parent = Path.GetDirectoryName(target) ??
            throw new InvalidDataException("Receive folder unavailable.");
        var temp = Path.Combine(parent, ".tjra-" + offer.Id.ToString("N") + ".part");
        FileStream? stream = null;
        try
        {
            stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var incoming = new Incoming {
                Offer = offer, Stream = stream, Destination = target, TemporaryPath = temp
            };
            lock (_gate)
            {
                DemandPermission();
                if (_incoming is not null) throw new InvalidOperationException("Receive already active.");
                _incoming = incoming;
            }
            await SendAsync(LabMessage.FileDecision, LabFileTransferWire.Decision(offer.Id, true));
            _log("Receiving " + offer.FileName + " (up to " + offer.Size + " bytes)");
        }
        catch
        {
            stream?.Dispose();
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    private void OnDecision(Guid id, bool accept)
    {
        Outgoing transfer;
        lock (_gate)
        {
            transfer = _outgoing ?? throw new InvalidDataException("No outstanding file offer.");
            if (transfer.Offer.Id != id || transfer.Started)
                throw new InvalidDataException("File decision did not match offer.");
            if (accept) transfer.Started = true;
            else _outgoing = null;
        }
        if (!accept)
        {
            transfer.Stream.Dispose();
            _log("File send declined by recipient.");
            return;
        }
        _ = Task.Run(() => PumpOutgoingAsync(transfer), _token);
    }

    private async Task PumpOutgoingAsync(Outgoing transfer)
    {
        try
        {
            var buffer = new byte[LabFileTransferWire.BlockSize];
            long offset = 0;
            int read;
            while ((read = await transfer.Stream.ReadAsync(buffer, _token)) > 0)
            {
                DemandPermission();
                if (transfer.Cancelled)
                    throw new OperationCanceledException("Transfer cancelled.");
                await SendAsync(LabMessage.FileBlock,
                    LabFileTransferWire.Block(transfer.Offer.Id, offset, buffer.AsSpan(0, read)));
                offset += read;
            }
            if (offset != transfer.Offer.Size)
                throw new IOException("Source file changed during transfer.");
            DemandPermission();
            await SendAsync(LabMessage.FileDone, LabFileTransferWire.Id(transfer.Offer.Id));
            _log("File sent: " + transfer.Offer.FileName);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException
            or AuthenticationException or ObjectDisposedException)
        {
            _log("File send ended: " + ex.Message);
            try { if (!_token.IsCancellationRequested)
                await SendAsync(LabMessage.FileCancel, LabFileTransferWire.Id(transfer.Offer.Id)); }
            catch (Exception) { /* disconnected */ }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_outgoing, transfer)) _outgoing = null; }
            transfer.Stream.Dispose();
        }
    }

    private async Task ReceiveBlockAsync(byte[] payload)
    {
        var part = LabFileTransferWire.ParseBlock(payload);
        var incoming = _incoming ?? throw new InvalidDataException("No approved file destination.");
        if (part.Id != incoming.Offer.Id || part.Offset != incoming.Received ||
            incoming.Received + part.Data.Length > incoming.Offer.Size)
            throw new InvalidDataException("File data exceeds offer or arrived out of order.");
        DemandPermission();
        await incoming.Stream.WriteAsync(part.Data, _token);
        incoming.Hash.AppendData(part.Data.Span);
        incoming.Received += part.Data.Length;
    }

    private async Task FinishReceiveAsync(Guid id)
    {
        var incoming = _incoming ?? throw new InvalidDataException("No file to finish.");
        if (id != incoming.Offer.Id || incoming.Received != incoming.Offer.Size)
            throw new InvalidDataException("File incomplete or transfer ID mismatch.");
        await incoming.Stream.FlushAsync(_token);
        incoming.Stream.Dispose();
        byte[] actual = incoming.Hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actual, incoming.Offer.Sha256))
            throw new InvalidDataException("Received file SHA-256 mismatch.");
        DemandPermission();
        File.Move(incoming.TemporaryPath, incoming.Destination, overwrite: true);
        lock (_gate) { if (ReferenceEquals(_incoming, incoming)) _incoming = null; }
        incoming.Hash.Dispose();
        _log("Received and SHA-256 verified: " + incoming.Offer.FileName);
    }

    private void CancelTransfer(Guid id)
    {
        lock (_gate)
        {
            if (_outgoing?.Offer.Id == id)
            {
                _outgoing.Cancelled = true;
                _outgoing.Stream.Dispose();
                _outgoing = null;
            }
            if (_incoming?.Offer.Id == id)
            {
                _incoming.Stream.Dispose();
                _incoming.Hash.Dispose();
                try { File.Delete(_incoming.TemporaryPath); } catch (IOException) { }
                _incoming = null;
            }
        }
        _log("Transfer cancelled.");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _outgoing?.Stream.Dispose();
            _outgoing = null;
            if (_incoming is { } incoming)
            {
                incoming.Stream.Dispose();
                incoming.Hash.Dispose();
                try { File.Delete(incoming.TemporaryPath); } catch (IOException) { }
                _incoming = null;
            }
        }
    }
}

internal static class LabFileTransferDialogs
{
    private static Task<string?> OnUiAsync(Form owner, Func<string?> prompt)
    {
        var result = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void Invoke()
        {
            try
            {
                if (owner.IsDisposed) { result.TrySetResult(null); return; }
                // The Server may be hidden in the tray. Make the receive
                // approval visible locally before accepting any file.
                if (!owner.Visible)
                {
                    owner.ShowInTaskbar = true;
                    owner.Show();
                }
                if (owner.WindowState == FormWindowState.Minimized)
                    owner.WindowState = FormWindowState.Normal;
                owner.Activate();
                result.TrySetResult(prompt());
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }
        try
        {
            if (owner.IsDisposed || !owner.IsHandleCreated) result.TrySetResult(null);
            else if (owner.InvokeRequired) owner.BeginInvoke(new Action(Invoke));
            else Invoke();
        }
        catch (InvalidOperationException) { result.TrySetResult(null); }
        return result.Task;
    }

    internal static Task<string?> DestinationAsync(Form owner, LabFileOffer offer) =>
        OnUiAsync(owner, () => {
            using var picker = new SaveFileDialog {
                Title = "Approve file from authenticated TJRA controller (" +
                        offer.Size + " bytes)",
                FileName = offer.FileName, OverwritePrompt = true,
                AddExtension = false
            };
            return picker.ShowDialog(owner) == DialogResult.OK ? picker.FileName : null;
        });

    internal static Task<string?> ServerFileAsync(Form owner) =>
        OnUiAsync(owner, () => {
            using var picker = new OpenFileDialog {
                Title = "Approve a file for download by the authenticated Viewer",
                CheckFileExists = true, Multiselect = false
            };
            return picker.ShowDialog(owner) == DialogResult.OK ? picker.FileName : null;
        });
}
