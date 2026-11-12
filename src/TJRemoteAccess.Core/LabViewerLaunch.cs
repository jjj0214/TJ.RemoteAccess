// SPDX-License-Identifier: GPL-2.0-or-later
// Stable command-line boundary for private PIE.FileTransfer Master and other clients.
// Only selects an ALREADY trusted local profile; never accepts a password, PFX or TLS pin.
namespace TJ.RemoteAccess.Lab;

internal sealed record LabViewerLaunchOptions(string? ProfileId, bool Connect)
{
    internal static LabViewerLaunchOptions Parse(string[] args)
    {
        if (args is null) throw new ArgumentNullException(nameof(args));
        if (args.Length == 0) return new(null, false);
        if (args.Length is not (2 or 3) || args[0] != "--profile-id" ||
            !Guid.TryParseExact(args[1], "N", out var id) || id == Guid.Empty ||
            args.Length == 3 && args[2] != "--connect")
            throw new ArgumentException(
                "Expected --profile-id <32-hex-local-profile-id> [--connect]. " +
                "No remote host, PFX, password or trust pins are accepted on the command line.");
        return new(id.ToString("N"), args.Length == 3);
    }

    internal LabViewerProfile? Resolve(LabViewerProfileCatalog catalog)
    {
        if (ProfileId is null) return null;
        return catalog.Profiles.SingleOrDefault(p =>
            string.Equals(p.Id, ProfileId, StringComparison.OrdinalIgnoreCase)) ??
            throw new InvalidOperationException(
                "Requested Viewer profile ID does not exist locally; refusing to fall back to another server.");
    }
}
