// SPDX-License-Identifier: GPL-2.0-or-later
// Bounded remote file manager protocol. Never accepts absolute paths or follows junctions.
// All remote paths are relative to the current-user TJ.RemoteAccess/Received folder.
using System.Text;
using System.Text.Json;

namespace TJ.RemoteAccess.Lab;

internal sealed record LabRemoteFileItem(string Name, bool IsDirectory, long Size, DateTime ModifiedUtc);
internal sealed record LabRemoteFileCommand(Guid Id, string Action, string Path = "",
    string NewName = "", int PageOffset = 0);
internal sealed record LabRemoteFileReply(Guid Id, bool Success, string Path,
    LabRemoteFileItem[] Items, string Error = "", int NextOffset = 0,
    bool HasMore = false, int TotalCount = 0);

internal static class LabRemoteFileWire
{
    internal const int MaxEntries = 160;
    internal const int MaxFolderEntries = 10000;
    internal const int MaxBytes = 64 * 1024;
    internal const int MaxRelativePathBytes = 720;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    // Internal, unfinished content is never remotely browsable, downloadable,
    // renamed, copied or deleted, even by an authenticated Controller.
    internal static bool IsInternalStage(string name) =>
        name.StartsWith(".tjra-", StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase);

    internal static string CheckRelative(string? path, bool allowEmpty = true)
    {
        if (path is null) throw new InvalidDataException("Null remote path is forbidden.");
        if (path.Length == 0)
        {
            if (allowEmpty) return "";
            throw new InvalidDataException("Select a remote file or folder.");
        }
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.EndsWith('/') || Utf8.GetByteCount(path) > MaxRelativePathBytes)
            throw new InvalidDataException("Unsafe remote path.");
        string[] parts = path.Split('/');
        if (parts.Length > 8)
            throw new InvalidDataException("Remote folder depth exceeds limit.");
        foreach (var part in parts)
        {
            LabFileTransfer.SafeFileName(part);
            if (IsInternalStage(part))
                throw new InvalidDataException("Transfer staging files are not shared.");
        }
        return string.Join("/", parts);
    }

    internal static LabRemoteFileCommand CheckCommand(LabRemoteFileCommand command)
    {
        if (command.Id == Guid.Empty ||
            command.Action is not ("List" or "CreateFolder" or "Rename" or "Delete" or "Copy") ||
            command.PageOffset < 0 || command.PageOffset > MaxFolderEntries ||
            (command.PageOffset != 0 && command.Action != "List"))
            throw new InvalidDataException("Unknown remote file operation or invalid page.");
        CheckRelative(command.Path, allowEmpty: command.Action is "List" or "CreateFolder");
        if (command.Action is "Rename" or "CreateFolder")
        {
            LabFileTransfer.SafeFileName(command.NewName);
            if (IsInternalStage(command.NewName))
                throw new InvalidDataException("Transfer staging filenames are reserved.");
        }
        else if (command.Action == "Copy")
            CheckRelative(command.NewName); // Destination folder, not an absolute path.
        else if (!string.IsNullOrEmpty(command.NewName))
            throw new InvalidDataException("Unexpected remote file argument.");
        return command;
    }

    internal static byte[] EncodeCommand(LabRemoteFileCommand command) =>
        Pack(CheckCommand(command));

    internal static LabRemoteFileCommand DecodeCommand(ReadOnlySpan<byte> payload) =>
        CheckCommand(Unpack<LabRemoteFileCommand>(payload));

    // Older 16-byte request prompts Server owner to choose any file explicitly.
    // Extended request is confined to an authorized Received relative path.
    internal static byte[] DownloadRequest(Guid id, string relativeFile)
    {
        if (id == Guid.Empty) throw new InvalidDataException("Invalid download identity.");
        CheckRelative(relativeFile, allowEmpty: false);
        byte[] name = Utf8.GetBytes(relativeFile);
        var bytes = new byte[16 + name.Length];
        id.TryWriteBytes(bytes.AsSpan(0, 16));
        name.CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    internal static (Guid Id, string RelativeFile) ParseDownloadRequest(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16 || bytes.Length > 16 + MaxRelativePathBytes)
            throw new InvalidDataException("Invalid remote download request.");
        Guid id = new(bytes[..16]);
        if (id == Guid.Empty) throw new InvalidDataException("Empty remote download identity.");
        string relative = bytes.Length == 16 ? "" : Utf8.GetString(bytes[16..]);
        return (id, CheckRelative(relative, allowEmpty: true));
    }

    internal static byte[] EncodeReply(LabRemoteFileReply reply)
    {
        if (reply.Path is null || reply.Items is null || reply.Error is null ||
            reply.Items.Any(item => item is null || item.Name is null) ||
            reply.Id == Guid.Empty || reply.Items.Length > MaxEntries ||
            reply.Error.Length > 160 || reply.NextOffset < 0 ||
            reply.NextOffset > MaxFolderEntries ||
            reply.TotalCount < 0 || reply.TotalCount > MaxFolderEntries ||
            (reply.HasMore && (reply.Items.Length == 0 || reply.NextOffset == 0)))
            throw new InvalidDataException("Remote file result too large.");
        CheckRelative(reply.Path);
        foreach (var item in reply.Items)
        {
            LabFileTransfer.SafeFileName(item.Name);
            if (item.Size < 0) throw new InvalidDataException("Invalid remote file length.");
        }
        return Pack(reply);
    }

    internal static LabRemoteFileReply DecodeReply(ReadOnlySpan<byte> payload)
    {
        var reply = Unpack<LabRemoteFileReply>(payload);
        // Reuse the serializer side's strict checks; reject hostile response fields.
        EncodeReply(reply);
        return reply;
    }

    private static byte[] Pack<T>(T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        if (payload.Length is < 2 or > MaxBytes)
            throw new InvalidDataException("Remote file packet too large.");
        return payload;
    }

    private static T Unpack<T>(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 2 or > MaxBytes)
            throw new InvalidDataException("Invalid remote file packet size.");
        try { return JsonSerializer.Deserialize<T>(payload) ??
            throw new InvalidDataException("Empty remote file packet."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid remote file JSON.", ex); }
    }
}

