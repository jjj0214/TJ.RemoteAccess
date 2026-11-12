// SPDX-License-Identifier: GPL-2.0-or-later
// An OPTIONAL password identity for a separate TLS-only LAB listener.
// Never disables TLS, never provides raw RFB/VNC access, never stores the password.
// Viewer must pin the Server certificate out of band before sending a secret.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabPasswordSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; }
    // Password access is ViewOnly by default. Never inferred from a cert profile.
    public bool CanControl { get; set; }
    public int Iterations { get; set; } = LabPasswordAccess.Iterations;
    public string SaltBase64 { get; set; } = "";
    public string VerifierBase64 { get; set; } = "";
}

internal sealed class LabPasswordAccess
{
    internal const int Iterations = 600_000;
    internal const int MinPasswordCharacters = 12;
    internal const int MaxPasswordCharacters = 128;
    internal const int MaxPasswordBytes = 256;
    private readonly string _directory;
    private readonly object _cacheGuard = new();
    private LabPasswordSettings? _cached;
    private DateTime _lastReadUtc;
    internal string SettingsFile => Path.Combine(_directory, "server-password.json");
    private string LockFile => Path.Combine(_directory, "server-password.lock");

    internal LabPasswordAccess(string? directory = null) =>
        _directory = directory ?? LabCertificates.ConfigDirectory;

    internal LabPasswordSettings Current()
    {
        lock (_cacheGuard)
        {
            if (_lastReadUtc == default ||
                DateTime.UtcNow - _lastReadUtc > TimeSpan.FromMilliseconds(500))
            {
                _lastReadUtc = DateTime.UtcNow;
                try
                {
                    _cached = Read();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or JsonException or FormatException or InvalidDataException)
                {
                    // A corrupt or missing password policy always denies admission.
                    _cached = null;
                }
            }
            return _cached is null ? new LabPasswordSettings() : Copy(_cached);
        }
    }

    internal bool Verify(string candidate) => TryAuthenticate(candidate, out _);

