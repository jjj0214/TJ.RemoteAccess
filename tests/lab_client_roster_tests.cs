// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabServerClientRosterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tjra-roster-" + Guid.NewGuid().ToString("N"));
    private const string LegacyPin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
    private const string NewPin = "FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210FEDCBA9876543210";
    private LabServerClientRoster Store => new(_dir, LegacyPin);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AutoMigrationRetainsLegacyAccessWithoutChangingServerSettings()
    {
        var roster = Store;
        var catalog = roster.Load();
        Assert.Single(catalog.Clients);
        Assert.Equal(LegacyPin, catalog.Clients.Single().CertificateSha256);
        Assert.True(catalog.Clients.Single().Enabled);
        Assert.True(catalog.Clients.Single().CanControl);
        Assert.NotNull(roster.GetAuthorized(LegacyPin));
        Assert.True(File.Exists(roster.CatalogPath));
        Assert.True(File.Exists(Path.Combine(_dir, "server-clients.migrated")));
        Assert.Equal(catalog.Clients[0].Id, Store.Load().Clients[0].Id);
    }

    [Fact]
    public void RevocationKeepsLegacyIdentityDeniedAfterReload()
    {
        var one = Store.Load().Clients.Single();
        Store.SetEnabled(one.Id, false);
        Assert.Null(Store.GetAuthorized(LegacyPin));
        Assert.Single(Store.Load().Clients);
        Assert.False(Store.Load().Clients[0].Enabled);
        Assert.Null(new LabServerClientRoster(_dir, LegacyPin).GetAuthorized(LegacyPin));
    }

    [Fact]
    public void DeletingCatalogCannotReactivatePreviouslyDisabledLegacyClient()
    {
        var roster = Store;
        var id = roster.Load().Clients.Single().Id;
        roster.SetEnabled(id, false);
        File.Delete(roster.CatalogPath);
        Assert.Null(new LabServerClientRoster(_dir, LegacyPin).GetAuthorized(LegacyPin));
        Assert.Throws<InvalidDataException>(() =>
            new LabServerClientRoster(_dir, LegacyPin).Load());
    }

    [Fact]
    public void DistinctClientsHaveSeparateAccessAndControlRights()
    {
        var first = Store.Register("Viewer A", NewPin, canControl: false);
        Assert.NotNull(Store.GetAuthorized(LegacyPin));
        Assert.False(Store.GetAuthorized(NewPin)!.CanControl);
        var approved = Store.SetCanControl(first.Id, true);
        Assert.True(approved.CanControl);
        Assert.True(Store.GetAuthorized(NewPin)!.CanControl);
        Store.SetEnabled(first.Id, false);
        Assert.Null(Store.GetAuthorized(NewPin));
        Assert.NotNull(Store.GetAuthorized(LegacyPin));
    }

    [Fact]
    public void CorruptCatalogIsDeniedInsteadOfFallingBackToOldPin()
    {
        var roster = Store;
        roster.Load();
        File.WriteAllText(roster.CatalogPath, "{invalid-json");
        Assert.Null(new LabServerClientRoster(_dir, LegacyPin).GetAuthorized(LegacyPin));
        Assert.ThrowsAny<Exception>(() => new LabServerClientRoster(_dir, LegacyPin).Load());
    }

    [Fact]
    public void DuplicateClientPinCannotBeReEnrolledOrSilentlyEnabled()
    {
        var id = Store.Load().Clients.Single().Id;
        Store.SetEnabled(id, false);
        Assert.Throws<InvalidOperationException>(
            () => Store.Register("Re-enrolled legacy", LegacyPin, canControl: true));
        Assert.False(Store.Load().Clients.Single().Enabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nname")]
    public void InvalidNamesCannotCreateClient(string name)
    {
        Assert.Throws<InvalidDataException>(() => Store.Register(name, NewPin, true));
    }

    [Fact]
    public void InvalidFingerprintAndPartialClientIdsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => Store.Register("Viewer B", "not-a-sha256", true));
        Assert.Throws<ArgumentException>(() => Store.SetEnabled("abc", false));
        Assert.Null(Store.GetAuthorized("bad-pin"));
    }

    [Fact]
    public void ReturnedClientAndCatalogAreCopies()
    {
        var catalog = Store.Load();
        catalog.Clients.Single().Enabled = false;
        Assert.NotNull(Store.GetAuthorized(LegacyPin));
        var read = Store.GetAuthorized(LegacyPin)!;
        read.CanControl = false;
        Assert.True(Store.GetAuthorized(LegacyPin)!.CanControl);
    }

    [Fact]
    public async Task ConcurrentWritersCannotDuplicateOrLoseClients()
    {
        var a = Store;
        var b = new LabServerClientRoster(_dir, LegacyPin);
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() => {
            var pin = i.ToString("X64");
            (i % 2 == 0 ? a : b).Register("viewer " + i, pin, canControl: i % 3 == 0);
        }));
        await Task.WhenAll(tasks);
        Assert.Equal(13, Store.Load().Clients.Count);
        Assert.Equal(13, Store.Load().Clients.Select(x => x.CertificateSha256).Distinct().Count());
    }

    private static X509Certificate2 NewServerCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=lab-server-roster-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(LabCertificates.ServerEku) }, critical: true));
        using var temporary = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadCertificate(temporary.RawData);
    }

    [Fact]
    public void UniquePairingBundleUsesOwnClientCertAndPrivateKey()
    {
        using var server = NewServerCert();
        var settings = new LabServerSettings {
            ServerCertificateThumbprint = server.Thumbprint,
            AllowedClientSha256 = LegacyPin,
            ListenAddress = "127.0.0.1",
            Port = 15444
        };
        var roster = Store;
        var result = LabClientPairing.Create("Unique Viewer A", settings, server,
            roster, Path.Combine(_dir, "Pairing"), canControl: false);
        Assert.Equal("Unique Viewer A", result.Client.Name);
        Assert.False(result.Client.CanControl);
        var file = Path.Combine(result.Folder, "client-transfer.pfx");
        Assert.True(File.Exists(file));
        using var client = X509CertificateLoader.LoadPkcs12FromFile(
            file, result.PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(client.HasPrivateKey);
        Assert.True(roster.IsCertificateAllowed(client));
        Assert.False(roster.GetAuthorized(result.Client.CertificateSha256)!.CanControl);
        Assert.Equal(client.Thumbprint, LabCertificates.LoadSettings<LabViewerSettings>(
            Path.Combine(result.Folder, "viewer.json")).ClientCertificateThumbprint);
        Assert.Equal(LabCertificates.Hash(server), LabCertificates.LoadSettings<LabViewerSettings>(
            Path.Combine(result.Folder, "viewer.json")).ServerSha256);
        Assert.Equal(LegacyPin, Store.Load().Clients.Single(x => x.Name == "Legacy paired Viewer").CertificateSha256);
        roster.SetEnabled(result.Client.Id, false);
        Assert.False(roster.IsCertificateAllowed(client));
        Assert.True(File.Exists(file)); // Revocation does not touch Viewer PFX files.
    }

    [Fact]
    public void TwoGeneratedPairingsHaveDifferentCertificates()
    {
        using var server = NewServerCert();
        var settings = new LabServerSettings {
            ServerCertificateThumbprint = server.Thumbprint,
            ListenAddress = "192.168.10.10"
        };
        var roster = Store;
        var a = LabClientPairing.Create("A", settings, server, roster, Path.Combine(_dir, "Pairing"));
        var b = LabClientPairing.Create("B", settings, server, roster, Path.Combine(_dir, "Pairing"));
        Assert.NotEqual(a.Client.Id, b.Client.Id);
        Assert.NotEqual(a.Client.CertificateSha256, b.Client.CertificateSha256);
        Assert.NotEqual(a.Folder, b.Folder);
        Assert.Equal(3, roster.Load().Clients.Count);
    }
}
