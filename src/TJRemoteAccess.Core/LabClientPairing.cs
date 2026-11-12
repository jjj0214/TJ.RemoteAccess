// SPDX-License-Identifier: GPL-2.0-or-later
// One unique, short-lived client certificate per Viewer. Its private key is
// never added to the SERVER certificate store. Only an encrypted PFX bundle
// is transferred manually to the intended Viewer.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TJ.RemoteAccess.Lab;

internal sealed record LabClientPairingResult(
    string Folder, string PfxPassword, LabServerClient Client);

internal static class LabClientPairing
{
    internal static LabClientPairingResult Create(
        string name, LabServerSettings serverSettings, X509Certificate2 serverCertificate,
        LabServerClientRoster roster, string? baseDirectory = null, bool canControl = true)
    {
        if (serverSettings is null || serverCertificate is null || roster is null)
            throw new ArgumentNullException(nameof(serverSettings));
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 ||
            name.Any(char.IsControl))
            throw new InvalidDataException("Viewer name is required (max 80 characters).");
        if (!LabCertificates.ValidatePinned(serverCertificate,
                LabCertificates.Hash(serverCertificate), LabCertificates.ServerEku) ||
            !string.Equals(serverCertificate.Thumbprint,
                           serverSettings.ServerCertificateThumbprint,
                           StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Current Server certificate is invalid or mismatched.");
        var folder = Path.Combine(
            baseDirectory ?? Path.Combine(LabCertificates.ConfigDirectory, "Pairing"),
            "Viewer-" + Guid.NewGuid().ToString("N"));
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            "CN=TJRA-Lab-Viewer-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)),
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(LabCertificates.ClientEku) }, critical: true));
        using var client = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        string pin = LabCertificates.Hash(client);
        if (!LabCertificates.ValidatePinned(client, pin, LabCertificates.ClientEku))
            throw new CryptographicException("Generated Viewer certificate failed validation.");
        // Pre-check the existing catalog before generating transfer artifacts.
        if (roster.Load().Clients.Count >= 128)
            throw new InvalidOperationException("Server client roster is full.");
        Directory.CreateDirectory(folder);
        bool registered = false;
        try
        {
            var pfxPath = Path.Combine(folder, "client-transfer.pfx");
            var pfx = client.Export(X509ContentType.Pfx, password);
            try { File.WriteAllBytes(pfxPath, pfx); }
            finally { CryptographicOperations.ZeroMemory(pfx); }
            LabCertificates.WriteSettings(Path.Combine(folder, "viewer.json"),
                new LabViewerSettings {
                    Host = serverSettings.ListenAddress, Port = serverSettings.Port,
                    ClientCertificateThumbprint = client.Thumbprint,
                    ServerSha256 = LabCertificates.Hash(serverCertificate)
                });
            File.WriteAllBytes(Path.Combine(folder, "server-public.cer"),
                serverCertificate.Export(X509ContentType.Cert));
            // Authorization is granted only AFTER the transfer bundle is complete.
            var entry = roster.Register(name, pin, canControl);
            registered = true;
            return new LabClientPairingResult(folder, password, entry);
        }
        finally
        {
            if (!registered)
            {
                try { Directory.Delete(folder, recursive: true); }
                catch (IOException) { /* operator may manually remove a partial folder */ }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
