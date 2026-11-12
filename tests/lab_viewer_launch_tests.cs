// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabViewerExternalLaunchTests
{
    private static readonly string ExistingId = Guid.NewGuid().ToString("N");
    private static LabViewerProfileCatalog Catalog() => new() {
        SelectedProfileId = ExistingId,
        Profiles = new() { new LabViewerProfile {
            Id = ExistingId, Name = "Machine A", Host = "127.0.0.1",
            Port = 15444, AuthMode = "Certificate",
            ClientCertificateThumbprint = new string('A', 40),
            ServerSha256 = new string('B', 64)
        } }
    };

    [Fact]
    public void NoArgumentsLeavesNormalManualViewerUntouched()
    {
        var options = LabViewerLaunchOptions.Parse(Array.Empty<string>());
        Assert.Null(options.ProfileId);
        Assert.False(options.Connect);
        Assert.Null(options.Resolve(Catalog()));
    }

    [Fact]
    public void ExplicitProfileMaySelectWithoutConnecting()
    {
        var options = LabViewerLaunchOptions.Parse(new[] { "--profile-id", ExistingId });
        Assert.False(options.Connect);
        Assert.Equal(ExistingId, options.Resolve(Catalog())!.Id);
    }

    [Fact]
    public void ExplicitConnectOnlyUsesExistingTrustedProfile()
    {
        var options = LabViewerLaunchOptions.Parse(
            new[] { "--profile-id", ExistingId.ToUpperInvariant(), "--connect" });
        Assert.True(options.Connect);
        Assert.Equal(ExistingId, options.ProfileId);
        Assert.Equal("Machine A", options.Resolve(Catalog())!.Name);
    }

    [Fact]
    public void MissingProfileNeverFallsBackToLastUsedServer()
    {
        var options = LabViewerLaunchOptions.Parse(
            new[] { "--profile-id", Guid.NewGuid().ToString("N"), "--connect" });
        Assert.Throws<InvalidOperationException>(() => options.Resolve(Catalog()));
    }

    [Theory]
    [InlineData("--connect")]
    [InlineData("--profile-id")]
    [InlineData("--host", "192.168.1.4")]
    [InlineData("--password", "whatever")]
    [InlineData("--profile-id", "non-guid")]
    [InlineData("--profile-id", "00000000000000000000000000000000", "--connect")]
    [InlineData("--profile-id", "0123456789abcdef0123456789abcdef", "--password")]
    [InlineData("--profile-id", "0123456789abcdef0123456789abcdef", "--connect", "--host")]
    public void RejectUnexpectedArgsAndUntrustedConnectionOverrides(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => LabViewerLaunchOptions.Parse(args));
    }
}
