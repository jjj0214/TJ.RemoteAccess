// SPDX-License-Identifier: GPL-2.0-or-later
// Independent, fail-closed client certificate authorization for the Lab Server.
// Server stores public SHA256 pins only; no viewer private keys in this roster.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerClient
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool CanControl { get; set; } = true;
    public DateTime CreatedUtc { get; set; }

    public LabServerClient Copy() => (LabServerClient)MemberwiseClone();
}

internal sealed class LabServerClientCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<LabServerClient> Clients { get; set; } = new();

    public LabServerClientCatalog Copy() => new() {
        SchemaVersion = SchemaVersion,
        Clients = Clients.Select(x => x.Copy()).ToList()
    };
}

internal sealed class LabServerClientRoster
{
    private readonly string _folder;
    private readonly string _legacyPin;
    private readonly object _cacheLock = new();
    private LabServerClientCatalog? _cached;
    private DateTime _lastLoadUtc;
    private const int MaxClients = 128;
    internal string CatalogPath => Path.Combine(_folder, "server-clients.json");
    private string MigrationMarkerPath => Path.Combine(_folder, "server-clients.migrated");
    private string LockPath => Path.Combine(_folder, "server-clients.lock");

    internal LabServerClientRoster(string? folder = null, string? legacyPin = null)
    {
        _folder = folder ?? LabCertificates.ConfigDirectory;
        _legacyPin = (legacyPin ?? "").Trim().ToUpperInvariant();
        if (_legacyPin.Length != 0 && !IsHex(_legacyPin, 64))
            throw new InvalidDataException("Legacy allowed client pin is malformed.");
    }

    internal LabServerClientCatalog Load() => WithExclusiveLock(ReadOrMigrate);

