// SPDX-License-Identifier: GPL-2.0-or-later
// Real Windows Schannel TLS loopback tests for the server's per-Viewer roster.
// Neither desktop capture nor a Windows Service is started by these tests.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabClientTlsAuthorizationTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "tjra-client-tls-" + Guid.NewGuid().ToString("N"));
    private LabServerClientRoster Roster => new(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static X509Certificate2 Certificate(bool server)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=lab-roster-tls-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(server ? LabCertificates.ServerEku : LabCertificates.ClientEku) },
            critical: true));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
        }
        using var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddHours(1));
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var pfx = cert.Export(X509ContentType.Pfx, password);
        try
        {
#pragma warning disable SYSLIB0057
            return new X509Certificate2(pfx, password, X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static async Task<bool> TryHandshake(
        LabServerClientRoster roster, X509Certificate2 serverCert, X509Certificate2 clientCert)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        var serverTask = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync(token);
            using var ssl = new SslStream(incoming.GetStream(), false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        roster.IsCertificateAllowed(cert)
                }, token);
                return ssl.IsMutuallyAuthenticated && ssl.IsEncrypted;
            }
            catch (AuthenticationException) { return false; }
            catch (IOException) { return false; }
        }, token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        using var sslClient = new SslStream(client.GetStream(), false);
        try
        {
            await sslClient.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { clientCert },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    LabCertificates.ValidatePinned(cert, LabCertificates.Hash(serverCert),
                                                   LabCertificates.ServerEku)
            }, token);
        }
        catch (AuthenticationException) { }
        catch (IOException) { }
        return await serverTask;
    }

    [Fact]
    public async Task UnknownClientFailsTlsThenIndividuallyGrantedClientSucceeds()
    {
        using var server = Certificate(server: true);
        using var viewer = Certificate(server: false);
        var roster = Roster;
        Assert.False(await TryHandshake(roster, server, viewer));
        roster.Register("Viewer A", LabCertificates.Hash(viewer), canControl: true);
        Assert.True(await TryHandshake(roster, server, viewer));
    }

    [Fact]
    public async Task RevokedClientFailsNewTlsButOtherClientStillWorks()
    {
        using var server = Certificate(server: true);
        using var one = Certificate(server: false);
        using var two = Certificate(server: false);
        var roster = Roster;
        var first = roster.Register("A", LabCertificates.Hash(one), canControl: true);
        roster.Register("B", LabCertificates.Hash(two), canControl: false);
        Assert.True(await TryHandshake(roster, server, one));
        Assert.True(await TryHandshake(roster, server, two));
        roster.SetEnabled(first.Id, false);
        Assert.False(await TryHandshake(roster, server, one));
        Assert.True(await TryHandshake(roster, server, two));
    }

    [Fact]
    public async Task ViewOnlyPolicyStillRequiresValidMutualTls()
    {
        using var server = Certificate(server: true);
        using var viewer = Certificate(server: false);
        var roster = Roster;
        var row = roster.Register("Only watch", LabCertificates.Hash(viewer), canControl: false);
        Assert.False(roster.GetAuthorized(LabCertificates.Hash(viewer))!.CanControl);
        Assert.True(await TryHandshake(roster, server, viewer));
        roster.SetEnabled(row.Id, false);
        Assert.False(await TryHandshake(roster, server, viewer));
    }
}
