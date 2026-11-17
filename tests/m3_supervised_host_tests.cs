// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3SupervisedHostTests
{
    [Fact]
    public void StatusOnlyIsTheDefaultEvenWhenUserEnrollmentIsPresent()
    {
        string folder = Path.Combine(Path.GetTempPath(), "TJRA-M3-Policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.False(M3ServiceHostPolicyStore.IsEnabled(folder));
            File.WriteAllText(Path.Combine(folder, M3ServiceHostPolicyStore.FileName),
                JsonSerializer.Serialize(new M3ServiceHostPolicy(
                    1, EnableSupervisedDesktop: false, "InteractiveOwnerConsentOnly")));
            Assert.False(M3ServiceHostPolicyStore.IsEnabled(folder));
            File.WriteAllText(Path.Combine(folder, M3ServiceHostPolicyStore.FileName),
                JsonSerializer.Serialize(new M3ServiceHostPolicy(
                    1, EnableSupervisedDesktop: true, "UnattendedSecureDesktop")));
            Assert.False(M3ServiceHostPolicyStore.IsEnabled(folder));
            File.WriteAllText(Path.Combine(folder, M3ServiceHostPolicyStore.FileName),
                JsonSerializer.Serialize(new M3ServiceHostPolicy(
                    1, EnableSupervisedDesktop: true, "InteractiveOwnerConsentOnly")));
            Assert.True(M3ServiceHostPolicyStore.IsEnabled(folder));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("S-1-5-21-1000", "S-1-5-21-1000", true)]
    [InlineData("S-1-5-21-1000", "S-1-5-21-1001", false)]
    [InlineData("S-1-5-18", "S-1-5-18", false)]
    public void UserEnrollmentCannotCrossAccountOrBecomeSystem(string stored, string actual, bool match)
    {
        string pin = new string('A', 64);
        var model = new M3DesktopEnrollment(1, stored, pin, true);
        Assert.Equal(match, M3DesktopEnrollmentStore.Matches(model, actual, pin));
        Assert.False(M3DesktopEnrollmentStore.Matches(model, actual, new string('B', 64)));
        Assert.False(M3DesktopEnrollmentStore.Matches(model with { AllowSupervisedStart = false },
                                                     actual, pin));
    }

    [Fact]
    public void ChildExecutableCannotEscapeAgentInstallationDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "TJRA-Root");
        string good = Path.Combine(path, "TJ.RemoteAccess.Server.exe");
        Assert.True(M3SupervisedDesktopHost.IsExecutablePathEligible(path, good));
        Assert.False(M3SupervisedDesktopHost.IsExecutablePathEligible(
            path, Path.Combine(path, "..", "TJ.RemoteAccess.Server.exe")));
        Assert.False(M3SupervisedDesktopHost.IsExecutablePathEligible(
            path, Path.Combine(path, "Lab.Server.exe")));
    }

    [Fact]
    public void AgentCanSuperviseOnlyItsOwnersExactCompactServerPath()
    {
        string local = Path.Combine(Path.GetTempPath(), "TJRA-M3-Test-" + Guid.NewGuid().ToString("N"));
        string good = Path.Combine(local, "Programs", "TJ.RemoteAccess", "Server",
                                   "TJ.RemoteAccess.Server.exe");
        Assert.True(M3SupervisedDesktopHost.IsInstalledProductServerPathEligible(local, good));
        Assert.False(M3SupervisedDesktopHost.IsInstalledProductServerPathEligible(local,
            Path.Combine(local, "Programs", "TJ.RemoteAccess", "Viewer", "TJ.RemoteAccess.Viewer.exe")));
        Assert.False(M3SupervisedDesktopHost.IsInstalledProductServerPathEligible(local,
            Path.Combine(local, "..", "TJ.RemoteAccess.Server.exe")));
        Assert.False(M3SupervisedDesktopHost.IsInstalledProductServerPathEligible(
            Path.Combine(local, "other-user"), good));
    }

    [Theory]
    [InlineData("Default", true)]
    [InlineData("Winlogon", false)]
    [InlineData("Screen-saver", false)]
    [InlineData("default", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ProtectedDesktopNamesAreNeverAuthorized(string? name, bool allowed)
    {
        Assert.Equal(allowed, M3InteractiveDesktopGuard.AllowedDesktopName(name));
    }

    [Fact]
    public void ServiceCannotReceiveDesktopStartFlagFromLegacySixArguments()
    {
        var basic = M3AgentArguments.Parse([
            "--pipe", "test", "--key-handle", "ABCD", "--service-pid", "123"]);
        Assert.False(basic.SuperviseDesktop);
    }
}
