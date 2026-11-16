// SPDX-License-Identifier: GPL-2.0-or-later
// Current-user enrollment for explicit, visible TLS desktop sharing.
// This is NEVER administrator policy. The service separately needs an
// administrator-owned desktop-host-policy.json to request a supervised host.
using System.Diagnostics;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text.Json;
using TJ.RemoteAccess.Lab;

namespace TJ.RemoteAccess.M3;

internal sealed record M3DesktopEnrollment(
    int SchemaVersion,
    string OwnerSid,
    string ServerCertificateSha256,
    bool AllowSupervisedStart);

internal static class M3DesktopEnrollmentStore
{
    internal const string EnrollmentName = "desktop-host-enrollment.json";
    internal static string CurrentFile =>
        Path.Combine(LabCertificates.ConfigDirectory, EnrollmentName);

    internal static bool Matches(M3DesktopEnrollment? record,
                                 string ownerSid, string serverSha256)
    {
        return record is {
                   SchemaVersion: 1,
                   AllowSupervisedStart: true
               } &&
               ownerSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) &&
               string.Equals(record.OwnerSid, ownerSid, StringComparison.Ordinal) &&
               serverSha256.Length == 64 &&
               string.Equals(record.ServerCertificateSha256, serverSha256,
                             StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsEnabledForCurrentUser()
    {
        try
        {
            if (!OperatingSystem.IsWindows() ||
                !Environment.UserInteractive ||
                Process.GetCurrentProcess().SessionId == 0 ||
                !File.Exists(CurrentFile))
                return false;
            var record = JsonSerializer.Deserialize<M3DesktopEnrollment>(
                File.ReadAllText(CurrentFile));
            using var user = WindowsIdentity.GetCurrent();
            var sid = user.User?.Value ?? "";
            var settings = LabCertificates.LoadSettings<LabServerSettings>(
                LabCertificates.ServerFile);
            using var cert = LabCertificates.LoadPrivateCertificate(
                settings.ServerCertificateThumbprint);
            return LabCertificates.ValidatePinned(cert, LabCertificates.Hash(cert),
                                                  LabCertificates.ServerEku) &&
                   Matches(record, sid, LabCertificates.Hash(cert));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or JsonException or InvalidDataException or
            System.Security.Cryptography.CryptographicException or
            InvalidOperationException)
        {
            return false;
        }
    }

    internal static void EnableForCurrentUser(bool confirmed)
    {
        if (!confirmed || !OperatingSystem.IsWindows() ||
            !Environment.UserInteractive ||
            Process.GetCurrentProcess().SessionId == 0)
            throw new AuthenticationException("Interactive local owner confirmation required.");
        using var user = WindowsIdentity.GetCurrent();
        var sid = user.User?.Value ?? "";
        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
            throw new AuthenticationException("A signed-in standard or administrator user is required.");
        var settings = LabCertificates.LoadSettings<LabServerSettings>(
            LabCertificates.ServerFile);
        using var cert = LabCertificates.LoadPrivateCertificate(
            settings.ServerCertificateThumbprint);
        if (!LabCertificates.ValidatePinned(cert, LabCertificates.Hash(cert),
                                            LabCertificates.ServerEku))
            throw new AuthenticationException("Server TLS identity invalid.");
        LabCertificates.WriteSettings(CurrentFile, new M3DesktopEnrollment(
            1, sid, LabCertificates.Hash(cert), AllowSupervisedStart: true));
    }

    internal static void DisableForCurrentUser()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive)
            throw new AuthenticationException("Disable from the local user session.");
        if (File.Exists(CurrentFile))
            File.Delete(CurrentFile);
    }
}
