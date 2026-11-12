// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tjra-profile-tests-" + Guid.NewGuid().ToString("N"));
    private const string ClientA = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string ClientB = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string ServerA = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string ServerB = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
    private LabViewerProfileStore Store => new(_root);
    private static LabViewerSettings Credentials(string client, string server,
                                                   string host = "127.0.0.1") => new()
    {
        ClientCertificateThumbprint = client,
        ServerSha256 = server,
        Host = host,
        Port = 15444
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void EmptyCatalogAllowsFirstServerImport()
    {
        var blank = Store.Load();
        Assert.Empty(blank.Profiles);
        var first = Store.RegisterProvisioned(Credentials(ClientA, ServerA),
                                               "Lab PC 1", "192.168.10.15");
        var saved = Store.Load();
        Assert.Single(saved.Profiles);
        Assert.Equal(first.Id, saved.SelectedProfileId);
        Assert.Equal("Lab PC 1", saved.Profiles[0].Name);
        Assert.Equal("192.168.10.15", saved.Profiles[0].Host);
        Assert.Equal(ServerA, saved.Profiles[0].ServerSha256);
        Assert.False(File.Exists(Store.LegacyViewerPath));
    }

    [Fact]
    public void NonDestructiveMigrationPreservesExistingViewerJson()
    {
        Directory.CreateDirectory(_root);
        var original = JsonSerializer.Serialize(Credentials(ClientA, ServerA),
            LabCertificates.JsonOptions);
        File.WriteAllText(Store.LegacyViewerPath, original);
        var migrated = Store.Load();
        Assert.Single(migrated.Profiles);
        Assert.Equal("Previous server", migrated.Profiles[0].Name);
        Assert.Equal(original, File.ReadAllText(Store.LegacyViewerPath));
        var second = Store.RegisterProvisioned(Credentials(ClientB, ServerB,
            "192.168.2.22"), "Remote server 2");
        var result = Store.Load();
        Assert.Equal(2, result.Profiles.Count);
        Assert.Equal(second.Id, result.SelectedProfileId);
        Assert.Equal(original, File.ReadAllText(Store.LegacyViewerPath));
    }

    [Fact]
    public void ReimportSamePinsIsIdempotentAndDoesNotResetEndpoint()
    {
        var first = Store.RegisterProvisioned(Credentials(ClientA, ServerA), "Work server");
        var changed = Store.Edit(first.Id, "Operator PC", "10.1.3.4", 23456);
        var again = Store.RegisterProvisioned(Credentials(ClientA, ServerA), "Injected name",
                                               "127.0.0.1");
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(changed.Host, again.Host);
        Assert.Equal(changed.Name, again.Name);
        Assert.Equal(changed.Port, again.Port);
        Assert.Single(Store.Load().Profiles);
    }

    [Fact]
    public void MultipleServersRememberDifferentPinsAndSelectedIdentity()
    {
        var one = Store.RegisterProvisioned(Credentials(ClientA, ServerA), "Server A");
        var two = Store.RegisterProvisioned(Credentials(ClientB, ServerB, "10.0.0.2"),
                                           "Server B");
        Assert.NotEqual(one.Id, two.Id);
        Store.Select(one.Id);
        Assert.Equal(one.Id, Store.Load().SelectedProfileId);
        Store.Select(two.Id);
        var result = Store.Load();
        Assert.Equal(two.Id, result.SelectedProfileId);
        Assert.Equal(ServerB, result.Profiles.Single(p => p.Id == two.Id).ServerSha256);
        Assert.Equal(ServerA, result.Profiles.Single(p => p.Id == one.Id).ServerSha256);
    }

    [Fact]
    public void DeleteOnlyRemovesTargetProfileNotOtherTrust()
    {
        var one = Store.RegisterProvisioned(Credentials(ClientA, ServerA));
        var two = Store.RegisterProvisioned(Credentials(ClientB, ServerB));
        Store.Remove(two.Id);
        var saved = Store.Load();
        Assert.Single(saved.Profiles);
        Assert.Equal(one.Id, saved.SelectedProfileId);
        Assert.Equal(ServerA, saved.Profiles[0].ServerSha256);
        Assert.Throws<KeyNotFoundException>(() => Store.Select(two.Id));
        Store.Remove(one.Id);
        Assert.Empty(Store.Load().Profiles);
        Assert.Equal("", Store.Load().SelectedProfileId);
    }

    [Theory]
    [InlineData(" ", "127.0.0.1", 15444)]
    [InlineData("Server", "bad host/path", 15444)]
    [InlineData("Server", "192.168.0.1", 80)]
    [InlineData("Server", "192.168.0.1", 65536)]
    public void EditRejectsBadEndpointsWithoutChangingSavedPins(
        string name, string host, int port)
    {
        var first = Store.RegisterProvisioned(Credentials(ClientA, ServerA));
        Assert.Throws<InvalidDataException>(() => Store.Edit(first.Id, name, host, port));
        Assert.Equal(ServerA, Store.Load().Profiles.Single().ServerSha256);
        Assert.Equal(15444, Store.Load().Profiles.Single().Port);
    }

    [Fact]
    public void InvalidCertificateFingerprintCannotBeProvisioned()
    {
        var invalid = Credentials(ClientA, "invalid-sha256");
        Assert.Throws<InvalidDataException>(() => Store.RegisterProvisioned(invalid));
        Assert.Empty(Store.Load().Profiles);
    }

    [Fact]
    public void CatalogTamperingFailsClosedEvenIfLegacyFileExists()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Store.LegacyViewerPath,
            JsonSerializer.Serialize(Credentials(ClientA, ServerA)));
        var original = Store.Load();
        Assert.Single(original.Profiles);
        File.WriteAllText(Store.CatalogPath, "{ \"SchemaVersion\": 999, \"Profiles\": [] }");
        Assert.Throws<InvalidDataException>(() => Store.Load());
        // Invalid catalog must not silently fall back to legacy trust values.
        Assert.Contains("999", File.ReadAllText(Store.CatalogPath));
    }

    [Fact]
    public async Task IndependentStoreInstancesSerializeConcurrentWrites()
    {
        var one = new LabViewerProfileStore(_root);
        var two = new LabViewerProfileStore(_root);
        var operations = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
        {
            var current = i % 2 == 0 ? one : two;
            var profile = current.RegisterProvisioned(
                Credentials(i % 2 == 0 ? ClientA : ClientB,
                            i % 2 == 0 ? ServerA : ServerB),
                "Worker " + i);
            current.Select(profile.Id);
        }));
        await Task.WhenAll(operations);
        var catalog = Store.Load();
        Assert.Equal(2, catalog.Profiles.Count);
        Assert.Contains(catalog.Profiles, p => p.ServerSha256 == ServerA);
        Assert.Contains(catalog.Profiles, p => p.ServerSha256 == ServerB);
    }

    [Fact]
    public void ReturnedSnapshotCannotMutateStoredProfiles()
    {
        var first = Store.RegisterProvisioned(Credentials(ClientA, ServerA));
        var read = Store.Load();
        read.Profiles[0].ServerSha256 = ServerB;
        Assert.Equal(first.ServerSha256, Store.Load().Profiles.Single().ServerSha256);
    }
