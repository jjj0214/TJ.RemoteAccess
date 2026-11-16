// SPDX-License-Identifier: GPL-2.0-or-later
using System.Windows.Forms;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class PreviewTrayPresentationTests
{
    [Fact]
    public void StoppedServerIsClearlyNotListening()
    {
        var state = new ServerTraySnapshot(false, 0, false);
        Assert.Equal("Stopped", state.Status);
        Assert.Equal("Viewers: 0/3", state.ViewerSummary);
        Assert.Contains("stopped", state.Tooltip);
        Assert.Contains("none", state.ControllerSummary);
    }

    [Fact]
    public void ListeningAndConnectedStatusesTrackActualSessions()
    {
        var listening = new ServerTraySnapshot(true, 0, false);
        var viewing = new ServerTraySnapshot(true, 2, false);
        var controlling = new ServerTraySnapshot(true, 3, true);

        Assert.Equal("Listening", listening.Status);
        Assert.Equal("Connected", viewing.Status);
        Assert.Equal("Connected", controlling.Status);
        Assert.Equal("Viewers: 3/3", controlling.ViewerSummary);
        Assert.Contains("connected", controlling.ControllerSummary);
        Assert.DoesNotContain("connected",
            new ServerTraySnapshot(false, 3, true).ControllerSummary);
        Assert.All(new[] { listening, viewing, controlling },
            state => Assert.True(state.Tooltip.Length <= 63));
    }

    [Fact]
    public void TrayExitRequestsConfirmationOnlyIfViewersWillDisconnect()
    {
        Assert.False(new ServerTraySnapshot(false, 0, false).RequiresExitConfirmation);
        Assert.False(new ServerTraySnapshot(true, 0, false).RequiresExitConfirmation);
        Assert.True(new ServerTraySnapshot(true, 1, false).RequiresExitConfirmation);
        Assert.True(new ServerTraySnapshot(true, 3, true).RequiresExitConfirmation);
    }

    [Fact]
    public void SingleInstanceUsesSessionLocalMutexAndRestoreSignal()
    {
        Assert.StartsWith(@"Local\", ServerTrayInstance.MutexName);
        Assert.StartsWith(@"Local\", ServerTrayInstance.RestoreEventName);
        Assert.NotEqual(ServerTrayInstance.MutexName, ServerTrayInstance.RestoreEventName);
    }

    [Theory]
    [InlineData(CloseReason.UserClosing, false, true)]
    [InlineData(CloseReason.UserClosing, true, false)]
    [InlineData(CloseReason.WindowsShutDown, false, false)]
    [InlineData(CloseReason.TaskManagerClosing, false, false)]
    [InlineData(CloseReason.ApplicationExitCall, false, false)]
    public void CloseButtonHidesOnlyOnExplicitUserClose(
        CloseReason reason, bool exitRequested, bool hide)
    {
        Assert.Equal(hide, ServerTraySnapshot.ShouldHideOnClose(reason, exitRequested));
    }

    [Fact]
    public void AbnormalCountsAreClampedInVisibleTrayMenu()
    {
        Assert.Equal("Viewers: 3/3",
            new ServerTraySnapshot(true, 99, true).ViewerSummary);
        Assert.Equal("Viewers: 0/3",
            new ServerTraySnapshot(true, -5, false).ViewerSummary);
    }
}
