// SPDX-License-Identifier: GPL-2.0-or-later
// Service session event policy: bind only to an explicitly active console
// desktop, never a guessed or unrelated Remote Desktop Windows session.
// Other session transitions revoke the currently authorized status-only Agent.
using System.ServiceProcess;

namespace TJ.RemoteAccess.M3;

internal static class M3ServiceSessionPolicy
{
    internal static bool MayStartConsoleAgent(SessionChangeReason reason,
                                               int eventSessionId,
                                               uint activeConsoleSession)
    {
        if (eventSessionId < 1 || activeConsoleSession is 0 or uint.MaxValue ||
            (uint)eventSessionId != activeConsoleSession)
            return false;
        return reason is SessionChangeReason.SessionLogon or
               SessionChangeReason.SessionUnlock or
               SessionChangeReason.ConsoleConnect;
    }

    internal static bool IsRevokeEvent(SessionChangeReason reason) => reason is
        SessionChangeReason.SessionLock or
        SessionChangeReason.SessionLogoff or
        SessionChangeReason.ConsoleDisconnect or
        SessionChangeReason.RemoteDisconnect or
        SessionChangeReason.RemoteConnect;
}
