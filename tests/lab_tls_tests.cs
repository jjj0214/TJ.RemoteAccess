// SPDX-License-Identifier: GPL-2.0-or-later
// Pure loopback TLS test; does not launch any VNC/Windows service.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabTlsTests
{
    private static X509Certificate2 NewSelfSigned(RSA key, string eku)
    {
        var request = new CertificateRequest("CN=TJRA-Lab-unit-test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        if (eku == LabCertificates.ServerEku) {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
        }
        var usages = new OidCollection { new(eku) };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
                                         DateTimeOffset.UtcNow.AddMinutes(30));
        string password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var pfx = ephemeral.Export(X509ContentType.Pfx, password);
        // Schannel on Windows cannot use an ephemeral private key for TLS.
#pragma warning disable SYSLIB0057
        return new X509Certificate2(pfx, password, X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
    }

    [Fact]
    public void PinMustMatchSha256AndEkuAndValidity()
    {
        using var rsa = RSA.Create(2048);
        using var server = NewSelfSigned(rsa, LabCertificates.ServerEku);
        var hash = LabCertificates.Hash(server);
        Assert.True(LabCertificates.ValidatePinned(server, hash, LabCertificates.ServerEku));
        Assert.False(LabCertificates.ValidatePinned(server, hash, LabCertificates.ClientEku));
        Assert.False(LabCertificates.ValidatePinned(server, new string('A', 64),
                                                     LabCertificates.ServerEku));
        Assert.False(LabCertificates.ValidatePinned(server, "garbage",
                                                     LabCertificates.ServerEku));
        Assert.False(LabCertificates.ValidatePinned(null, hash, LabCertificates.ServerEku));
    }

    [Fact]
    public async Task MutualTlsOverLoopbackAndFirstProtocolExchange()
    {
        using var serverRsa = RSA.Create(2048);
        using var clientRsa = RSA.Create(2048);
        using var serverCertificate = NewSelfSigned(serverRsa, LabCertificates.ServerEku);
        using var clientCertificate = NewSelfSigned(clientRsa, LabCertificates.ClientEku);
        string serverPin = LabCertificates.Hash(serverCertificate);
        string clientPin = LabCertificates.Hash(clientCertificate);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var local = (IPEndPoint)listener.LocalEndpoint;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cts.Token;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptTcpClientAsync(token);
            using var ssl = new SslStream(accepted.GetStream(), false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificate = serverCertificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    LabCertificates.ValidatePinned(cert, clientPin, LabCertificates.ClientEku)
            }, token);
            Assert.True(ssl.IsAuthenticated);
            Assert.True(ssl.IsEncrypted);
            Assert.True(ssl.IsMutuallyAuthenticated);
            var hello = await LabWire.ReadAsync(ssl, token);
            Assert.Equal(LabMessage.Hello, hello.Type);
            Assert.Equal(new byte[] { LabWire.Version }, hello.Payload);
            using var writer = new SemaphoreSlim(1);
            await LabWire.WriteAsync(ssl, writer, LabMessage.Welcome,
                LabWire.Welcome(new LabWelcome(LabRole.Controller, 1920, 1080, 0, 0)),
                token);
        }, token);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, local.Port, token);
        using var stream = new SslStream(client.GetStream(), false);
        try {
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
            TargetHost = "localhost",
            ClientCertificates = new X509CertificateCollection { clientCertificate },
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            RemoteCertificateValidationCallback = (_, cert, _, _) =>
                LabCertificates.ValidatePinned(cert, serverPin, LabCertificates.ServerEku)
        }, token);
        } catch (Exception clientError) {
            try { await serverTask; }
            catch (Exception serverError) {
                throw new Exception("Client handshake: " + clientError.Message +
                    "; Server handshake: " + serverError, clientError);
            }
            throw;
        }
        Assert.True(stream.IsEncrypted);
        Assert.True(stream.IsMutuallyAuthenticated);
        using var send = new SemaphoreSlim(1);
        await LabWire.WriteAsync(stream, send, LabMessage.Hello,
            new byte[] { LabWire.Version }, token);
        var welcome = await LabWire.ReadAsync(stream, token);
        Assert.Equal(LabMessage.Welcome, welcome.Type);
        Assert.Equal(LabRole.Controller, LabWire.ParseWelcome(welcome.Payload).Role);
        await serverTask;
    }
[Fact]
    public async Task RejectedClientPinCannotCompleteServerTlsAuthentication()
    {
        using var serverRsa = RSA.Create(2048);
        using var clientRsa = RSA.Create(2048);
        using var serverCert = NewSelfSigned(serverRsa, LabCertificates.ServerEku);
        using var clientCert = NewSelfSigned(clientRsa, LabCertificates.ClientEku);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = cts.Token;
        var serverTask = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync(token);
            using var stream = new SslStream(incoming.GetStream(), false);
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = serverCert,
                    ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        LabCertificates.ValidatePinned(cert, new string('F', 64),
                                                       LabCertificates.ClientEku)
                }, token);
                return false;
            }
            catch (AuthenticationException) { return true; }
            catch (IOException) { return true; }
        }, token);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, endpoint.Port, token);
        using var clientStream = new SslStream(client.GetStream(), false);
        try
        {
            await clientStream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions {
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
        Assert.True(await serverTask, "A non-allowlisted client must be refused by Schannel.");
    }
}
