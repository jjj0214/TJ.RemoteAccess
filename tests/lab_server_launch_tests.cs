// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabServerStartupTests
{
    [Fact]
    public void ManualLaunchIsTheDefault()
    {
        var options = LabServerLaunchOptions.Parse(Array.Empty<string>());
        Assert.False(options.AllowLan);
        Assert.False(options.AutoStart);
    }

    [Theory]
    [InlineData("--allow-lan")]
    [InlineData("--auto-start")]
    public void SingleExpectedSwitchIsAccepted(string option)
    {
        var value = LabServerLaunchOptions.Parse(new[] { option });
        Assert.Equal(option == "--allow-lan", value.AllowLan);
        Assert.Equal(option == "--auto-start", value.AutoStart);
    }

    [Fact]
    public void ExplicitAutostartAndLanAreOrderIndependent()
    {
        foreach (var args in new[] {
            new[] { "--auto-start", "--allow-lan" },
            new[] { "--allow-lan", "--auto-start" }
        })
        {
            var launch = LabServerLaunchOptions.Parse(args);
            Assert.True(launch.AllowLan);
            Assert.True(launch.AutoStart);
        }
    }

    [Theory]
    [InlineData("--allow-lan", "--allow-lan")]
    [InlineData("--auto-start", "--auto-start")]
    [InlineData("--allow-lan", "--password")]
    [InlineData("--service", "--auto-start")]
    public void UnknownAndDuplicateSwitchesAreRejected(string arg1, string arg2)
    {
        Assert.Throws<ArgumentException>(() =>
            LabServerLaunchOptions.Parse(new[] { arg1, arg2 }));
    }

    [Fact]
    public void MoreThanTwoSwitchesCannotAccidentallyEnableRemoteAccess()
    {
        Assert.Throws<ArgumentException>(() =>
            LabServerLaunchOptions.Parse(new[] { "--auto-start", "--allow-lan", "invalid" }));
    }
}
