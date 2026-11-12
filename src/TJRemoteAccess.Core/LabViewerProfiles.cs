// SPDX-License-Identifier: GPL-2.0-or-later
// Current-user profile catalog. Only public certificate fingerprints are stored.
// Private keys stay in the Windows CurrentUser\My certificate store.
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabViewerProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 15444;
    public string ClientCertificateThumbprint { get; set; } = "";
    public string ServerSha256 { get; set; } = "";

    public LabViewerProfile Copy() => (LabViewerProfile)MemberwiseClone();
    public override string ToString() => $"{Name}  ({Host}:{Port})";
}

internal sealed class LabViewerProfileCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public string SelectedProfileId { get; set; } = "";
    public List<LabViewerProfile> Profiles { get; set; } = new();

    public LabViewerProfileCatalog Copy() => new()
    {
        SchemaVersion = SchemaVersion,
        SelectedProfileId = SelectedProfileId,
        Profiles = Profiles.Select(p => p.Copy()).ToList()
    };
}

internal sealed class LabViewerProfileStore
{
    private readonly string _directory;
    internal string CatalogPath => Path.Combine(_directory, "viewer-profiles.json");
    internal string LegacyViewerPath => Path.Combine(_directory, "viewer.json");
    private string LockPath => Path.Combine(_directory, "viewer-profiles.lock");
    internal LabViewerProfileStore(string? directory = null) =>
        _directory = directory ?? LabCertificates.ConfigDirectory;

    internal LabViewerProfileCatalog Load() =>
        WithExclusiveLock(() => ReadOrMigrate().Copy());

    internal LabViewerProfile RegisterProvisioned(LabViewerSettings settings,
                                                  string? displayName = null,
                                                  string? hostOverride = null)
    {
        var profile = new LabViewerProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(displayName)
                ? $"Server {settings.ServerSha256[^Math.Min(8, settings.ServerSha256.Length)..]}"
                : displayName.Trim(),
            Host = string.IsNullOrWhiteSpace(hostOverride) ? settings.Host.Trim() : hostOverride.Trim(),
            Port = settings.Port,
            ClientCertificateThumbprint = settings.ClientCertificateThumbprint.ToUpperInvariant(),
            ServerSha256 = settings.ServerSha256.ToUpperInvariant()
        };
        ValidateProfile(profile);
        return WithExclusiveLock(() =>
        {
            var catalog = ReadOrMigrate();
            // Re-importing the same pinned server and client identity is
            // idempotent; never silently change an existing fingerprint.
            var existing = catalog.Profiles.FirstOrDefault(p =>
                string.Equals(p.ServerSha256, profile.ServerSha256,
                              StringComparison.OrdinalIgnoreCase) &&
                string.Equals(p.ClientCertificateThumbprint,
                              profile.ClientCertificateThumbprint,
                              StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                catalog.SelectedProfileId = existing.Id;
                Save(catalog);
                return existing.Copy();
            }
            catalog.Profiles.Add(profile);
            catalog.SelectedProfileId = profile.Id;
            Save(catalog);
            return profile.Copy();
        });
    }