[Fact]
    public void TamperedPairingBundleCannotReplaceExistingTrust()
    {
        var existing = Store.RegisterProvisioned(Credentials(ClientA, ServerA));
        var folder = Path.Combine(_root, "Pairing");
        Directory.CreateDirectory(folder);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=untrusted-bundle", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new(LabCertificates.ClientEku) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var pfx = Path.Combine(folder, "client-transfer.pfx");
        File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "local-test-password"));
        File.WriteAllText(Path.Combine(folder, "viewer.json"),
            JsonSerializer.Serialize(Credentials(ClientB, ServerB),
                                     LabCertificates.JsonOptions));
        Assert.Throws<CryptographicException>(() =>
            Store.ImportPairing(pfx, "local-test-password", "Spoofed server"));
        var result = Store.Load();
        Assert.Single(result.Profiles);
        Assert.Equal(existing.Id, result.SelectedProfileId);
        Assert.Equal(ServerA, result.Profiles.Single().ServerSha256);
    }
[Fact]
    public void MigratesCurrentAndPreviousManualBackupAsSeparateServerProfiles()
    {
        Directory.CreateDirectory(_root);
        var current = JsonSerializer.Serialize(Credentials(ClientB, ServerB, "192.168.1.20"));
        var oldBackup = JsonSerializer.Serialize(Credentials(ClientA, ServerA, "127.0.0.1"));
        File.WriteAllText(Store.LegacyViewerPath, current);
        string backupPath = Path.Combine(_root, "viewer.json.backup-20261111-190000");
        File.WriteAllText(backupPath, oldBackup);
        File.WriteAllText(Path.Combine(_root, "viewer.json.backup-duplicate"),
                          current);
        var catalog = Store.Load();
        Assert.Equal(2, catalog.Profiles.Count);
        Assert.Equal("Previous server",
            catalog.Profiles.Single(p => p.Id == catalog.SelectedProfileId).Name);
        Assert.Contains(catalog.Profiles, p => p.ServerSha256 == ServerA &&
            p.Name.StartsWith("Recovered server", StringComparison.Ordinal));
        Assert.Contains(catalog.Profiles, p => p.ServerSha256 == ServerB);
        Assert.Equal(oldBackup, File.ReadAllText(backupPath));
        Assert.Equal(current, File.ReadAllText(Store.LegacyViewerPath));
    }

    [Fact]
    public void InvalidOldBackupCannotOverrideValidCurrentServer()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Store.LegacyViewerPath,
            JsonSerializer.Serialize(Credentials(ClientA, ServerA)));
        File.WriteAllText(Path.Combine(_root, "viewer.json.backup-corrupted"),
                          "{this-is-invalid-json");
        var result = Store.Load();
        Assert.Single(result.Profiles);
        Assert.Equal(ServerA, result.Profiles[0].ServerSha256);
    }
}