internal sealed class LabRemoteFileAccessPolicy
{
    private readonly LabServerSettings _settings;
    internal readonly string Root;

    internal LabRemoteFileAccessPolicy(LabServerSettings settings)
    {
        _settings = settings;
        Root = ValidateRoot(settings.RemoteFileRoot);
    }

    internal bool RootUnchanged
    {
        get
        {
            try
            {
                return string.Equals(Root, ValidateRoot(_settings.RemoteFileRoot),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException)
            {
                return false; // Invalid or removed share is an immediate revocation.
            }
        }
    }
    internal bool Browse => RootUnchanged && _settings.AllowRemoteFileBrowse;
    internal bool Upload => RootUnchanged && _settings.AllowRemoteFileUpload;
    internal bool Download => RootUnchanged && _settings.AllowRemoteFileDownload;
    internal bool Changes => Browse && _settings.AllowRemoteFileChanges;

    internal static string ValidateRoot(string? configured)
    {
        string input = string.IsNullOrWhiteSpace(configured) ?
            LabFileReceiver.ServerReceivedDirectory : configured;
        if (!Path.IsPathFullyQualified(input) || input.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Remote file share must be an absolute local folder.");
        string full = Path.GetFullPath(input);
        if (string.Equals(Path.TrimEndingDirectorySeparator(full),
            Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? ""),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sharing a drive root is forbidden.");
        if (!string.IsNullOrWhiteSpace(configured) && !Directory.Exists(full))
            throw new InvalidDataException("Chosen remote file share folder does not exist.");
        string? current = full;
        while (current is not null && Directory.Exists(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked folders cannot be shared remotely.");
            current = Directory.GetParent(current)?.FullName;
        }
        return full;
    }
}

internal sealed class LabRemoteFileStore
{
    private readonly string _root;
    internal string Root => _root;
    internal LabRemoteFileStore(string root)
    {
        _root = Path.GetFullPath(root);
        if (File.Exists(_root)) throw new InvalidDataException("Remote storage root is not a folder.");
    }

    internal static LabRemoteFileStore ForCurrentUser() =>
        new(LabFileReceiver.ServerReceivedDirectory);

    // Path confinement is checked before EVERY request. Reparse paths are
    // rejected even if they appear under the allowed root. All mutations
    // run in the normal signed-in user's Windows account, never LocalSystem.
    private string Resolve(string relative, bool allowMissingLast = false)
    {
        LabRemoteFileWire.CheckRelative(relative);
        if (Directory.Exists(_root))
        {
            if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Remote storage root is redirected.");
        }
        else Directory.CreateDirectory(_root);
        string rootWithSep = Path.EndsInDirectorySeparator(_root) ? _root :
            _root + Path.DirectorySeparatorChar;
        string destination = _root;
        string[] parts = relative.Length == 0 ? Array.Empty<string>() : relative.Split('/');
        for (int i = 0; i < parts.Length; i++)
        {
            destination = Path.GetFullPath(Path.Combine(destination, parts[i]));
            if (!destination.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Remote path escapes allowed root.");
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked remote storage entry is not accessible.");
            }
            else if (i != parts.Length - 1 || !allowMissingLast)
                throw new FileNotFoundException("Requested remote entry does not exist.");
        }
        return destination;
    }

    internal string SelectedFile(string relative)
    {
        LabRemoteFileWire.CheckRelative(relative, false);
        string path = Resolve(relative);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.Directory) != 0)
            throw new InvalidDataException("Remote file not found.");
        var file = new FileInfo(path);
        if (file.Length > LabFileTransfer.MaxFileBytes)
            throw new InvalidDataException("Remote file exceeds 4 GiB.");
        return path;
    }

    internal string UploadFolder(string relative)
    {
        string path = Resolve(relative);
        if (!Directory.Exists(path)) throw new InvalidDataException("Remote upload folder missing.");
        return path;
    }

    internal LabRemoteFileReply List(Guid id, string relative, int pageOffset = 0)
    {
        string folder = Resolve(LabRemoteFileWire.CheckRelative(relative));
        if (!Directory.Exists(folder)) throw new InvalidDataException("Remote folder missing.");
        if (pageOffset < 0 || pageOffset > LabRemoteFileWire.MaxFolderEntries)
            throw new InvalidDataException("Invalid remote page offset.");
        var list = new List<LabRemoteFileItem>();
        foreach (string path in Directory.EnumerateFileSystemEntries(folder)
            .Take(LabRemoteFileWire.MaxFolderEntries + 1))
        {
            if (list.Count >= LabRemoteFileWire.MaxFolderEntries)
                throw new InvalidDataException("Remote folder has more than 10000 entries.");
            var info = new FileInfo(path);
            if (LabRemoteFileWire.IsInternalStage(info.Name)) continue;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Folder contains a linked entry; listing refused.");
            LabFileTransfer.SafeFileName(info.Name);
            bool directory = (info.Attributes & FileAttributes.Directory) != 0;
            list.Add(new LabRemoteFileItem(info.Name, directory, directory ? 0 : info.Length,
                info.LastWriteTimeUtc));
        }
        var entries = list.OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name,
            StringComparer.OrdinalIgnoreCase).ToArray();
        if (pageOffset > entries.Length)
            throw new InvalidDataException("Remote page offset exceeds folder entries.");
        var page = entries.Skip(pageOffset).Take(LabRemoteFileWire.MaxEntries).ToArray();
        int nextOffset = pageOffset + page.Length;
        bool hasMore = nextOffset < entries.Length;
        return new LabRemoteFileReply(id, true, relative, page, "",
            hasMore ? nextOffset : 0, hasMore, entries.Length);
    }

    internal LabRemoteFileReply Mutate(LabRemoteFileCommand command,
        CancellationToken stop = default, Func<bool>? permitted = null)
    {
        LabRemoteFileWire.CheckCommand(command);
        stop.ThrowIfCancellationRequested();
        if (permitted is not null && !permitted())
            throw new UnauthorizedAccessException("Remote operation permission revoked.");
        if (command.Action == "List") throw new InvalidDataException("Use List for browsing.");
        string path = Resolve(command.Path, allowMissingLast: command.Action == "CreateFolder");
        switch (command.Action)
        {
            case "CreateFolder":
                if (!Directory.Exists(path)) throw new InvalidDataException("Parent folder missing.");
                if (List(command.Id, command.Path).TotalCount >= LabRemoteFileWire.MaxFolderEntries)
                    throw new InvalidDataException("Remote folder is full (160 entries); create denied.");
                string child = Resolve(command.Path.Length == 0 ? command.NewName :
                    command.Path + "/" + command.NewName, allowMissingLast: true);
                if (Directory.Exists(child) || File.Exists(child))
                    throw new IOException("A file or folder already has that name.");
                Directory.CreateDirectory(child);
                break;
            case "Copy":
                // Local copy within the operator-approved root. No directory
                // recursion, link following, overwrite or arbitrary destination.
                if (!File.Exists(path))
                    throw new InvalidDataException("Only a single remote file can be copied.");
                string copyFolder = Resolve(command.NewName);
                if (!Directory.Exists(copyFolder))
                    throw new InvalidDataException("Copy destination folder is missing.");
                if (List(command.Id, command.NewName).TotalCount >= LabRemoteFileWire.MaxFolderEntries)
                    throw new InvalidDataException("Copy destination folder is full.");
                string sourceName = Path.GetFileName(path);
                string stem = Path.GetFileNameWithoutExtension(sourceName);
                string ext = Path.GetExtension(sourceName);
                string copyName = sourceName;
                string copyTarget = Resolve(command.NewName.Length == 0 ? copyName :
                    command.NewName + "/" + copyName, allowMissingLast: true);
                int copyIndex = 1;
                while (File.Exists(copyTarget) || Directory.Exists(copyTarget))
                {
                    if (copyIndex > 1000)
                        throw new IOException("Too many remote copies with the same name.");
                    copyName = LabFileTransfer.SafeFileName(
                        stem + " (copy " + copyIndex++ + ")" + ext);
                    copyTarget = Resolve(command.NewName.Length == 0 ? copyName :
                        command.NewName + "/" + copyName, allowMissingLast: true);
                }
                if (new FileInfo(path).Length > LabFileTransfer.MaxFileBytes)
                    throw new InvalidDataException("Remote copy exceeds the 4 GiB file limit.");
                // Write to an exclusive temporary file and rename only after
                // the copy succeeds, so a failed copy never looks complete.
                string partial = Path.Combine(copyFolder,
                    ".tjra-copy-" + Guid.NewGuid().ToString("N") + ".partial");
                try
                {
                    using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                    using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 64 * 1024, FileOptions.WriteThrough))
                    {
                        var buffer = new byte[64 * 1024];
                        int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            stop.ThrowIfCancellationRequested();
                            if (permitted is not null && !permitted())
                                throw new UnauthorizedAccessException("Remote copy permission revoked.");
                            output.Write(buffer, 0, count);
                        }
                        output.Flush(true);
                    }
                    stop.ThrowIfCancellationRequested();
                    if (permitted is not null && !permitted())
                        throw new UnauthorizedAccessException("Remote copy permission revoked.");
                    File.Move(partial, copyTarget, overwrite: false);
                }
                finally
                {
                    if (File.Exists(partial)) File.Delete(partial);
                }
                break;
            case "Rename":
                string parent = command.Path.Contains('/') ? command.Path[..command.Path.LastIndexOf('/')] : "";
                string target = Resolve(parent.Length == 0 ? command.NewName : parent + "/" + command.NewName,
                    allowMissingLast: true);
                if (Directory.Exists(target) || File.Exists(target))
                    throw new IOException("A file or folder already has that name.");
                if (Directory.Exists(path)) Directory.Move(path, target);
                else if (File.Exists(path)) File.Move(path, target);
                else throw new FileNotFoundException("Remote entry not found.");
                break;
            case "Delete":
                if (command.Path.Length == 0) throw new InvalidDataException("Cannot delete shared root.");
                // A remote delete may only remove ONE file or EMPTY folder, never an entire tree.
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
                else throw new FileNotFoundException("Remote entry not found.");
                break;
        }
        string browseParent = command.Action == "Copy" ? command.NewName :
            command.Action == "CreateFolder" ? command.Path :
            command.Path.Contains('/') ? command.Path[..command.Path.LastIndexOf('/')] : "";
        return List(command.Id, browseParent);
    }
}
