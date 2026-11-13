// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3AgentBootstrapTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("--pipe")]
    [InlineData("--pipe bad/path --key-handle ABC --service-pid 12")]
    [InlineData("--pipe valid --key-handle 0 --service-pid 12")]
    [InlineData("--pipe valid --key-handle FF --service-pid 0")]
    [InlineData("--pipe valid --key-handle NOTHEX --service-pid 12")]
    [InlineData("--pipe valid --key-handle FF --service-pid -1")]
    public void InvalidBootstrapArgumentsFailClosed(string? args)
    {
        string[] parts = args?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        Assert.Throws<ArgumentException>(() => M3AgentArguments.Parse(parts));
    }

    [Fact]
    public void ValidInheritedHandleIsOnlyAParsedCapability()
    {
        var parsed = M3AgentArguments.Parse([
            "--pipe", "test-session", "--key-handle", "ABC", "--service-pid", "123"]);
        Assert.Equal("test-session", parsed.PipeInstance);
        Assert.Equal(new IntPtr(0xABC), parsed.KeyReadHandle);
        Assert.Equal((uint)123, parsed.ServicePid);
    }
}
