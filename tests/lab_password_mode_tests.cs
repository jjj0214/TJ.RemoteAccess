// SPDX-License-Identifier: GPL-2.0-or-later
// Password access is opt-in and always protected by server-pinned TLS.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabPasswordModeTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "tjra-password-" + Guid.NewGuid().ToString("N"));
    private LabPasswordAccess Store => new(_folder);
    private const string Secret = "A unique Lab passphrase 2026!";

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void PasswordListenerIsOptInAndUnavailableByDefault()
    {
        Assert.False(Store.Current().Enabled);
        Assert.False(Store.Verify(Secret));
        Assert.False(Store.IsSessionAuthorized("any-verifier", false));
        Assert.False(File.Exists(Store.SettingsFile));
    }

    [Fact]
    public void PasswordIsSaltedHashedAndNeverStoredInPlaintext()
    {
        var access = Store;
        access.Configure(Secret, canControl: false);
        string json = File.ReadAllText(access.SettingsFile);
        Assert.DoesNotContain(Secret, json, StringComparison.Ordinal);
        Assert.Contains("VerifierBase64", json);
        Assert.Contains("SaltBase64", json);
        Assert.True(access.TryAuthenticate(Secret, out var authorized));
        Assert.True(access.IsSessionAuthorized(authorized.VerifierBase64, false));
        Assert.False(access.IsSessionAuthorized(authorized.VerifierBase64, true));
        Assert.True(new LabPasswordAccess(_folder).Verify(Secret));
        Assert.False(access.Verify("Not the password 2026!"));
    }

    [Fact]
    public void PasswordRotationAndDisableRevokeOldSessionAndKeepViewOnlyDefault()
    {
        var access = Store;
        access.Configure(Secret, canControl: true);
        Assert.True(access.TryAuthenticate(Secret, out var old));
        Assert.True(access.IsSessionAuthorized(old.VerifierBase64, true));
        access.Configure("New different Lab passphrase 2026!!", canControl: false);
        Assert.False(access.IsSessionAuthorized(old.VerifierBase64, false));
        Assert.False(access.Verify(Secret));
        Assert.True(access.TryAuthenticate("New different Lab passphrase 2026!!", out var current));
        Assert.False(access.IsSessionAuthorized(current.VerifierBase64, true));
        access.Disable();
        Assert.False(access.Current().Enabled);
        Assert.False(access.Verify("New different Lab passphrase 2026!!"));
        Assert.False(access.IsSessionAuthorized(current.VerifierBase64, false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("tab\tcharacter")]
    [InlineData("a line\nwith break")]
    public void WeakOrControlCharacterPasswordsAreRejected(string secret)
    {
        Assert.False(LabPasswordAccess.ValidPassword(secret));
        Assert.Throws<InvalidDataException>(() => Store.Configure(secret, false));
    }

    [Fact]
    public void InvalidAndMissingPolicyAreFailClosed()
    {
        Store.Configure(Secret, false);
        File.WriteAllText(Store.SettingsFile, "{invalid-json");
        Assert.False(new LabPasswordAccess(_folder).Current().Enabled);
        Assert.False(new LabPasswordAccess(_folder).Verify(Secret));
        File.Delete(Store.SettingsFile);
        Assert.False(new LabPasswordAccess(_folder).Current().Enabled);
    }

    [Fact]
    public void GlobalPasswordDataIsSmallAndVersioned()
    {
        var body = LabWire.PasswordLogin(Secret);
        Assert.Equal(LabWire.Version, body[0]);
        Assert.Equal(Secret, LabWire.ParsePasswordLogin(body));
        Assert.True(body.Length + 1 <= LabWire.MaxPasswordPacketBytes);
        body[0] = 99;
        Assert.Throws<InvalidDataException>(() => LabWire.ParsePasswordLogin(body));
        Assert.Throws<InvalidDataException>(() => LabWire.PasswordLogin("short"));
        Assert.Throws<InvalidDataException>(() => LabWire.ParsePasswordLogin(new byte[260]));
    }

    [Fact]
    public void RateLimiterLocksAfterFiveFailuresAndResetsAfterDelay()
    {
        DateTimeOffset now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        var limiter = new LabPasswordAttemptLimiter(() => now);
        for (int i = 0; i < 5; i++)
        {
            Assert.True(limiter.MayAttempt("192.168.1.5"));
            limiter.Failed("192.168.1.5");
        }
        Assert.False(limiter.MayAttempt("192.168.1.5"));
        Assert.True(limiter.MayAttempt("192.168.1.6"));
        now = now.AddSeconds(119);
        Assert.False(limiter.MayAttempt("192.168.1.5"));
        now = now.AddSeconds(2);
        Assert.True(limiter.MayAttempt("192.168.1.5"));
        limiter.Succeeded("192.168.1.5");
        Assert.True(limiter.MayAttempt("192.168.1.5"));
    }

    [Fact]
    public void AggregateRateLimitBlocksDistributedBruteForceAttempts()
    {
        DateTimeOffset now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        var limiter = new LabPasswordAttemptLimiter(() => now);
        for (int i = 0; i < 20; ++i)
            limiter.Failed("10.0.0." + i);
        Assert.False(limiter.MayAttempt("10.0.1.100"));
        now = now.AddSeconds(121);
        Assert.True(limiter.MayAttempt("10.0.1.100"));
    }

    [Fact]
    public void CertificateProfilesAndPasswordProfilesAreIndependentlyPinned()
    {
        const string pin = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
        var store = new LabViewerProfileStore(_folder);
        var cert = store.RegisterProvisioned(new LabViewerSettings {
            Host = "127.0.0.1", Port = 15444,
            ServerSha256 = pin,
            ClientCertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567"
        }, "Original certificate Viewer");
        var pwd = store.RegisterPasswordProfile("Lab password", "192.168.1.2", 15445, pin);
        Assert.Equal("Certificate", cert.AuthMode);
        Assert.Equal("Password", pwd.AuthMode);
        Assert.Equal("", pwd.ClientCertificateThumbprint);
        Assert.Equal(2, store.Load().Profiles.Count);
        var repeat = store.RegisterPasswordProfile("Duplicate", "192.168.1.9", 15445, pin);
        Assert.Equal(pwd.Id, repeat.Id);
        Assert.Equal("192.168.1.2", repeat.Host);
        Assert.Equal(2, store.Load().Profiles.Count);
        Assert.Throws<InvalidDataException>(() =>
            store.RegisterPasswordProfile("Unsafe", "192.168.1.2", 15445, "not a pin"));
    }

    private static X509Certificate2 NewServer()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=tjra-password-loopback", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(LabCertificates.ServerEku) }, true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var original = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1));
        var pkcs = original.Export(X509ContentType.Pfx, "test temporary");
        try
        {
#pragma warning disable SYSLIB0057
            return new X509Certificate2(pkcs, "test temporary",
                X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
        }
        finally { CryptographicOperations.ZeroMemory(pkcs); }
    }

    private async Task<(bool Encrypted, bool Authorized)> LoopbackLogin(
        X509Certificate2 serverCert, string password)
    {
        var policy = Store;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var serverTask = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync(token);
            using var tls = new SslStream(incoming.GetStream(), false);
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = serverCert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            var packet = await LabWire.ReadAsync(tls, token, LabWire.MaxPasswordPacketBytes);
            if (packet.Type != LabMessage.PasswordLogin ||
                !policy.Verify(LabWire.ParsePasswordLogin(packet.Payload)))
                return (Encrypted: tls.IsEncrypted, Authorized: false);
            var hello = await LabWire.ReadAsync(tls, token, LabWire.MaxControlPacketBytes);
            if (hello.Type != LabMessage.Hello || hello.Payload.Length != 1 ||
                hello.Payload[0] != LabWire.Version) return (Encrypted: tls.IsEncrypted, Authorized: false);
            using var mutex = new SemaphoreSlim(1);
            await LabWire.WriteAsync(tls, mutex, LabMessage.Welcome,
                LabWire.Welcome(new LabWelcome(LabRole.ViewOnly, 800, 600, 0, 0)), token);
            return (Encrypted: tls.IsEncrypted, Authorized: true);
        }, token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        using var ssl = new SslStream(client.GetStream(), false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "localhost",
            ClientCertificates = null,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
                LabCertificates.ValidatePinned(cert, LabCertificates.Hash(serverCert),
                                               LabCertificates.ServerEku)
        }, token);
        Assert.True(ssl.IsEncrypted);
        using var sendLock = new SemaphoreSlim(1);
        var loginBody = LabWire.PasswordLogin(password);
        try {
            await LabWire.WriteAsync(ssl, sendLock, LabMessage.PasswordLogin, loginBody, token);
        }
        finally { CryptographicOperations.ZeroMemory(loginBody); }
        await LabWire.WriteAsync(ssl, sendLock,
                                  LabMessage.Hello, new[] { LabWire.Version }, token);
        var actual = await serverTask;
        if (actual.Authorized)
        {
            var welcome = await LabWire.ReadAsync(ssl, token);
            Assert.Equal(LabMessage.Welcome, welcome.Type);
            Assert.Equal(LabRole.ViewOnly, LabWire.ParseWelcome(welcome.Payload).Role);
        }
        return actual;
    }

    [Fact]
    public async Task RealTlsPasswordHandshakeWorksWithoutClientCertificate()
    {
        using var serverCert = NewServer();
        Store.Configure(Secret, canControl: false);
        var result = await LoopbackLogin(serverCert, Secret);
        Assert.True(result.Encrypted);
        Assert.True(result.Authorized);
        Assert.True(LabCertificates.ValidatePinned(serverCert, LabCertificates.Hash(serverCert),
                                                   LabCertificates.ServerEku));
    }

    [Fact]
    public async Task WrongPasswordCannotGetAWelcomeEvenWithValidTls()
    {
        using var serverCert = NewServer();
        Store.Configure(Secret, canControl: false);
        var result = await LoopbackLogin(serverCert, "Definitely not the correct pass 2026");
        Assert.True(result.Encrypted);
        Assert.False(result.Authorized);
    }
}
