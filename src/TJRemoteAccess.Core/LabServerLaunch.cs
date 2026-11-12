// SPDX-License-Identifier: GPL-2.0-or-later
// Explicit consent-oriented, current-user Lab Server process launch options.
// There is no service/lock-screen/UAC bypass and no unattended auth override.
namespace TJ.RemoteAccess.Lab;

internal sealed record LabServerLaunchOptions(bool AllowLan, bool AutoStart)
{
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
