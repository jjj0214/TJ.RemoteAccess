// SPDX-License-Identifier: GPL-2.0-or-later
// TJ.RemoteAccess isolated test-session certificates. NOT production PKI.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerSettings
{
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 15444;
    public string ServerCertificateThumbprint { get; set; } = "";
    public string AllowedClientSha256 { get; set; } = "";
}
internal sealed class LabViewerSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 15444;
    public string ClientCertificateThumbprint { get; set; } = "";
    public string ServerSha256 { get; set; } = "";
}

internal static class LabCertificates
{
    internal const string DirectoryName = "TJ.RemoteAccess.Lab";
    internal const string ServerEku = "1.3.6.1.5.5.7.3.1";
    internal const string ClientEku = "1.3.6.1.5.5.7.3.2";
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    internal static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DirectoryName);
    internal static string ServerFile => Path.Combine(ConfigDirectory, "server.json");
    internal static string ViewerFile => Path.Combine(ConfigDirectory, "viewer.json");

    internal static T LoadSettings<T>(string filename) where T : class
    {
        if (!File.Exists(filename))
            throw new FileNotFoundException(
                $"Missing {filename}. Run TJ.RemoteAccess.Lab.Setup init first.");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(filename))
               ?? throw new InvalidDataException("Invalid configuration.");
    }

    internal static void WriteSettings<T>(string filename, T settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filename)!);
        var temp = filename + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, filename, overwrite: true);
    }

    internal static string Hash(X509Certificate certificate)
    {
        return Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    }

    internal static X509Certificate2 LoadPrivateCertificate(string localThumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint,
                                                localThumbprint, validOnly: false);
        var cert = matches.OfType<X509Certificate2>().FirstOrDefault(x => x.HasPrivateKey);
        if (cert is null)
            throw new InvalidOperationException(
                "Required local client/server certificate is not installed with a private key.");
        if (DateTime.UtcNow < cert.NotBefore.ToUniversalTime() ||
            DateTime.UtcNow > cert.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("Local certificate expired or is not yet valid.");
        return cert;
    }

    internal static bool ValidatePinned(X509Certificate? raw, string pinSha256,
                                        string requiredEku)
    {
        if (raw is null || string.IsNullOrWhiteSpace(pinSha256) || pinSha256.Length != 64)
            return false;
        try
        {
            using var cert = new X509Certificate2(raw);
            if (DateTime.UtcNow < cert.NotBefore.ToUniversalTime() ||
                DateTime.UtcNow > cert.NotAfter.ToUniversalTime())
                return false;
            if (!cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(
                    e => e.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == requiredEku)))
                return false;
            var pin = Convert.FromHexString(pinSha256);
            var actual = SHA256.HashData(cert.RawData);
            return CryptographicOperations.FixedTimeEquals(pin, actual);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException
                                   or ArgumentException)
        {
            return false;
        }
    }

    internal static X509Certificate2 GenerateCertificate(bool server)
    {
        using var rsa = RSA.Create(3072);
        string label = server ? "Server" : "Viewer";
        var request = new CertificateRequest(
            $"CN=TJRA-Lab-{label}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection();
        eku.Add(new Oid(server ? ServerEku : ClientEku));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
        }
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        string randomPass = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var pfx = ephemeral.Export(X509ContentType.Pfx, randomPass);
        // Re-import into CurrentUser\My with a persisted user-owned private key.
#pragma warning disable SYSLIB0057
        var persisted = new X509Certificate2(pfx, randomPass,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet |
            (server ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.Exportable));
#pragma warning restore SYSLIB0057
        CryptographicOperations.ZeroMemory(pfx);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    internal static void ImportPrivateCertificate(string file, string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("The pairing PFX password cannot be empty.");
#pragma warning disable SYSLIB0057
        using var imported = new X509Certificate2(file, password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
#pragma warning restore SYSLIB0057
        if (!imported.HasPrivateKey || !ValidatePinned(imported, Hash(imported), ClientEku))
            throw new CryptographicException("The file is not a valid TJRA Lab viewer certificate.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(imported);
    }
}
