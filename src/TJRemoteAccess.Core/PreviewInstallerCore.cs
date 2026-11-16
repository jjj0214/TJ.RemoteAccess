// SPDX-License-Identifier: GPL-2.0-or-later
// Standalone current-user offline ZIP installer. The GUI never shells out to
// PowerShell, never changes a Windows Service, firewall, TightVNC or UAC.
// Every package byte and entry is SHA-256 checked before any installed folder
// is replaced. Changes are directory-atomic per component with rollback.
using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TJ.RemoteAccess.Installer;

internal enum PreviewInstallMode { Both, Server, Viewer }
internal sealed record PreviewInstallProgress(int Percent, string Phase, string Detail);

internal sealed class PreviewPackageManifest
{
    public int SchemaVersion { get; set; }
    public string Artifact { get; set; } = "";
    public string Version { get; set; } = "";
    public string RuntimeIdentifier { get; set; } = "";
    public string SourceRevision { get; set; } = "";
    public bool Unsigned { get; set; }
    public PreviewPackageFile[]? Files { get; set; }
}
internal sealed class PreviewPackageFile
{
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class PreviewPackageInstaller
{
    internal const long MaxTotalUncompressedBytes = 1_610_612_736L;
    internal const long MaxEntryBytes = 536_870_912L;
    internal const int MaxEntries = 4096;
    private const string InstallFolder = @"Programs\TJ.RemoteAccess";
    private const string MenuFolder = @"Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Preview";
    private static readonly Regex SafePath = new(
        @"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HashPattern = new(
        "^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SourceRevisionPattern = new(
        "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> AllowedRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "LICENSE", "manifest.json", "source.zip",
        "Install-TJRemoteAccess-Preview-Offline.ps1",
        "Uninstall-TJRemoteAccess-Preview.ps1",
        "Test-TJRemoteAccess-Preview.ps1",
        "Measure-TJRemoteAccess-Preview.ps1",
        "Set-TJRemoteAccess-Preview-Startup.ps1",
        "Get-TJRemoteAccessFileSha256.ps1"
    };

    private readonly string _installRoot;
    private readonly string _startMenu;
    internal string InstallRoot => _installRoot;

    internal PreviewPackageInstaller(string? installRoot = null, string? startMenu = null)
    {
        _installRoot = installRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            InstallFolder);
        _startMenu = startMenu ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            MenuFolder);
    }

    internal static string[] Components(PreviewInstallMode mode) =>
        mode switch
        {
            PreviewInstallMode.Server => ["Server", "Setup"],
            PreviewInstallMode.Viewer => ["Viewer", "Setup"],
            PreviewInstallMode.Both => ["Server", "Viewer", "Setup"],
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    internal static string? ReadSidecarHash(string packagePath)
    {
        string sidecar = packagePath + ".sha256";
        if (!File.Exists(sidecar)) return null;
        string[] tokens = File.ReadAllText(sidecar).Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2 || !IsSha256(tokens[0]) ||
            !string.Equals(tokens[1], Path.GetFileName(packagePath),
                           StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Invalid .sha256 sidecar. Do not install.");
        return tokens[0].ToUpperInvariant();
    }

    internal static bool IsSha256(string? value) => value is not null &&
        HashPattern.IsMatch(value);

    internal static bool SafeArchiveEntry(string name)
    {
        if (!SafePath.IsMatch(name) || name.Contains('\\') || name.Contains(':') ||
            name.Split('/').Any(x => x is "." or "..")) return false;
        int slash = name.IndexOf('/');
        return slash < 0
            ? AllowedRoot.Contains(name)
            : name[..slash] is "Server" or "Viewer" or "Setup";
    }

    internal static bool RequiresExistingProcessShutdown(IEnumerable<string> processNames)
    {
        var names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);
        return names.Overlaps(["TJ.RemoteAccess.Server", "TJ.RemoteAccess.Viewer",
                               "TJ.RemoteAccess.Setup", "TJ.RemoteAccess.M3.Agent",
                               "TJ.RemoteAccess.M3.ControlService"]);
    }

    internal static void EnsureNotRunning()
    {
        foreach (var name in new[] {
            "TJ.RemoteAccess.Server", "TJ.RemoteAccess.Viewer", "TJ.RemoteAccess.Setup",
            "TJ.RemoteAccess.M3.Agent", "TJ.RemoteAccess.M3.ControlService"
        })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0)
                {
                    var ids = string.Join(", ", processes.Select(x => x.Id));
                    throw new InvalidOperationException(
                        name + " is running (PID: " + ids + "). Exit it first. " +
                        "Server X hides to tray; right-click its icon > Exit TJ.RemoteAccess.");
                }
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
    }

    private static string ComputeArchiveHash(Stream stream,
        IProgress<PreviewInstallProgress>? progress, CancellationToken stop)
    {
        stream.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long total = stream.Length;
            int read;
            int lastPercent = -1;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                stop.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                int percent = (int)(5 + 20 * stream.Position / Math.Max(1, total));
                if (percent != lastPercent)
                {
                    progress?.Report(new(percent, "Checking package SHA-256",
                        stream.Position.ToString("N0") + " / " + total.ToString("N0") + " bytes"));
                    lastPercent = percent;
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static PreviewPackageManifest ValidateManifest(ZipArchive zip,
        out List<(ZipArchiveEntry Entry, PreviewPackageFile Manifest)> payload)
    {
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("Too many ZIP entries.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in zip.Entries)
        {
            var name = item.FullName;
            if (name.EndsWith('/'))
            {
                var trimmed = name.TrimEnd('/');
                if (trimmed.Split('/').Any(x => x is "." or "..") ||
                    !SafePath.IsMatch(trimmed) ||
                    trimmed.Split('/')[0] is not ("Server" or "Viewer" or "Setup"))
                    throw new InvalidDataException("Unsafe ZIP directory: " + name);
                continue;
            }
            if (!SafeArchiveEntry(name) || !entries.TryAdd(name, item))
                throw new InvalidDataException("Unexpected/duplicate ZIP file: " + name);
            if ((((uint)item.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP symlink rejected: " + name);
            if (item.Length < 0 || item.Length > MaxEntryBytes ||
                total > MaxTotalUncompressedBytes - item.Length)
                throw new InvalidDataException("ZIP expanded size limit exceeded.");
            total += item.Length;
        }
        if (!entries.TryGetValue("manifest.json", out var manifestEntry) ||
            manifestEntry.Length > 1024 * 1024)
            throw new InvalidDataException("Missing or oversized manifest.json.");
        PreviewPackageManifest manifest;
        using (var stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<PreviewPackageManifest>(stream)
                ?? throw new InvalidDataException("Empty package manifest.");
        }
        if (manifest.SchemaVersion != 1 ||
            manifest.Artifact != "TJ.RemoteAccess.Offline.Preview" ||
            manifest.RuntimeIdentifier != "win-x64" || !manifest.Unsigned ||
            !SourceRevisionPattern.IsMatch(manifest.SourceRevision ?? "") ||
            manifest.Files is null ||
            manifest.Files.Length != entries.Count - 1)
            throw new InvalidDataException("Unsupported Preview package manifest.");
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        payload = new();
        foreach (var row in manifest.Files)
        {
            if (row is null || !SafeArchiveEntry(row.Path) ||
                !IsSha256(row.Sha256) ||
                !entries.TryGetValue(row.Path, out var entry) ||
                entry.Length != row.Bytes || !referenced.Add(row.Path) ||
                row.Path.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid manifest file entry.");
            payload.Add((entry, row));
        }
        foreach (var pair in entries)
            if (!pair.Key.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) &&
                !referenced.Contains(pair.Key))
                throw new InvalidDataException("Unmanifested ZIP content: " + pair.Key);
        foreach (var component in new[] { "Server", "Viewer", "Setup" })
            if (!referenced.Contains(component + "/TJ.RemoteAccess." + component + ".exe"))
                throw new InvalidDataException("Missing " + component + " executable.");
        return manifest;
    }

    internal void Install(string packagePath, string expectedSha,
                          PreviewInstallMode mode,
                          IProgress<PreviewInstallProgress>? progress,
                          CancellationToken stop)
    {
        if (!File.Exists(packagePath) || !packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Choose the Preview offline .zip package.", packagePath);
        if (!IsSha256(expectedSha))
            throw new InvalidDataException("A 64-character trusted package SHA-256 is required.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows only.");
        string[] selected = Components(mode);
        EnsureNotRunning();
        progress?.Report(new(2, "Checking existing programs", "No running Server/Viewer/Setup found."));
        // Keep ONE read-only file handle open from the archive SHA-256 check
        // through parsing and every payload SHA-256 check. A different ZIP
        // cannot be swapped into the filesystem between two opens.
        using var packageStream = new FileStream(packagePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        string actual = ComputeArchiveHash(packageStream, progress, stop);
        if (!CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual), Convert.FromHexString(expectedSha)))
            throw new InvalidDataException("Package SHA-256 mismatch. NO files changed.");

        packageStream.Position = 0;
        using var zip = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = ValidateManifest(zip, out var files);
        progress?.Report(new(30, "Manifest verified",
            "Version " + manifest.Version + " / source " + manifest.SourceRevision[..12]));

        Directory.CreateDirectory(_installRoot);
        string transaction = Guid.NewGuid().ToString("N");
        string stage = Path.Combine(_installRoot, ".stage-" + transaction);
        var replaced = new List<(string Target, string Backup, bool HadOriginal)>();
        try
        {
            Directory.CreateDirectory(stage);
            long total = files.Sum(x => x.Entry.Length);
            long processed = 0;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int lastPercent = -1;
                foreach (var file in files)
                {
                    stop.ThrowIfCancellationRequested();
                    string name = file.Entry.FullName;
                    bool extract = selected.Any(s =>
                        name.StartsWith(s + "/", StringComparison.Ordinal));
                    string? destination = null;
                    if (extract)
                    {
                        destination = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    }
                    using var input = file.Entry.Open();
                    using var output = extract
                        ? new FileStream(destination!, FileMode.CreateNew, FileAccess.Write,
                                         FileShare.None, 128 * 1024)
                        : null;
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long itemBytes = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
                    {
                        stop.ThrowIfCancellationRequested();
                        itemBytes += read;
                        if (itemBytes > file.Entry.Length || itemBytes > MaxEntryBytes)
                            throw new InvalidDataException("Expanded ZIP exceeds entry size limit.");
                        hasher.AppendData(buffer, 0, read);
                        output?.Write(buffer, 0, read);
                        processed += read;
                        int percent = (int)(32 + 51 * processed / Math.Max(1, total));
                        if (percent != lastPercent)
                        {
                            progress?.Report(new(percent, "Verifying and staging files",
                                name + " (" + processed.ToString("N0") + " / " +
                                total.ToString("N0") + " bytes)"));
                            lastPercent = percent;
                        }
                    }
                    if (itemBytes != file.Entry.Length ||
                        !CryptographicOperations.FixedTimeEquals(
                            hasher.GetHashAndReset(), Convert.FromHexString(file.Manifest.Sha256)))
                        throw new InvalidDataException("Manifest SHA-256 mismatch: " + name);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            // No installed component was touched until the entire package
            // passed integrity verification. Verify process state again.
            stop.ThrowIfCancellationRequested();
            EnsureNotRunning();
            progress?.Report(new(85, "Installing verified components", string.Join(", ", selected)));

            try
            {
                foreach (string component in selected)
                {
                    stop.ThrowIfCancellationRequested();
                    string target = Path.Combine(_installRoot, component);
                    string backup = Path.Combine(_installRoot, component + ".backup-" + transaction);
                    bool previous = Directory.Exists(target);
                    if (previous) Directory.Move(target, backup);
                    replaced.Add((target, backup, previous));
                    Directory.Move(Path.Combine(stage, component), target);
                    if (!File.Exists(Path.Combine(target, "TJ.RemoteAccess." + component + ".exe")))
                        throw new IOException("Installed executable missing: " + component);
                }
            }
            catch
            {
                // Includes cancellation during component swap.
                var errors = new List<Exception>();
                for (int i = replaced.Count - 1; i >= 0; --i)
                {
                    var record = replaced[i];
                    try
                    {
                        if (Directory.Exists(record.Target))
                            Directory.Delete(record.Target, recursive: true);
                        if (record.HadOriginal)
                            Directory.Move(record.Backup, record.Target);
                    }
                    catch (Exception error) { errors.Add(error); }
                }
                if (errors.Count != 0)
                    throw new IOException("Install failed and rollback was incomplete. " +
                        "Backups are retained for manual recovery.",
                        new AggregateException(errors));
                throw;
            }
            // After a successful swap, cancellation no longer undoes an
            // installed version. Cleanup/shortcuts are best-effort.
            foreach (var record in replaced)
                try
                {
                    if (record.HadOriginal && Directory.Exists(record.Backup))
                        Directory.Delete(record.Backup, recursive: true);
                }
                catch (IOException) { /* retained backup, not data loss */ }
                catch (UnauthorizedAccessException) { /* retained backup */ }

            progress?.Report(new(95, "Creating Start Menu shortcuts", "Current user only"));
            TryCreateShortcuts(selected);
            progress?.Report(new(100, "Installation complete",
                "Profiles, TLS certificates, TightVNC and Windows services unchanged."));
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); }
            catch (IOException) { /* safe staging directory retained */ }
            catch (UnauthorizedAccessException) { /* safe staging directory retained */ }
        }
    }

    internal void Remove(PreviewInstallMode mode, IProgress<PreviewInstallProgress>? progress,
                         CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows only.");
        EnsureNotRunning();
        var components = Components(mode);
        progress?.Report(new(5, "Removing Preview applications",
            "Existing user certificates/passwords will be preserved."));
        for (int i = 0; i < components.Length; ++i)
        {
            stop.ThrowIfCancellationRequested();
            string path = Path.Combine(_installRoot, components[i]);
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            string shortcut = Path.Combine(_startMenu,
                "TJ.RemoteAccess Preview " + components[i] + ".lnk");
            if (File.Exists(shortcut)) File.Delete(shortcut);
            progress?.Report(new((i + 1) * 90 / components.Length, "Removing Preview applications",
                components[i]));
        }
        progress?.Report(new(100, "Removal complete",
            "Configuration and certificates kept. Remove any scheduled startup task separately."));
    }

    private void TryCreateShortcuts(IEnumerable<string> components)
    {
        try
        {
            Directory.CreateDirectory(_startMenu);
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            object shellObject = Activator.CreateInstance(type)!;
            try
            {
                dynamic shell = shellObject;
                foreach (var part in components)
                {
                    string path = Path.Combine(_startMenu, "TJ.RemoteAccess Preview " + part + ".lnk");
                    string exe = Path.Combine(_installRoot, part, "TJ.RemoteAccess." + part + ".exe");
                    dynamic shortcut = shell.CreateShortcut(path);
                    shortcut.TargetPath = exe;
                    shortcut.WorkingDirectory = Path.GetDirectoryName(exe)!;
                    shortcut.Description = "TJ.RemoteAccess Preview " + part + " (unsigned test)";
                    shortcut.Save();
                    if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
                }
            }
            finally
            {
                if (Marshal.IsComObject(shellObject)) Marshal.FinalReleaseComObject(shellObject);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Binary installation is already complete. Missing shortcuts
            // must not trigger a destructive rollback.
            Debug.WriteLine("Optional shortcut creation failed: " + ex.Message);
        }
    }
}