    // Dynamic authorization: cache for <=500 ms, including a short time for
    // failed reads. A corrupt or unreadable roster always DENIES access.
    internal LabServerClient? GetAuthorized(string certificateSha256)
    {
        if (!IsHex(certificateSha256, 64)) return null;
        lock (_cacheLock)
        {
            if (_cached is null || DateTime.UtcNow - _lastLoadUtc >= TimeSpan.FromMilliseconds(500))
            {
                _lastLoadUtc = DateTime.UtcNow;
                try { _cached = Load(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidDataException or JsonException
                                           or ArgumentException)
                {
                    _cached = new LabServerClientCatalog();
                }
            }
            var result = _cached.Clients.FirstOrDefault(x =>
                x.Enabled && string.Equals(x.CertificateSha256,
                                           certificateSha256, StringComparison.OrdinalIgnoreCase));
            return result?.Copy();
        }
    }

    internal bool IsCertificateAllowed(X509Certificate? clientCertificate)
    {
        if (clientCertificate is null) return false;
        var pin = LabCertificates.Hash(clientCertificate);
        return GetAuthorized(pin) is not null &&
               LabCertificates.ValidatePinned(clientCertificate, pin, LabCertificates.ClientEku);
    }

    internal LabServerClient Register(string name, string sha256, bool canControl)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 80 ||
            name.Any(char.IsControl) || !IsHex(sha256, 64))
            throw new InvalidDataException("Invalid client name or SHA-256 certificate fingerprint.");
        var result = WithExclusiveLock(() =>
        {
            var catalog = ReadOrMigrate();
            string pin = sha256.ToUpperInvariant();
            if (catalog.Clients.Any(x => string.Equals(x.CertificateSha256, pin,
                                                       StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Client certificate is already registered.");
            if (catalog.Clients.Count >= MaxClients)
                throw new InvalidOperationException("Client roster reached its capacity.");
            var entry = new LabServerClient {
                Id = Guid.NewGuid().ToString("N"),
                Name = name.Trim(), CertificateSha256 = pin,
                Enabled = true, CanControl = canControl,
                CreatedUtc = DateTime.UtcNow
            };
            catalog.Clients.Add(entry);
            Save(catalog);
            return entry.Copy();
        });
        InvalidateCache();
        return result;
    }

    internal LabServerClient SetEnabled(string id, bool enabled) =>
        Update(id, entry => entry.Enabled = enabled);

    internal LabServerClient SetCanControl(string id, bool canControl) =>
        Update(id, entry => entry.CanControl = canControl);

    private LabServerClient Update(string id, Action<LabServerClient> action)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Client ID must be the full identifier.", nameof(id));
        var result = WithExclusiveLock(() =>
        {
            var catalog = ReadOrMigrate();
            var entry = catalog.Clients.SingleOrDefault(x => x.Id == id) ??
                throw new KeyNotFoundException("Client identity not found.");
            action(entry);
            Save(catalog);
            return entry.Copy();
        });
        InvalidateCache();
        return result;
    }

    private LabServerClientCatalog ReadOrMigrate()
    {
        if (File.Exists(CatalogPath))
        {
            var catalog = JsonSerializer.Deserialize<LabServerClientCatalog>(
                File.ReadAllText(CatalogPath)) ??
                throw new InvalidDataException("Empty server client authorization catalog.");
            Validate(catalog);
            return catalog.Copy();
        }
        if (File.Exists(MigrationMarkerPath))
            throw new InvalidDataException("Authorization catalog missing after migration; denying all clients.");
        var first = new LabServerClientCatalog();
        if (_legacyPin.Length == 64)
        {
            first.Clients.Add(new LabServerClient {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Legacy paired Viewer",
                CertificateSha256 = _legacyPin,
                Enabled = true, CanControl = true,
                CreatedUtc = DateTime.UtcNow
            });
        }
        // Marker first: a missing active catalog must not revive revoked legacy pins.
        File.WriteAllText(MigrationMarkerPath, "migrated-v1");
        // Manual recovery from backup requires a deliberate operator action.
        Save(first); // Explicit catalog replaces the original single-pin fallback.
        return first.Copy();
    }

    private void Save(LabServerClientCatalog catalog)
    {
        Validate(catalog);
        Directory.CreateDirectory(_folder);
        string temp = CatalogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                                           FileShare.None))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
                writer.Write(JsonSerializer.Serialize(catalog, LabCertificates.JsonOptions));
            // Keep a recovery copy for an operator, but never trust it as a
            // fallback when the active catalog is corrupt/missing.
            if (File.Exists(CatalogPath))
                File.Copy(CatalogPath, CatalogPath + ".bak", overwrite: true);
            File.Move(temp, CatalogPath, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void Validate(LabServerClientCatalog catalog)
    {
        if (catalog.SchemaVersion != 1 || catalog.Clients is null ||
            catalog.Clients.Count > MaxClients)
            throw new InvalidDataException("Unknown/oversized server client catalog.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var pins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in catalog.Clients)
        {
            if (!Guid.TryParseExact(entry.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(entry.Name) ||
                entry.Name != entry.Name.Trim() || entry.Name.Length > 80 ||
                entry.Name.Any(char.IsControl) ||
                !IsHex(entry.CertificateSha256, 64) ||
                !ids.Add(entry.Id) || !pins.Add(entry.CertificateSha256) ||
                entry.CreatedUtc == default)
                throw new InvalidDataException("Invalid/duplicate server client entry.");
        }
    }

    private static bool IsHex(string? text, int length) =>
        text?.Length == length && text.All(c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private void InvalidateCache()
    {
        lock (_cacheLock) { _cached = null; }
    }

    private T WithExclusiveLock<T>(Func<T> action)
    {
        Directory.CreateDirectory(_folder);
        FileStream? fileLock = null;
        for (int i = 0; i < 40; ++i)
        {
            try
            {
                fileLock = new FileStream(LockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException) when (i < 39) { Thread.Sleep(50); }
        }
        using (fileLock ?? throw new IOException("Server client catalog locked."))
            return action();
    }
}
