// SPDX-License-Identifier: GPL-2.0-or-later
// Executes the REAL offline-package builder against a tiny disposable local
// Git repository. This catches PowerShell-specific packaging errors that
// compiler checks and syntactic script checks cannot detect. No install.
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using TJ.RemoteAccess.Installer;
using Xunit;

public sealed class PreviewOfflineBuilderEndToEndTests
{
    private static string SourceRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));

    private static async Task<string> RunAsync(
        string program, IReadOnlyList<string> args, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        Assert.True(process.Start(), "Unable to launch " + program);
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var stdout = await output;
        var stderr = await error;
        Assert.True(process.ExitCode == 0,
            program + " exit " + process.ExitCode + "\n" +
            "stdout:\n" + stdout + "\nstderr:\n" + stderr);
        return stdout;
    }

    [Fact]
    public async Task FullOfflinePackageBuilderCreatesExactlyNamedManifestAndValidSidecar()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        string fixture = Path.Combine(Path.GetTempPath(),
            "TJRA-full-offline-" + Guid.NewGuid().ToString("N"));
        string tools = Path.Combine(fixture, "tools");
        try
        {
            Directory.CreateDirectory(tools);
            string sourceTools = Path.Combine(SourceRoot(), "tools");
            foreach (var file in new[] {
                "Build-TJRemoteAccess-Preview-OfflinePackage.ps1",
                "New-TJRemoteAccess-PreviewZip.ps1",
                "Install-TJRemoteAccess-Preview-Offline.ps1",
                "Uninstall-TJRemoteAccess-Preview.ps1",
                "Test-TJRemoteAccess-Preview.ps1",
                "Measure-TJRemoteAccess-Preview.ps1",
                "Set-TJRemoteAccess-Preview-Startup.ps1",
                "Get-TJRemoteAccessFileSha256.ps1"
            }) File.Copy(Path.Combine(sourceTools, file), Path.Combine(tools, file));
            File.WriteAllText(Path.Combine(fixture, "LICENSE"), "Test-only GPL license stub");
            File.WriteAllText(Path.Combine(fixture, ".gitignore"), "out/\n");
            await RunAsync("git.exe", ["init", "-q", fixture], token);
            await RunAsync("git.exe", ["-C", fixture, "add", "-A"], token);
            await RunAsync("git.exe", ["-C", fixture,
                "-c", "user.name=TJRA Fixture",
                "-c", "user.email=fixture@example.invalid",
                "commit", "-q", "-m", "packaging fixture"], token);

            foreach (var part in new[] { "Server", "Viewer", "Setup" })
            {
                string dir = Path.Combine(fixture, "out", "preview-publish", part);
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    Path.Combine(dir, "TJ.RemoteAccess." + part + ".exe"),
                    "Test binary " + part);
                File.WriteAllText(Path.Combine(dir, "metadata.txt"), "Data " + part);
            }

            // Reproduce the GitHub runner regression: Get-FileHash is
            // deliberately unavailable in the shell running the REAL builder.
            // The portable SHA-256 helper must work without that cmdlet.
            string harness = Path.Combine(fixture, "out", "without-get-filehash.ps1");
            string builder = Path.Combine(tools,
                "Build-TJRemoteAccess-Preview-OfflinePackage.ps1")
                .Replace("'", "''");
            File.WriteAllText(harness,
                "function global:Get-FileHash { throw 'Get-FileHash disabled by CI regression test' }\n" +
                "& '" + builder + "' -Version '0.5.3-preview' -SkipBuild\n");
            string stdout = await RunAsync("powershell.exe", [
                "-NoLogo", "-NoProfile", "-NonInteractive",
                "-ExecutionPolicy", "Bypass", "-File", harness
            ], token);
            Assert.Contains("OFFLINE PACKAGE BUILT", stdout);
            string zipPath = Path.Combine(fixture, "out", "packages",
                "TJ.RemoteAccess-0.5.3-preview-win-x64.zip");
            string sidecar = zipPath + ".sha256";
            Assert.True(File.Exists(zipPath));
            Assert.True(File.Exists(sidecar));
            Assert.StartsWith(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))),
                File.ReadAllText(sidecar), StringComparison.OrdinalIgnoreCase);

            using var archive = ZipFile.OpenRead(zipPath);
            var names = archive.Entries.Select(x => x.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("manifest.json", names);
            Assert.Contains("source.zip", names);
            foreach (var part in new[] { "Server", "Viewer", "Setup" })
                Assert.Contains(part + "/TJ.RemoteAccess." + part + ".exe", names);
            Assert.All(names, entry => Assert.DoesNotContain('\\', entry));
            using var reader = new StreamReader(archive.GetEntry("manifest.json")!.Open());
            var manifest = JsonSerializer.Deserialize<PreviewPackageManifest>(
                await reader.ReadToEndAsync(token));
            Assert.NotNull(manifest);
            Assert.Equal("TJ.RemoteAccess.Offline.Preview", manifest.Artifact);
            Assert.Equal("0.5.3-preview", manifest.Version);
            Assert.NotNull(manifest.Files);
            Assert.Equal(names.Count, manifest.Files.Length + 1);
            foreach (var row in manifest.Files)
            {
                var entry = archive.GetEntry(row.Path);
                Assert.NotNull(entry);
                Assert.Equal(row.Bytes, entry.Length);
                using var stream = entry.Open();
                var digest = await SHA256.HashDataAsync(stream, token);
                Assert.Equal(row.Sha256, Convert.ToHexString(digest),
                    ignoreCase: true);
            }
        }
        finally
        {
            if (Directory.Exists(fixture))
            {
                // Git on Windows marks some loose objects read-only. This
                // disposable fixture must be cleaned without touching real
                // repositories or masking the packaging assertions.
                foreach (string file in Directory.EnumerateFiles(
                    fixture, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(fixture, recursive: true);
            }
        }
    }
}
