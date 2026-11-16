// SPDX-License-Identifier: GPL-2.0-or-later
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TJ.RemoteAccess.Installer;
using Xunit;

public sealed class PreviewInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "TJRA-Installer-Tests-" + Guid.NewGuid().ToString("N"));
    private string InstallRoot => Path.Combine(_root, "installed");
    private string MenuRoot => Path.Combine(_root, "menu");
    private PreviewPackageInstaller Engine => new(InstallRoot, MenuRoot);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (string Path, string Sha) Package(Dictionary<string, byte[]>? overrides = null, bool corruptManifest = false)
    {
        Directory.CreateDirectory(_root);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["Server/TJ.RemoteAccess.Server.exe"] = Encoding.UTF8.GetBytes("new-server-0.5.3"),
            ["Viewer/TJ.RemoteAccess.Viewer.exe"] = Encoding.UTF8.GetBytes("new-viewer-0.5.3"),
            ["Setup/TJ.RemoteAccess.Setup.exe"] = Encoding.UTF8.GetBytes("new-setup-0.5.3"),
            ["LICENSE"] = Encoding.UTF8.GetBytes("GPLv2 test license fixture")
        };
        if (overrides is not null)
            foreach (var (key, bytes) in overrides) files[key] = bytes;
        string location = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(location, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                using var stream = archive.CreateEntry(file.Key).Open();
                stream.Write(file.Value);
            }
            var manifest = new PreviewPackageManifest
            {
                SchemaVersion = 1,
                Artifact = "TJ.RemoteAccess.Offline.Preview",
                Version = "0.5.3-preview",
                SourceRevision = new string('a', 40),
                RuntimeIdentifier = "win-x64",
                Unsigned = true,
                Files = files.Select(file => new PreviewPackageFile {
                    Path = file.Key, Bytes = file.Value.LongLength,
                    Sha256 = corruptManifest && file.Key.StartsWith("Setup/", StringComparison.Ordinal)
                        ? new string('F', 64)
                        : Convert.ToHexString(SHA256.HashData(file.Value))
                }).ToArray()
            };
            using var output = new StreamWriter(archive.CreateEntry("manifest.json").Open(),
                                                new UTF8Encoding(false));
            output.Write(JsonSerializer.Serialize(manifest));
        }
        return (location, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(location))));
    }

    [Theory]
    [InlineData("Server/TJ.RemoteAccess.Server.exe", true)]
    [InlineData("Setup/TJ.RemoteAccess.Setup.exe", true)]
    [InlineData("Viewer/file.dll", true)]
    [InlineData("README.exe", false)]
    [InlineData("Server/../escape.exe", false)]
    [InlineData("Server/./entry.exe", false)]
    [InlineData("/Server/wrong.exe", false)]
    [InlineData("Server\\wrong.exe", false)]
    [InlineData("Server/file:name.exe", false)]
    public void ArchivePathAllowlistBlocksZipSlipAndForeignFiles(string path, bool valid)
    {
        Assert.Equal(valid, PreviewPackageInstaller.SafeArchiveEntry(path));
    }

    [Fact]
    public void SidecarHashMustBeExactlyBoundToSelectedZipFilename()
    {
        var (path, hash) = Package();
        Assert.Null(PreviewPackageInstaller.ReadSidecarHash(path));
        File.WriteAllText(path + ".sha256", hash + "  " + Path.GetFileName(path));
        Assert.Equal(hash, PreviewPackageInstaller.ReadSidecarHash(path));
        File.WriteAllText(path + ".sha256", hash + "  OTHER.zip");
        Assert.Throws<InvalidDataException>(() => PreviewPackageInstaller.ReadSidecarHash(path));
        Assert.False(PreviewPackageInstaller.IsSha256(hash[..63]));
        Assert.True(PreviewPackageInstaller.IsSha256(hash));
    }

    [Fact]
    public void InstallServerModePreservesViewerAndUnrelatedConfiguration()
    {
        var (path, sha) = Package();
        Directory.CreateDirectory(Path.Combine(InstallRoot, "Server"));
        File.WriteAllText(Path.Combine(InstallRoot, "Server", "TJ.RemoteAccess.Server.exe"), "old");
        Directory.CreateDirectory(Path.Combine(_root, "user-config"));
        var profile = Path.Combine(_root, "user-config", "viewer-profiles.json");
        File.WriteAllText(profile, "trusted public pins");
        Engine.Install(path, sha, PreviewInstallMode.Server, null, CancellationToken.None);
        Assert.Equal("new-server-0.5.3",
            File.ReadAllText(Path.Combine(InstallRoot, "Server", "TJ.RemoteAccess.Server.exe")));
        Assert.True(File.Exists(Path.Combine(InstallRoot, "Setup", "TJ.RemoteAccess.Setup.exe")));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "Viewer")));
        Assert.Equal("trusted public pins", File.ReadAllText(profile));
        Assert.Empty(Directory.GetDirectories(InstallRoot, ".stage-*"));
        Assert.Empty(Directory.GetDirectories(InstallRoot, "Server.backup-*"));
    }

    [Fact]
    public void WrongArchiveHashNeverReplacesAnInstalledComponent()
    {
        var (path, sha) = Package();
        Directory.CreateDirectory(Path.Combine(InstallRoot, "Server"));
        string exe = Path.Combine(InstallRoot, "Server", "TJ.RemoteAccess.Server.exe");
        File.WriteAllText(exe, "previous");
        Assert.Throws<InvalidDataException>(() =>
            Engine.Install(path, new string('F', 64), PreviewInstallMode.Both,
                           null, CancellationToken.None));
        Assert.Equal("previous", File.ReadAllText(exe));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "Setup")));
    }

    [Fact]
    public void TamperedManifestFileIsRejectedBeforeInstalledBinaryIsChanged()
    {
        // The OUTER ZIP hash is correct, but an inner SHA-256 claim was
        // tampered with. Nothing must be replaced before full verification.
        var (path, sha) = Package(corruptManifest: true);
        Assert.Throws<InvalidDataException>(() =>
            Engine.Install(path, sha, PreviewInstallMode.Both,
                           null, CancellationToken.None));
        // Empty install root may be created for staging, but no
        // COMPONENT folder is replaced on integrity failure.
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "Server")));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "Viewer")));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "Setup")));
        Assert.Empty(Directory.GetDirectories(InstallRoot, ".stage-*"));
    }

    [Fact]
    public void ComponentSwapErrorRestoresPriorServerAndLeavesNoPartialViewer()
    {
        var (path, sha) = Package();
        Directory.CreateDirectory(Path.Combine(InstallRoot, "Server"));
        string exe = Path.Combine(InstallRoot, "Server", "TJ.RemoteAccess.Server.exe");
        File.WriteAllText(exe, "previous-server");
        // A file instead of a folder at the Viewer destination forces a
        // real filesystem failure AFTER the Server directory has been swapped.
        File.WriteAllText(Path.Combine(InstallRoot, "Viewer"), "blocked-destination");
        Assert.ThrowsAny<IOException>(() =>
            Engine.Install(path, sha, PreviewInstallMode.Both,
                           null, CancellationToken.None));
        Assert.Equal("previous-server", File.ReadAllText(exe));
        Assert.Equal("blocked-destination", File.ReadAllText(Path.Combine(InstallRoot, "Viewer")));
        Assert.False(Directory.Exists(Path.Combine(InstallRoot, "Setup")));
    }

    [Fact]
    public void VerificationCancellationDoesNotModifyPreviousInstall()
    {
        var (path, sha) = Package();
        Directory.CreateDirectory(Path.Combine(InstallRoot, "Server"));
        string exe = Path.Combine(InstallRoot, "Server", "TJ.RemoteAccess.Server.exe");
        File.WriteAllText(exe, "previous");
        using var cancel = new CancellationTokenSource();
        var reporter = new InlineProgress(p => {
            if (p.Phase == "Verifying and staging files") cancel.Cancel();
        });
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Engine.Install(path, sha, PreviewInstallMode.Server, reporter, cancel.Token));
        Assert.Equal("previous", File.ReadAllText(exe));
        Assert.Empty(Directory.GetDirectories(InstallRoot, ".stage-*"));
    }

    [Fact]
    public void OnlyKnownTJRAExecutablesShouldBlockInstallation()
    {
        Assert.True(PreviewPackageInstaller.RequiresExistingProcessShutdown(
            ["TJ.RemoteAccess.Server", "notepad"]));
        Assert.True(PreviewPackageInstaller.RequiresExistingProcessShutdown(
            ["TJ.RemoteAccess.M3.Agent"]));
        Assert.False(PreviewPackageInstaller.RequiresExistingProcessShutdown(
            ["TightVNC", "explorer"]));
        Assert.Equal(["Server", "Viewer", "Setup"],
            PreviewPackageInstaller.Components(PreviewInstallMode.Both));
    }

    private sealed class InlineProgress(Action<PreviewInstallProgress> action)
        : IProgress<PreviewInstallProgress>
    {
        public void Report(PreviewInstallProgress step) => action(step);
    }
}
