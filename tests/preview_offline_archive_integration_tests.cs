// SPDX-License-Identifier: GPL-2.0-or-later
// Regression: build a realistic tiny Preview ZIP using the EXACT PowerShell
// ZIP writer used by the release builder on Windows. No installation,
// registry, service, firewall or user configuration is modified.
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TJ.RemoteAccess.Installer;
using Xunit;

public sealed class PreviewOfflineArchiveIntegrationTests
{
    private static string RepositoryRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, ".."));

    [Fact]
    public async Task WindowsZipBuilderPreservesExactManifestPathsAndPayloadHashes()
    {
        if (!OperatingSystem.IsWindows()) return;
        string folder = Path.Combine(Path.GetTempPath(),
            "TJRA-offline-zip-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(folder, "stage");
        string output = Path.Combine(folder, "package.zip");
        try
        {
            Directory.CreateDirectory(stage);
            var files = new Dictionary<string, byte[]>
            {
                ["Server/TJ.RemoteAccess.Server.exe"] = Encoding.UTF8.GetBytes("server-payload"),
                ["Viewer/TJ.RemoteAccess.Viewer.exe"] = Encoding.UTF8.GetBytes("viewer-payload"),
                ["Setup/TJ.RemoteAccess.Setup.exe"] = Encoding.UTF8.GetBytes("setup-payload"),
                ["LICENSE"] = Encoding.UTF8.GetBytes("GPLv2 fixture"),
                ["source.zip"] = Encoding.UTF8.GetBytes("GPL source stub")
            };
            foreach (var (name, bytes) in files)
            {
                string path = Path.Combine(stage, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
            var manifest = new PreviewPackageManifest
            {
                SchemaVersion = 1,
                Artifact = "TJ.RemoteAccess.Offline.Preview",
                Version = "0.5.3-preview",
                RuntimeIdentifier = "win-x64",
                SourceRevision = new string('c', 40),
                Unsigned = true,
                Files = files.Select(x => new PreviewPackageFile
                {
                    Path = x.Key, Bytes = x.Value.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(x.Value))
                }).ToArray()
            };
            File.WriteAllText(Path.Combine(stage, "manifest.json"),
                JsonSerializer.Serialize(manifest), new UTF8Encoding(false));

            var p = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (string arg in new[] {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", Path.Combine(RepositoryRoot(), "tools",
                                      "New-TJRemoteAccess-PreviewZip.ps1"),
                "-StageDirectory", stage, "-DestinationZip", output
            }) p.ArgumentList.Add(arg);
            using var process = Process.Start(p) ??
                throw new InvalidOperationException("Windows PowerShell unavailable.");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            string stdout = await stdoutTask, stderr = await stderrTask;
            Assert.True(process.ExitCode == 0,
                "ZIP writer failed.\nstdout: " + stdout + "\nstderr: " + stderr);
            Assert.True(File.Exists(output));
            using var zip = ZipFile.OpenRead(output);
            var actual = zip.Entries.ToDictionary(e => e.FullName, StringComparer.Ordinal);
            Assert.Equal(files.Count + 1, actual.Count);
            Assert.Contains("manifest.json", actual.Keys);
            Assert.All(actual.Keys, name => Assert.DoesNotContain('\\', name));
            foreach (var (name, data) in files)
            {
                Assert.True(actual.ContainsKey(name),
                    "Expected exact forward-slash path: " + name +
                    "; entries: " + string.Join("; ", actual.Keys));
                Assert.Equal(data.Length, actual[name].Length);
                using var entryStream = actual[name].Open();
                using var memory = new MemoryStream();
                await entryStream.CopyToAsync(memory, timeout.Token);
                Assert.Equal(data, memory.ToArray());
            }
            using var manifestReader = new StreamReader(actual["manifest.json"].Open());
            var extracted = JsonSerializer.Deserialize<PreviewPackageManifest>(
                await manifestReader.ReadToEndAsync(timeout.Token));
            Assert.NotNull(extracted?.Files);
            Assert.All(extracted.Files, row =>
            {
                Assert.True(actual.ContainsKey(row.Path), row.Path);
                Assert.Equal(row.Bytes, actual[row.Path].Length);
            });
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
