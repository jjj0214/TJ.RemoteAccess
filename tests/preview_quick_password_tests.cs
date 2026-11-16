// SPDX-License-Identifier: GPL-2.0-or-later
// IP + password quick access requires TLS, a verified Server fingerprint,
// and an explicit first-connection decision BEFORE any PasswordLogin message.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class PreviewQuickPasswordTests : IDisposable
{
    private const string Password = "Correct horse battery 2026!";
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "TJRA-quick-pass-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static X509Certificate2 MakeCertificate(bool server = true)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=TJRA-quick-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(server ? LabCertificates.ServerEku : LabCertificates.ClientEku) },
            true));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var original = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        byte[] bytes = original.Export(X509ContentType.Pfx, "test-only-pfx");
        try
        {
#pragma warning disable SYSLIB0057
            return new X509Certificate2(bytes, "test-only-pfx",
                X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    [Fact]
    public void FirstUseAcceptsOnlyValidServerCertificatesNotClientCertificates()
    {
        using var server = MakeCertificate();
        using var client = MakeCertificate(server: false);
        var sha = LabCertificates.Hash(server);
        Assert.Equal(sha, LabQuickPasswordTrust.ExamineServer(server, null));
        Assert.Equal(sha, LabQuickPasswordTrust.ExamineServer(server, sha));
        Assert.Null(LabQuickPasswordTrust.ExamineServer(server, new string('F', 64)));
        Assert.Null(LabQuickPasswordTrust.ExamineServer(client, null));
        Assert.Null(LabQuickPasswordTrust.ExamineServer(null, sha));
        Assert.True(LabQuickPasswordTrust.MustConfirmFirstUse(null));
        Assert.True(LabQuickPasswordTrust.MustConfirmFirstUse(""));
        Assert.False(LabQuickPasswordTrust.MustConfirmFirstUse(sha));
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public void PasswordIsNeverSentBeforeEncryptedAndExplicitlyTrustedTls(
        bool authenticated, bool encrypted, bool approved, bool expected)
    {
        Assert.Equal(expected, LabQuickPasswordTrust.MaySendPassword(
            authenticated, encrypted, new string('A', 64), approved));
        Assert.False(LabQuickPasswordTrust.MaySendPassword(
            authenticated, encrypted, null, approved));
        Assert.False(LabQuickPasswordTrust.MaySendPassword(
            authenticated, encrypted, "not-hex", approved));
    }

    [Fact]
    public void PasswordPolicyChangesTriggerTlsListenerRebindAndSessionRevocation()
    {
        var initial = new LabPasswordSettings {
            Enabled = false, CanControl = false, SaltBase64 = "",
            VerifierBase64 = ""
        };
        var enabled = new LabPasswordSettings {
            Enabled = true, CanControl = false,
            SaltBase64 = Convert.ToBase64String(new byte[32]),
            VerifierBase64 = Convert.ToBase64String(new byte[32])
        };
        Assert.True(LabPasswordAccess.NeedsListenerRestart(initial, enabled));
        Assert.False(LabPasswordAccess.NeedsListenerRestart(enabled, new LabPasswordSettings {
            Enabled = enabled.Enabled, CanControl = enabled.CanControl,
            SaltBase64 = enabled.SaltBase64, VerifierBase64 = enabled.VerifierBase64
        }));
        Assert.True(LabPasswordAccess.NeedsListenerRestart(enabled, new LabPasswordSettings {
            Enabled = true, CanControl = true,
            SaltBase64 = enabled.SaltBase64, VerifierBase64 = enabled.VerifierBase64
        }));
        Assert.True(LabPasswordAccess.NeedsListenerRestart(enabled, new LabPasswordSettings {
            Enabled = true, CanControl = false,
            SaltBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
            VerifierBase64 = enabled.VerifierBase64
        }));
        Assert.True(LabPasswordAccess.NeedsListenerRestart(enabled, initial));
    }

    [Fact]
    public void PasswordServerTrustIsBoundToHostAndPortAndCannotBeReplacedSilently()
    {
        var store = new LabViewerProfileStore(_dir);
        var firstPin = new string('A', 64);
        var changedPin = new string('B', 64);
        Assert.Null(store.FindPasswordEndpoint("192.168.10.30", 15545));
        var first = store.RememberQuickPasswordEndpoint("Line 1", "192.168.10.30", 15545, firstPin);
        Assert.Equal(firstPin, first.ServerSha256);
        Assert.Equal(first.Id, store.FindPasswordEndpoint("192.168.10.30", 15545)?.Id);
        var repeat = store.RememberQuickPasswordEndpoint("Injected name", "192.168.10.30", 15545, firstPin);
        Assert.Equal(first.Id, repeat.Id);
        Assert.Equal("Line 1", repeat.Name);

        Assert.Throws<InvalidOperationException>(() =>
            store.RememberQuickPasswordEndpoint("Hijack", "192.168.10.30", 15545, changedPin));
        Assert.Equal(firstPin, store.FindPasswordEndpoint("192.168.10.30", 15545)?.ServerSha256);
        Assert.Null(store.FindPasswordEndpoint("192.168.10.31", 15545));
        Assert.Null(store.FindPasswordEndpoint("192.168.10.30", 15546));
        Assert.Throws<InvalidDataException>(() => store.FindPasswordEndpoint("bad/host", 15545));

        // The same public certificate may be on a separately authorized host,
        // but that host needs its OWN explicit first-time approval.
        var second = store.RememberQuickPasswordEndpoint("Line 2", "192.168.10.31", 15545, firstPin);
        Assert.NotEqual(first.Id, second.Id);
        string saved = File.ReadAllText(store.CatalogPath);
        Assert.DoesNotContain(Password, saved);
        Assert.DoesNotContain("Password\\u0022:", saved);
        Assert.Equal(2, store.Load().Profiles.Count);
    }

    [Fact]
    public void PreexistingConflictingEndpointPinsFailClosed()
    {
        var store = new LabViewerProfileStore(_dir);
        const string address = "10.0.1.5";
        store.RegisterPasswordProfile("First", address, 15545, new string('C', 64));
        store.RegisterPasswordProfile("Second", address, 15545, new string('D', 64));
        Assert.Throws<InvalidDataException>(() => store.FindPasswordEndpoint(address, 15545));
        Assert.Throws<InvalidOperationException>(() => store.RememberQuickPasswordEndpoint(
            "Quick", address, 15545, new string('C', 64)));
    }

    [Fact]
    public async Task FingerprintPreflightClosesTlsWithoutSendingAnyApplicationData()
    {
        using var serverCert = MakeCertificate();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = new SslStream(incoming.GetStream(), false);
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = serverCert, ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, timeout.Token);
            using var brief = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            brief.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                // Not even a Hello or PasswordLogin may be sent during preflight.
                await LabWire.ReadAsync(stream, brief.Token, LabWire.MaxPasswordPacketBytes);
                return false;
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or
                                         OperationCanceledException)
            {
                return true;
            }
        }, timeout.Token);

        string sha = await LabQuickPasswordTrust.ProbeServerIdentityAsync(
            "127.0.0.1", port, timeout.Token);
        Assert.Equal(LabCertificates.Hash(serverCert), sha);
        Assert.True(await task);
        Assert.Null(new LabViewerProfileStore(_dir).FindPasswordEndpoint("127.0.0.1", port));
    }

    [Fact]
    public async Task FirstUseRealTlsPasswordFlowWithoutClientCertificateSavesOnlyAfterWelcome()
    {
        using var serverCert = MakeCertificate();
        var sha = LabCertificates.Hash(serverCert);
        var serverPassword = new LabPasswordAccess(_dir);
        serverPassword.Configure(Password, canControl: false);
        var store = new LabViewerProfileStore(Path.Combine(_dir, "viewer"));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = cts.Token;

        var accepted = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync(token);
            using var ssl = new SslStream(incoming.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = serverCert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            Assert.True(ssl.IsEncrypted);
            Assert.False(ssl.IsMutuallyAuthenticated);
            var login = await LabWire.ReadAsync(ssl, token, LabWire.MaxPasswordPacketBytes);
            Assert.Equal(LabMessage.PasswordLogin, login.Type);
            Assert.True(serverPassword.Verify(LabWire.ParsePasswordLogin(login.Payload)));
            var hello = await LabWire.ReadAsync(ssl, token, LabWire.MaxControlPacketBytes);
            Assert.Equal(LabMessage.Hello, hello.Type);
            using var sender = new SemaphoreSlim(1);
            await LabWire.WriteAsync(ssl, sender, LabMessage.Welcome,
                LabWire.Welcome(new LabWelcome(LabRole.ViewOnly, 1024, 768, 0, 0)), token);
        }, token);

        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, token);
        using var tls = new SslStream(tcp.GetStream(), false);
        string? observedSha = null;
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "localhost",
            ClientCertificates = null,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) => {
                observedSha = LabQuickPasswordTrust.ExamineServer(cert, sha);
                return observedSha is not null;
            }
        }, token);
        Assert.True(tls.IsEncrypted);
        Assert.False(tls.IsMutuallyAuthenticated);
        Assert.Null(store.FindPasswordEndpoint("127.0.0.1", port));
        Assert.False(LabQuickPasswordTrust.MaySendPassword(
            tls.IsAuthenticated, tls.IsEncrypted, observedSha, false));
        // Simulates a user checking the SHA fingerprint on the Server PC.
        Assert.Equal(sha, observedSha);
        Assert.True(LabQuickPasswordTrust.MaySendPassword(
            tls.IsAuthenticated, tls.IsEncrypted, observedSha, true));

        using var send = new SemaphoreSlim(1);
        byte[] message = LabWire.PasswordLogin(Password);
        try { await LabWire.WriteAsync(tls, send, LabMessage.PasswordLogin, message, token); }
        finally { CryptographicOperations.ZeroMemory(message); }
        await LabWire.WriteAsync(tls, send, LabMessage.Hello,
                                 new[] { LabWire.Version }, token);
        var welcome = await LabWire.ReadAsync(tls, token);
        Assert.Equal(LabMessage.Welcome, welcome.Type);
        Assert.Equal(LabRole.ViewOnly, LabWire.ParseWelcome(welcome.Payload).Role);
        var saved = store.RememberQuickPasswordEndpoint("Loopback", "127.0.0.1",
                                                       port, observedSha!);
        Assert.Equal(sha, saved.ServerSha256);
        Assert.DoesNotContain(Password, File.ReadAllText(store.CatalogPath));
        await accepted;
    }

    [Fact]
    public async Task ChangedCertificateFailsTlsBeforePasswordCanBeSent()
    {
        using var serverCert = MakeCertificate();
        using var savedCert = MakeCertificate();
        var knownPin = LabCertificates.Hash(savedCert);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var token = cts.Token;

        var server = Task.Run(async () =>
        {
            using var tcp = await listener.AcceptTcpClientAsync(token);
            using var ssl = new SslStream(tcp.GetStream(), false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, token);
                // Schannel may finish the SERVER handshake before it sees
                // the client's rejected-certificate alert. Never infer that
                // server-side AuthenticateAsServer success means authentication
                // on the client succeeded; assert no PasswordLogin was sent.
                using var waitForSecret = CancellationTokenSource.CreateLinkedTokenSource(token);
                waitForSecret.CancelAfter(TimeSpan.FromSeconds(2));
                var packet = await LabWire.ReadAsync(ssl, waitForSecret.Token,
                                                    LabWire.MaxPasswordPacketBytes);
                return packet.Type != LabMessage.PasswordLogin;
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or
                                         OperationCanceledException)
            {
                return true; // TLS rejected or no post-handshake secret sent
            }
        }, token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port, token);
        using var tls = new SslStream(client.GetStream(), false);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "localhost",
                ClientCertificates = null,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    LabQuickPasswordTrust.ExamineServer(cert, knownPin) is not null
            }, token));
        Assert.True(await server);
    }
}
