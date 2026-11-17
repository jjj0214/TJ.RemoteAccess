// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.IO;
using System.Linq;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class PreviewFileTransferProtocolTests
{
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("..\\secrets.txt")]
    [InlineData("C:\\Windows\\system.ini")]
    [InlineData("con.txt")]
    [InlineData("file. ")]
    [InlineData("")]
    [InlineData("name/other")]
    public void RemoteFileNamesCannotSelectAPath(string fileName)
    {
        Assert.False(LabFileTransferWire.SafeFileName(fileName));
    }

    [Fact]
    public void MetadataAndChunkRoundtripAreBoundedAndVersioned()
    {
        var id = Guid.NewGuid();
        var offer = new LabFileOffer(id, "inspection_001.jpg", 65000,
            Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var packet = LabFileTransferWire.Offer(offer);
        var read = LabFileTransferWire.ParseOffer(packet);
        Assert.Equal(id, read.Id);
        Assert.Equal(offer.FileName, read.FileName);
        Assert.Equal(offer.Size, read.Size);
        Assert.Equal(offer.Sha256, read.Sha256);
        var decision = LabFileTransferWire.ParseDecision(
            LabFileTransferWire.Decision(id, true));
        Assert.Equal(id, decision.Id);
        Assert.True(decision.Accepted);
        var block = LabFileTransferWire.ParseBlock(
            LabFileTransferWire.Block(id, 32768, new byte[] { 1, 2, 3 }));
        Assert.Equal(id, block.Id);
        Assert.Equal(32768, block.Offset);
        Assert.Equal(new byte[] { 1, 2, 3 }, block.Data.ToArray());
    }

    [Fact]
    public void OversizedFileAndBlockAreRejectedWithoutAllocation()
    {
        var id = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransferWire.Offer(new LabFileOffer(id, "large.bin",
                LabFileTransferWire.MaxFileBytes + 1, new byte[32])));
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransferWire.Block(id, 0, new byte[LabFileTransferWire.BlockSize + 1]));
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransferWire.ParseBlock(new byte[24]));
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransferWire.ParseDecision(new byte[] { 1 }));
    }

    [Fact]
    public void MalformedOfferOrTraversalFailsClosed()
    {
        var offer = new LabFileOffer(Guid.NewGuid(), "safe.jpg", 0, new byte[32]);
        var data = LabFileTransferWire.Offer(offer);
        data[57] = 255;
        Assert.Throws<InvalidDataException>(() => LabFileTransferWire.ParseOffer(data));
        Assert.False(LabFileTransferWire.SafeFileName("NUL"));
    }

    [Fact]
    public void MouseWheelRejectsInvalidDeltas()
    {
        Assert.Equal((short)-120, LabWire.ParseWheel(LabWire.Wheel(-120)));
        Assert.Throws<InvalidDataException>(() => LabWire.Wheel(0));
        Assert.Throws<InvalidDataException>(() => LabWire.Wheel(1000));
        Assert.Throws<InvalidDataException>(() => LabWire.ParseWheel(new byte[3]));
    }

    [Fact]
    public void FilePacketTypesAreNotArbitraryControlMessages()
    {
        Assert.True(LabFileTransferWire.IsFilePacket(LabMessage.FileOffer));
        Assert.True(LabFileTransferWire.IsFilePacket(LabMessage.FileRequest));
        Assert.False(LabFileTransferWire.IsFilePacket(LabMessage.Key));
        Assert.False(LabFileTransferWire.IsFilePacket(LabMessage.JpegFrame));
        Assert.True(LabFileTransferWire.MaxPacketBytes < LabWire.MaxFrameBytes);
    }
}
