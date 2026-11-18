// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class PreviewConnectionOptionsTests
{
    [Fact]
    public void DefaultOptionsPreserveSixFpsSixtyFiveQualityAndNormalControl()
    {
        var settings = new LabViewerConnectionOptions().Validate();
        Assert.Equal(65, settings.JpegQuality);
        Assert.Equal(6, settings.FramesPerSecond);
        Assert.False(settings.ViewOnly);
        Assert.False(settings.AutoReconnect);
        Assert.False(settings.FullScreen);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 6)]
    [InlineData(6, 5)]
    [InlineData(7, 7)]
    public void PrimaryMouseSwapPreservesMiddleButton(int input, int expected)
    {
        var options = new LabViewerConnectionOptions(SwapMouseButtons: true);
        Assert.Equal((byte)expected, options.MapMouseButtons((byte)input));
        Assert.Equal((byte)input, new LabViewerConnectionOptions().MapMouseButtons((byte)input));
        Assert.Throws<InvalidDataException>(() => options.MapMouseButtons(8));
    }

    [Theory]
    [InlineData(24, 6)]
    [InlineData(91, 6)]
    [InlineData(65, 1)]
    [InlineData(65, 21)]
    [InlineData(256, 6)]
    public void InvalidCaptureOptionsFailClosed(int quality, int fps)
    {
        Assert.ThrowsAny<Exception>(() =>
            new LabViewerConnectionOptions(JpegQuality: quality, FramesPerSecond: fps).Validate());
    }

    [Fact]
    public void AutoReconnectNeverUsesPasswordModeOrOverridesManualDisconnect()
    {
        var enabled = new LabViewerConnectionOptions(AutoReconnect: true);
        Assert.True(enabled.MayAutoReconnect(authenticatedWithCertificate: true, manualDisconnect: false));
        Assert.False(enabled.MayAutoReconnect(authenticatedWithCertificate: false, manualDisconnect: false));
        Assert.False(enabled.MayAutoReconnect(authenticatedWithCertificate: true, manualDisconnect: true));
        Assert.False(new LabViewerConnectionOptions().MayAutoReconnect(true, false));
    }

    [Theory]
    [InlineData(25, 2)]
    [InlineData(65, 6)]
    [InlineData(90, 20)]
    public void CapturePacketAndWheelHaveBounds(int quality, int fps)
    {
        var encoded = LabWire.CaptureSettings((byte)quality, (byte)fps);
        var actual = LabWire.ParseCaptureSettings(encoded);
        Assert.Equal(quality, (int)actual.Quality);
        Assert.Equal(fps, (int)actual.Fps);
        Assert.Equal((short)-120, LabWire.ParseWheel(LabWire.Wheel(-120)));
        Assert.Equal((short)120, LabWire.ParseWheel(LabWire.Wheel(120)));
        Assert.Throws<InvalidDataException>(() => LabWire.Wheel(0));
        Assert.Throws<InvalidDataException>(() => LabWire.Wheel(1201));
        Assert.Throws<InvalidDataException>(() => LabWire.ParseWheel(new byte[] { 1 }));
        Assert.Throws<InvalidDataException>(() => LabWire.ParseCaptureSettings(new byte[] { 80, 21 }));
    }

    [Fact]
    public void SharedCaptureAppliesOnlyValidatedOptions()
    {
        var frames = new LabSharedFrameStore();
        Assert.Equal(65, frames.Quality);
        Assert.Equal(6, frames.FramesPerSecond);
        frames.Configure(80, 12);
        Assert.Equal(80, frames.Quality);
        Assert.Equal(12, frames.FramesPerSecond);
        Assert.Throws<InvalidDataException>(() => frames.Configure(80, 22));
        Assert.Equal(12, frames.FramesPerSecond);
    }
}
