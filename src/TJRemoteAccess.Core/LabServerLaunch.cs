// SPDX-License-Identifier: GPL-2.0-or-later
// Explicit consent-oriented, current-user Lab Server process launch options.
// There is no service/lock-screen/UAC bypass and no unattended auth override.
namespace TJ.RemoteAccess.Lab;

internal sealed record LabServerLaunchOptions(bool AllowLan, bool AutoStart)
{
    // Only the CURRENT USER'S expected Inno Setup Server directory is granted
    // installed-product LAN intent. Lab and arbitrary copies remain opt-in.
    internal static bool IsInstalledPreviewServer(string directory)
    {
#if TJRA_STANDALONE_PREVIEW
        try
        {
            string expected = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "TJ.RemoteAccess", "Server")).TrimEnd('\\', '/');
            string actual = Path.GetFullPath(directory).TrimEnd('\\', '/');
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException) { return false; }
#else
        return false;
#endif
    }

    internal static LabServerLaunchOptions Parse(string[] args)
    {
        bool lan = false, start = false;
        if (args is null || args.Length > 2)
            throw new ArgumentException("Supported Lab Server flags: --allow-lan --auto-start.");
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--allow-lan" when !lan:
                    lan = true;
                    break;
                case "--auto-start" when !start:
                    start = true;
                    break;
                default:
                    throw new ArgumentException(
                        "Invalid or duplicate Lab Server option. Use --allow-lan and/or --auto-start.");
            }
        }
        return new(lan, start);
    }
}
