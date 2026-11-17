// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class PreviewInstalledServerTests
{
    [Fact]
    public void ProductInitializationTargetsLanButLabRemainsIsolated()
    {
        Assert.Equal("0.0.0.0", LabCertificates.DefaultListenAddress);
        Assert.Equal(15544, LabCertificates.DefaultPort);
        var old = LabServerLaunchOptions.Parse(Array.Empty<string>());
        Assert.False(old.AllowLan); // Only the installed product grants launch intent.
    }

    [Fact]
    public void OnlyExpectedCurrentUserProductDirectoryImpliesLanIntent()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string installed = Path.Combine(local, "Programs", "TJ.RemoteAccess", "Server");
        Assert.True(LabServerLaunchOptions.IsInstalledPreviewServer(installed));
        Assert.True(LabServerLaunchOptions.IsInstalledPreviewServer(installed + Path.DirectorySeparatorChar));
        Assert.False(LabServerLaunchOptions.IsInstalledPreviewServer(
            Path.Combine(local, "Programs", "TJ.RemoteAccess", "Viewer")));
        Assert.False(LabServerLaunchOptions.IsInstalledPreviewServer(
            Path.Combine(local, "Programs", "TJ.RemoteAccess", "Server", "..", "Viewer")));
    }

    [Fact]
    public void StandardServerShortcutUsesExplicitLanParameter()
    {
        var parsed = LabServerLaunchOptions.Parse(new[] { "--allow-lan" });
        Assert.True(parsed.AllowLan);
        Assert.False(parsed.AutoStart);
    }
}
