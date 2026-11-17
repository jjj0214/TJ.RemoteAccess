// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class PreviewIsolationTests
{
    [Fact]
    public void PreviewDefaultsAreDistinctFromExistingLabAndTightVnc()
    {
        Assert.Equal(15544, LabCertificates.DefaultPort);
        Assert.Equal(15544, new LabServerSettings().Port);
        Assert.Equal(15544, new LabViewerSettings().Port);
        Assert.Equal("TJ.RemoteAccess", LabCertificates.DirectoryName);
        Assert.NotEqual("TJ.RemoteAccess.Lab", LabCertificates.DirectoryName);
        Assert.NotEqual(5900, LabCertificates.DefaultPort);
    }

    [Fact]
    public void PreviewConfigAndEnrollmentArePerUser()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TJ.RemoteAccess");
        Assert.Equal(expected, LabCertificates.ConfigDirectory);
        Assert.Equal(Path.Combine(expected, "server.json"), LabCertificates.ServerFile);
        Assert.Equal(Path.Combine(expected, "viewer.json"), LabCertificates.ViewerFile);
        Assert.Equal(Path.Combine(expected, "desktop-host-enrollment.json"),
                     M3DesktopEnrollmentStore.CurrentFile);
    }

    [Fact]
    public void M3EnrollmentRequiresExactLocalOwnerAndCertificatePin()
    {
        var pin = new string('A', 64);
        var settings = new M3DesktopEnrollment(1, "S-1-5-21-101", pin, true);
        Assert.True(M3DesktopEnrollmentStore.Matches(settings, "S-1-5-21-101", pin));
        Assert.False(M3DesktopEnrollmentStore.Matches(settings, "S-1-5-21-102", pin));
        Assert.False(M3DesktopEnrollmentStore.Matches(settings, "S-1-5-21-101", new string('B', 64)));
        Assert.False(M3DesktopEnrollmentStore.Matches(settings with { AllowSupervisedStart=false },
                                                      "S-1-5-21-101", pin));
    }

    [Fact]
    public void InstalledPreviewServerAcceptsLanByDefaultWithoutChangingAuthentication()
    {
        var manual = LabServerLaunchOptions.Parse(Array.Empty<string>());
        Assert.True(manual.AllowLan);
        Assert.False(manual.AutoStart);
        var auto = LabServerLaunchOptions.Parse(new[] { "--auto-start" });
        Assert.True(auto.AllowLan);
        Assert.True(auto.AutoStart);
        Assert.True(LabServerLaunchOptions.Parse(new[] { "--allow-lan" }).AllowLan);
        Assert.Throws<ArgumentException>(() =>
            LabServerLaunchOptions.Parse(new[] { "--allow-lan", "--allow-lan" }));
    }
}