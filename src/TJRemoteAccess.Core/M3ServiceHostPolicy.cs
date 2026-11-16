// SPDX-License-Identifier: GPL-2.0-or-later
// Privileged service operator opt-in. The policy file must be deployed in
// the administrator-owned service installation directory, NEVER ProgramData
// or a normal user's writable folder. Default: status-only.
using System.Text.Json;

namespace TJ.RemoteAccess.M3;

internal sealed record M3ServiceHostPolicy(
    int SchemaVersion,
    bool EnableSupervisedDesktop,
    string Mode);

internal static class M3ServiceHostPolicyStore
{
    internal const string FileName = "desktop-host-policy.json";

    internal static bool AllowsDesktop(M3ServiceHostPolicy? policy) =>
        policy is {
            SchemaVersion: 1,
            EnableSupervisedDesktop: true,
            Mode: "InteractiveOwnerConsentOnly"
        };

    internal static bool IsEnabled(string? serviceDirectory = null)
    {
        string path = Path.Combine(serviceDirectory ?? AppContext.BaseDirectory, FileName);
        try
        {
            if (!File.Exists(path)) return false;
            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length > 4096) return false;
            return AllowsDesktop(JsonSerializer.Deserialize<M3ServiceHostPolicy>(
                File.ReadAllText(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or ArgumentException)
        {
            return false;
        }
    }
}
