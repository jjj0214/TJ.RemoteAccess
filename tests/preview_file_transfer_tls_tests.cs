// SPDX-License-Identifier: GPL-2.0-or-later
// Actual in-memory TCP + pinned mutual TLS file transfer without showing WinForms UI.
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class PreviewFileTransferTlsTests
{
    private static X509Certificate2 TestCertificate(bool isServer)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=TJRA-file-transfer-test", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(isServer ? LabCertificates.ServerEku : LabCertificates.ClientEku) },
            true));
        if (isServer)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            req.CertificateExtensions.Add(san.Build());
        }
        using var ephemeral = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(30));
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
#pragma warning disable SYSLIB0057
        return new X509Certificate2(ephemeral.Export(X509ContentType.Pfx, password),
            password, X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileCopyWorksOverPinnedMutualTlsInEitherDirection(bool download)
    {
        string folder = Path.Combine(Path.GetTempPath(),
            "TJRA-File-TLS-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string input = Path.Combine(folder, "source.bin");
        string output = Path.Combine(folder, "received.bin");
        byte[] bytes = RandomNumberGenerator.GetBytes(145_123);
        await File.WriteAllBytesAsync(input, bytes);
        try
        {
            using var serverCert = TestCertificate(true);
            using var viewerCert = TestCertificate(false);
            var serverPin = LabCertificates.Hash(serverCert);
            var viewerPin = LabCertificates.Hash(viewerCert);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var token = deadline.Token;
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync(token);
                using var tls = new SslStream(tcp.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                    ServerCertificate = serverCert, ClientCertificateRequired = true,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        LabCertificates.ValidatePinned(cert, viewerPin, LabCertificates.ClientEku)
                }, token);
                Assert.True(tls.IsEncrypted && tls.IsMutuallyAuthenticated);
                using var writer = new SemaphoreSlim(1);
                using var peer = new LabFileTransferPeer(tls, writer, token,
                    () => true, _ => Task.FromResult<string?>(output),
                    () => Task.FromResult<string?>(input), _ => { });
                var incoming = await LabWire.ReadAsync(tls, token, LabFileTransferWire.MaxPacketBytes);
                if (download) Assert.Equal(LabMessage.FileRequest, incoming.Type);
                else Assert.Equal(LabMessage.FileOffer, incoming.Type);
                await peer.HandleAsync(incoming);
                if (download)
                {
                    var decision = await LabWire.ReadAsync(tls, token, LabFileTransferWire.MaxPacketBytes);
                    Assert.Equal(LabMessage.FileDecision, decision.Type);
                    await peer.HandleAsync(decision);
                    await completed.Task.WaitAsync(token);
                }
                else
                {
                    while (true)
                    {
                        var block = await LabWire.ReadAsync(tls, token, LabFileTransferWire.MaxPacketBytes);
                        await peer.HandleAsync(block);
                        if (block.Type == LabMessage.FileDone) break;
                    }
                    completed.TrySetResult();
                }
            }, token);

            using var viewer = new TcpClient();
            await viewer.ConnectAsync(IPAddress.Loopback, port, token);
            using var stream = new SslStream(viewer.GetStream(), false);
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "localhost",
                ClientCertificates = new X509CertificateCollection { viewerCert },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    LabCertificates.ValidatePinned(cert, serverPin, LabCertificates.ServerEku)
            }, token);
            Assert.True(stream.IsEncrypted && stream.IsMutuallyAuthenticated);
            using var sendLock = new SemaphoreSlim(1);
            using var viewerPeer = new LabFileTransferPeer(stream, sendLock, token,
                () => true, _ => Task.FromResult<string?>(output), null, _ => { });
            if (download)
            {
                await viewerPeer.RequestDownloadAsync();
                var offer = await LabWire.ReadAsync(stream, token);
                Assert.Equal(LabMessage.FileOffer, offer.Type);
                await viewerPeer.HandleAsync(offer);
                while (true)
                {
                    var block = await LabWire.ReadAsync(stream, token);
                    await viewerPeer.HandleAsync(block);
                    if (block.Type == LabMessage.FileDone) break;
                }
                completed.TrySetResult();
            }
            else
            {
                await viewerPeer.OfferFileAsync(input);
                var decision = await LabWire.ReadAsync(stream, token);
                Assert.Equal(LabMessage.FileDecision, decision.Type);
                await viewerPeer.HandleAsync(decision);
                await completed.Task.WaitAsync(token);
            }
            await serverTask;
            Assert.Equal(bytes, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