    internal bool TryAuthenticate(string candidate, out LabPasswordSettings authorized)
    {
        var config = Current();
        authorized = new LabPasswordSettings();
        if (!config.Enabled || !ValidPassword(candidate)) return false;
        byte[] salt = Convert.FromBase64String(config.SaltBase64);
        byte[] expected = Convert.FromBase64String(config.VerifierBase64);
        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(
            candidate, salt, config.Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, computed)) return false;
            authorized = config;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(computed);
            CryptographicOperations.ZeroMemory(expected);
        }
    }

    internal bool IsSessionAuthorized(string originalVerifier, bool requireControl)
    {
        var policy = Current();
        if (!policy.Enabled || (requireControl && !policy.CanControl) ||
            string.IsNullOrEmpty(originalVerifier)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(originalVerifier),
            Encoding.ASCII.GetBytes(policy.VerifierBase64));
    }

    internal void Configure(string password, bool canControl)
    {
        if (!ValidPassword(password))
            throw new InvalidDataException("Password must be 12..128 printable characters (max 256 UTF-8 bytes).");
        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] verifier = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            Write(new LabPasswordSettings {
                Enabled = true, CanControl = canControl, Iterations = Iterations,
                SaltBase64 = Convert.ToBase64String(salt),
                VerifierBase64 = Convert.ToBase64String(verifier)
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(verifier);
        }
    }

    internal void Disable()
    {
        // Preserve the verifier to allow a deliberate new Configure; never
        // reenable an old password with a toggle after a disable.
        var previous = Current();
        previous.Enabled = false;
        Write(previous);
    }

    internal static bool ValidPassword(string? password) =>
        password is { Length: >= MinPasswordCharacters and <= MaxPasswordCharacters } &&
        !password.Any(char.IsControl) &&
        Encoding.UTF8.GetByteCount(password) <= MaxPasswordBytes;

    private LabPasswordSettings? Read()
    {
        if (!File.Exists(SettingsFile)) return null;
        var settings = JsonSerializer.Deserialize<LabPasswordSettings>(
            File.ReadAllText(SettingsFile)) ?? throw new InvalidDataException("Empty password policy.");
        Validate(settings);
        return settings;
    }

    private void Write(LabPasswordSettings settings)
    {
        Validate(settings);
        Directory.CreateDirectory(_directory);
        string temp = SettingsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var fileLock = Lock())
        {
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew,
                                                   FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(JsonSerializer.Serialize(settings, LabCertificates.JsonOptions));
                File.Move(temp, SettingsFile, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        lock (_cacheGuard)
        {
            _cached = Copy(settings);
            _lastReadUtc = DateTime.UtcNow;
        }
    }

    private static LabPasswordSettings Copy(LabPasswordSettings item) => new() {
        SchemaVersion = item.SchemaVersion, Enabled = item.Enabled,
        CanControl = item.CanControl, Iterations = item.Iterations,
        SaltBase64 = item.SaltBase64, VerifierBase64 = item.VerifierBase64
    };

    private static void Validate(LabPasswordSettings item)
    {
        if (item.SchemaVersion != 1 || item.Iterations != Iterations ||
            item.SaltBase64 is null || item.VerifierBase64 is null)
            throw new InvalidDataException("Unsupported password policy.");
        if (!item.Enabled && item.SaltBase64.Length == 0 && item.VerifierBase64.Length == 0)
            return; // Disabled before initial setup.
        try
        {
            byte[] salt = Convert.FromBase64String(item.SaltBase64);
            byte[] verifier = Convert.FromBase64String(item.VerifierBase64);
            if (salt.Length != 32 || verifier.Length != 32)
                throw new InvalidDataException("Invalid password verifier lengths.");
        }
        catch (FormatException ex) { throw new InvalidDataException("Invalid password verifier encoding.", ex); }
    }

    private FileStream Lock()
    {
        Directory.CreateDirectory(_directory);
        for (int i = 0; i < 40; ++i)
        {
            try { return new FileStream(LockFile, FileMode.OpenOrCreate,
                                        FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (i < 39) { Thread.Sleep(50); }
        }
        throw new IOException("Password configuration is locked.");
    }
}

internal sealed class LabPasswordAttemptLimiter
{
    private sealed class Counter
    {
        public DateTimeOffset WindowStart;
        public int Failures;
        public DateTimeOffset LockedUntil;
    }
    private readonly Dictionary<string, Counter> _failed = new(StringComparer.Ordinal);
    private readonly Counter _global = new();
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;

    internal LabPasswordAttemptLimiter(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    internal bool MayAttempt(string ip)
    {
        lock (_gate)
        {
            var now = _clock();
            return now >= _global.LockedUntil &&
                   (!_failed.TryGetValue(ip, out var state) || now >= state.LockedUntil);
        }
    }

    internal void Failed(string ip)
    {
        lock (_gate)
        {
            var now = _clock();
            if (now - _global.WindowStart >= TimeSpan.FromMinutes(1))
            {
                _global.WindowStart = now;
                _global.Failures = 0;
            }
            _global.Failures++;
            if (_global.Failures >= 20)
                _global.LockedUntil = now.AddMinutes(2);
            if (_failed.Count >= 256)
            {
                foreach (var old in _failed.Where(x =>
                    now - x.Value.WindowStart > TimeSpan.FromMinutes(5)).Select(x => x.Key).ToList())
                    _failed.Remove(old);
            }
            if (_failed.Count >= 256 && !_failed.ContainsKey(ip)) return; // no unbounded memory
            if (!_failed.TryGetValue(ip, out var state) ||
                now - state.WindowStart >= TimeSpan.FromMinutes(1))
            {
                state = new Counter { WindowStart = now };
                _failed[ip] = state;
            }
            state.Failures++;
            if (state.Failures >= 5)
                state.LockedUntil = now.AddMinutes(2);
        }
    }

    internal void Succeeded(string ip)
    {
        lock (_gate) { _failed.Remove(ip); }
    }
}
