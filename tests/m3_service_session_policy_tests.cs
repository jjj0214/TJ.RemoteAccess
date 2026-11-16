// SPDX-License-Identifier: GPL-2.0-or-later
using System.ServiceProcess;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3ServiceSessionPolicyTests
{
    [Theory]
    [InlineData(SessionChangeReason.SessionLogon)]
    [InlineData(SessionChangeReason.SessionUnlock)]
    [InlineData(SessionChangeReason.ConsoleConnect)]
    public void ActiveConsoleSessionMayCreateFreshAgent(SessionChangeReason reason)
    {
        Assert.True(M3ServiceSessionPolicy.MayStartConsoleAgent(reason, 2, 2));
        Assert.False(M3ServiceSessionPolicy.MayStartConsoleAgent(reason, 2, 3));
        Assert.False(M3ServiceSessionPolicy.MayStartConsoleAgent(reason, 0, 0));
        Assert.False(M3ServiceSessionPolicy.MayStartConsoleAgent(reason, 2, uint.MaxValue));
    }

    [Theory]
    [InlineData(SessionChangeReason.SessionLock)]
    [InlineData(SessionChangeReason.SessionLogoff)]
    [InlineData(SessionChangeReason.ConsoleDisconnect)]
    [InlineData(SessionChangeReason.RemoteDisconnect)]
    [InlineData(SessionChangeReason.RemoteConnect)]
    public void DesktopRevocationEventsNeverStartNewConsoleAgent(SessionChangeReason reason)
    {
        Assert.False(M3ServiceSessionPolicy.MayStartConsoleAgent(reason, 2, 2));
        Assert.True(M3ServiceSessionPolicy.IsRevokeEvent(reason));
    }
}
