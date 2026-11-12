// SPDX-License-Identifier: GPL-2.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabProtocolTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void WelcomeRoundTrip(int role)
    {
        var original = new LabWelcome((LabRole)role, 2592, 1600, -1920, 0);
        Assert.Equal(original, LabWire.ParseWelcome(LabWire.Welcome(original)));
    }

    [Fact]
    public void WelcomeRejectsInvalidRoleAndGeometry()
    {
        var invalid = LabWire.Welcome(new LabWelcome(LabRole.Controller, 1, 1, 0, 0));
        invalid[0] = 0x80;
        Assert.Throws<InvalidDataException>(() => LabWire.ParseWelcome(invalid));
        invalid = LabWire.Welcome(new LabWelcome(LabRole.ViewOnly, -1, 100, 0, 0));
        Assert.Throws<InvalidDataException>(() => LabWire.ParseWelcome(invalid));
    }

    [Fact]
    public void InputRoundTripAndInvalidValues()
    {
        var keyboard = LabWire.ParseKey(LabWire.Key(0x41, true));
        Assert.Equal((ushort)0x41, keyboard.Key);
        Assert.True(keyboard.Pressed);
        var pointer = LabWire.ParsePointer(LabWire.Pointer(65535, 32767, 3));
        Assert.Equal((ushort)65535, pointer.X);
        Assert.Equal((ushort)32767, pointer.Y);
        Assert.Equal((byte)3, pointer.Buttons);
        Assert.Throws<InvalidDataException>(() => LabWire.ParseKey(new byte[] { 0, 0, 1 }));
        Assert.Throws<InvalidDataException>(() => LabWire.ParsePointer(new byte[] { 1, 2, 3, 4, 255 }));
        Assert.Throws<InvalidDataException>(() => LabWire.Pointer(1, 2, 8));
    }

    [Fact]
    public async Task FramedStreamRoundTrip()
    {
        using var stream = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        var bytes = RandomNumberGenerator.GetBytes(1000);
        await LabWire.WriteAsync(stream, writer, LabMessage.JpegFrame, bytes,
                                 CancellationToken.None);
        stream.Position = 0;
        var actual = await LabWire.ReadAsync(stream, CancellationToken.None);
        Assert.Equal(LabMessage.JpegFrame, actual.Type);
        Assert.Equal(bytes, actual.Payload);
    }

    [Fact]
    public async Task RejectInvalidLengthBeforeAllocation()
    {
        var big = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(big, int.MaxValue);
        using var oversized = new MemoryStream(big);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await LabWire.ReadAsync(oversized, CancellationToken.None));
        var invalid = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(invalid, 0);
        using var zero = new MemoryStream(invalid);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await LabWire.ReadAsync(zero, CancellationToken.None));
    }

    [Fact]
    public async Task RejectTruncatedPackets()
    {
        using var truncated = new MemoryStream(new byte[] { 0, 0, 0, 10, 2 });
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LabWire.ReadAsync(truncated, CancellationToken.None));
    }

    [Fact]
    public async Task RejectOversizedOutboundPacket()
    {
        using var sink = new MemoryStream();
        using var mutex = new SemaphoreSlim(1);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await LabWire.WriteAsync(sink, mutex, LabMessage.JpegFrame,
                new byte[LabWire.MaxFrameBytes], CancellationToken.None));
    }
}
