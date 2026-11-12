// SPDX-License-Identifier: GPL-2.0-or-later
// Bounds and cancellation regression tests for the authenticated Lab channel.
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabTransportLimitsTests
{
    [Fact]
    public async Task OversizedControlHeaderFailsBeforeAllocatingPayload()
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, LabWire.MaxControlPacketBytes + 1);
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await LabWire.ReadAsync(input, CancellationToken.None,
                                                 LabWire.MaxControlPacketBytes));
        Assert.Equal(4, input.Position);
    }

    [Fact]
    public async Task ControlLimitAcceptsWellFormedHello()
    {
        using var output = new MemoryStream();
        using var writer = new SemaphoreSlim(1);
        await LabWire.WriteAsync(output, writer, LabMessage.Hello,
                                 new byte[] { LabWire.Version }, CancellationToken.None);
        output.Position = 0;
        var hello = await LabWire.ReadAsync(output, CancellationToken.None,
                                             LabWire.MaxControlPacketBytes);
        Assert.Equal(LabMessage.Hello, hello.Type);
        Assert.Equal(new byte[] { LabWire.Version }, hello.Payload);
    }

    [Fact]
    public async Task MalformedControlFrameWithinLimitStillRejectsTruncation()
    {
        var packet = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(0, 4), 10);
        packet[4] = (byte)LabMessage.Pointer;
        using var input = new MemoryStream(packet);
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await LabWire.ReadAsync(input, CancellationToken.None,
                                                 LabWire.MaxControlPacketBytes));
    }

    [Fact]
    public async Task RejectsImpossiblePacketLimits()
    {
        using var input = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await LabWire.ReadAsync(input, CancellationToken.None, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            async () => await LabWire.ReadAsync(input, CancellationToken.None,
                                                 LabWire.MaxFrameBytes + 1));
    }

    [Fact]
    public async Task FrameTransportRetainsFullJpegCapacity()
    {
        using var wire = new MemoryStream();
        using var mutex = new SemaphoreSlim(1);
        var jpeg = RandomNumberGenerator.GetBytes(4096);
        await LabWire.WriteAsync(wire, mutex, LabMessage.JpegFrame,
                                 jpeg, CancellationToken.None);
        wire.Position = 0;
        var received = await LabWire.ReadAsync(wire, CancellationToken.None);
        Assert.Equal(LabMessage.JpegFrame, received.Type);
        Assert.Equal(jpeg, received.Payload);
    }

    [Fact]
    public async Task BlockingSocketReadCancelsWithoutAnotherNetworkPacket()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        await sender.ConnectAsync(IPAddress.Loopback,
            ((IPEndPoint)listener.LocalEndpoint).Port);
        using var receiver = await listener.AcceptTcpClientAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await LabWire.ReadAsync(receiver.GetStream(), timeout.Token,
                                                 LabWire.MaxControlPacketBytes));
    }
[Fact]
    public async Task FrameEncodingIsByteCompatibleWithPreviousLabProtocol()
    {
        using var wire = new MemoryStream();
        using var writer = new SemaphoreSlim(1);
        await LabWire.WriteAsync(wire, writer, LabMessage.Key,
            LabWire.Key(0x41, pressed: true), CancellationToken.None);
        Assert.Equal(new byte[] { 0, 0, 0, 4, (byte)LabMessage.Key, 0, 0x41, 1 },
                     wire.ToArray());
    }

    [Fact]
    public async Task ConcurrentWritersCannotInterleaveHeaderAndBody()
    {
        using var wire = new MemoryStream();
        using var writer = new SemaphoreSlim(1);
        const int count = 40;
        await Task.WhenAll(Enumerable.Range(0, count).Select(i =>
            LabWire.WriteAsync(wire, writer, LabMessage.Status,
                new[] { (byte)i }, CancellationToken.None)));
        wire.Position = 0;
        var seen = new HashSet<int>();
        for (int i = 0; i < count; ++i)
        {
            var packet = await LabWire.ReadAsync(wire, CancellationToken.None,
                                                 LabWire.MaxControlPacketBytes);
            Assert.Equal(LabMessage.Status, packet.Type);
            Assert.Single(packet.Payload);
            Assert.True(seen.Add(packet.Payload[0]));
        }
        Assert.Equal(count, seen.Count);
        Assert.Equal(wire.Length, wire.Position);
    }
}