    internal LabViewerProfile ImportPairing(string pfxPath, string password,
                                            string? name = null, string? host = null)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidDataException("Client PFX password is required.");
        var fullPath = Path.GetFullPath(pfxPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Pairing PFX not found.", fullPath);
        string viewerJson = Path.Combine(Path.GetDirectoryName(fullPath)!, "viewer.json");
        var settings = LabCertificates.LoadSettings<LabViewerSettings>(viewerJson);
        // Validate the intended profile before any private key is installed.
        var candidate = new LabViewerProfile {
            Id = Guid.NewGuid().ToString("N"),
            Name = string.IsNullOrWhiteSpace(name) ? "Paired server" : name.Trim(),
            Host = string.IsNullOrWhiteSpace(host) ? settings.Host : host.Trim(),
            Port = settings.Port,
            ClientCertificateThumbprint = settings.ClientCertificateThumbprint,
            ServerSha256 = settings.ServerSha256
        };
        ValidateProfile(candidate);
        string publicCertificate = Path.Combine(Path.GetDirectoryName(fullPath)!, "server-public.cer");
        if (File.Exists(publicCertificate))
        {
            using var serverCert = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(publicCertificate));
            if (!LabCertificates.ValidatePinned(serverCert, settings.ServerSha256,
                                                LabCertificates.ServerEku))
                throw new CryptographicException("Pairing server certificate does not match viewer.json.");
        }
        // Validate the bundle BEFORE importing the client private key or
        // modifying any stored profile. A spoofed JSON/PFX mismatch is fatal.
#pragma warning disable SYSLIB0057
        using var supplied = new X509Certificate2(
            fullPath, password, X509KeyStorageFlags.EphemeralKeySet);
#pragma warning restore SYSLIB0057
        if (!supplied.HasPrivateKey ||
            !LabCertificates.ValidatePinned(supplied, LabCertificates.Hash(supplied),
                                            LabCertificates.ClientEku) ||
            !string.Equals(supplied.Thumbprint, settings.ClientCertificateThumbprint,
                           StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Pairing client certificate does not match viewer.json.");

        // Preserve existing certificates (and their dependent profiles).
        bool alreadyInstalled;
        using (var store = new X509Store(StoreName.My, StoreLocation.CurrentUser))
        {
            store.Open(OpenFlags.ReadOnly);
            alreadyInstalled = store.Certificates
                .Find(X509FindType.FindByThumbprint, settings.ClientCertificateThumbprint, false)
                .OfType<X509Certificate2>()
                .Any(c => c.HasPrivateKey &&
                          LabCertificates.ValidatePinned(c, LabCertificates.Hash(c),
                                                        LabCertificates.ClientEku));
        }
        if (!alreadyInstalled)
            LabCertificates.ImportPrivateCertificate(fullPath, password);
        return RegisterProvisioned(settings, name, host);
    }

    internal LabViewerProfile Edit(string profileId, string name, string host, int port)
    {
        return WithExclusiveLock(() =>
        {
            var catalog = ReadOrMigrate();
            var profile = catalog.Profiles.SingleOrDefault(p => p.Id == profileId) ??
                          throw new KeyNotFoundException("Selected viewer profile was removed.");
            profile.Name = name.Trim();
            profile.Host = host.Trim();
            profile.Port = port;
            ValidateProfile(profile);
            Save(catalog);
            return profile.Copy();
        });
    }

    internal void Select(string profileId)
    {
        WithExclusiveLock(() =>
        {
            var catalog = ReadOrMigrate();
            if (!catalog.Profiles.Any(p => p.Id == profileId))
                throw new KeyNotFoundException("Selected viewer profile no longer exists.");
            if (catalog.SelectedProfileId != profileId)
            {
                catalog.SelectedProfileId = profileId;
                Save(catalog);
            }
            return 0;
        });
    }

    internal void Remove(string profileId)
    {
        WithExclusiveLock(() =>
        {
            var catalog = ReadOrMigrate();
            int count = catalog.Profiles.RemoveAll(p => p.Id == profileId);
            if (count == 0)
                throw new KeyNotFoundException("Selected viewer profile no longer exists.");
            if (catalog.SelectedProfileId == profileId)
                catalog.SelectedProfileId = catalog.Profiles.FirstOrDefault()?.Id ?? "";
            Save(catalog);
            return 0;
        });
        // Intentionally keep CurrentUser certificate and legacy viewer.json.
        // Other programs/profiles might still depend on the same identity.
    }

    private LabViewerProfileCatalog ReadOrMigrate()
    {
        if (File.Exists(CatalogPath))
        {
            var json = File.ReadAllText(CatalogPath);
            var catalog = JsonSerializer.Deserialize<LabViewerProfileCatalog>(json)
                ?? throw new InvalidDataException("Viewer profile catalog is empty.");
            ValidateCatalog(catalog);
            return catalog;
        }
        var result = new LabViewerProfileCatalog();
        var candidates = new List<string>();
        if (File.Exists(LegacyViewerPath))
            candidates.Add(LegacyViewerPath);
        // Recover profiles backed up by the old manual workaround:
        // viewer.json.backup-yyyyMMdd-HHmmss. These files are never modified.
        candidates.AddRange(Directory.EnumerateFiles(_directory, "viewer.json.backup-*")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        foreach (string file in candidates)
        {
            try
            {
                var legacy = LabCertificates.LoadSettings<LabViewerSettings>(file);
                bool isCurrent = file == LegacyViewerPath;
                string suffix = isCurrent ? "" : Path.GetFileName(file)["viewer.json.backup-".Length..];
                if (suffix.Length > 32) suffix = suffix[..32];
                var profile = new LabViewerProfile
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = isCurrent ? "Previous server" : "Recovered server " + suffix,
                    Host = legacy.Host,
                    Port = legacy.Port,
                    ClientCertificateThumbprint = legacy.ClientCertificateThumbprint,
                    ServerSha256 = legacy.ServerSha256
                };
                ValidateProfile(profile);
                if (result.Profiles.Any(existing =>
                    string.Equals(existing.ServerSha256, profile.ServerSha256,
                                  StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.ClientCertificateThumbprint,
                                  profile.ClientCertificateThumbprint,
                                  StringComparison.OrdinalIgnoreCase)))
                    continue;
                result.Profiles.Add(profile);
                if (result.SelectedProfileId.Length == 0)
                    result.SelectedProfileId = profile.Id;
            }
            catch (Exception ex) when (file != LegacyViewerPath &&
                    (ex is IOException or InvalidDataException or UnauthorizedAccessException
                        or ArgumentException or System.Text.Json.JsonException))
            {
                // An invalid OLD backup must never insert a new trusted identity.
                // A broken CURRENT viewer.json is still fatal, not silently trusted.
                continue;
            }
        }
        if (result.Profiles.Count > 0)
            Save(result);
        return result;
    }

    private static void ValidateCatalog(LabViewerProfileCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Profiles is null ||
            catalog.Profiles.Count > 256)
            throw new InvalidDataException("Unsupported or oversized viewer profile catalog.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in catalog.Profiles)
        {
            ValidateProfile(profile);
            if (!ids.Add(profile.Id))
                throw new InvalidDataException("Duplicate viewer profile ID.");
        }
        if (catalog.Profiles.Count == 0 && catalog.SelectedProfileId != "")
            throw new InvalidDataException("Viewer profile selection is invalid.");
        if (catalog.Profiles.Count != 0 &&
            !catalog.Profiles.Any(p => p.Id == catalog.SelectedProfileId))
            throw new InvalidDataException("Selected viewer profile was not found.");
    }

    internal static void ValidateProfile(LabViewerProfile profile)
    {
        if (profile is null || !Guid.TryParse(profile.Id, out _) ||
            string.IsNullOrWhiteSpace(profile.Name) ||
            profile.Name.Length > 80 ||
            profile.Name.Any(char.IsControl) ||
            !ValidHost(profile.Host) ||
            profile.Port is < 1024 or > 65535 ||
            !Hex(profile.ClientCertificateThumbprint, 40) ||
            !Hex(profile.ServerSha256, 64))
            throw new InvalidDataException("Invalid profile name, endpoint or TLS identity.");
    }

    private static bool Hex(string? text, int length) => text?.Length == length &&
        text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static bool ValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            host != host.Trim() || host.Any(char.IsWhiteSpace) ||
            host.Contains('/') || host.Contains('\\') || host.Contains('@'))
            return false;
        return IPAddress.TryParse(host, out _) ||
               Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private void Save(LabViewerProfileCatalog catalog)
    {
        ValidateCatalog(catalog);
        Directory.CreateDirectory(_directory);
        string temp = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                                               FileShare.None, bufferSize: 4096))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(catalog, LabCertificates.JsonOptions));
            if (File.Exists(CatalogPath))
                File.Copy(CatalogPath, CatalogPath + ".bak", overwrite: true);
            File.Move(temp, CatalogPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private T WithExclusiveLock<T>(Func<T> action)
    {
        Directory.CreateDirectory(_directory);
        FileStream? fileLock = null;
        for (int i = 0; i < 40; ++i)
        {
            try
            {
                fileLock = new FileStream(LockPath, FileMode.OpenOrCreate,
                                          FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (i < 39)
            {
                Thread.Sleep(50);
            }
        }
        using (fileLock ?? throw new IOException("Viewer profile catalog is locked."))
            return action();
    }
}
