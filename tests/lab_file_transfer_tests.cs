// SPDX-License-Identifier: GPL-2.0-or-later
using System.Security.Cryptography;
using System.Text;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabFileTransferTests
{
    [Fact]
    public void OfferAndChunksRoundTripWithStrictSizeAndEncoding()
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("testing"));
        var offer = new LabFileOffer(Guid.NewGuid(), "측정-results.txt", 7, digest);
        Assert.Equal(offer, LabFileTransfer.ParseOffer(LabFileTransfer.OfferBytes(offer)) with { Sha256 = digest });
        Assert.Equal(offer.FileName, LabFileTransfer.ParseOffer(LabFileTransfer.OfferBytes(offer)).FileName);
        Assert.Equal(digest, LabFileTransfer.ParseOffer(LabFileTransfer.OfferBytes(offer)).Sha256);
        var chunk = LabFileTransfer.ParseChunk(LabFileTransfer.Chunk(offer.Id, 0, Encoding.UTF8.GetBytes("testing")));
        Assert.Equal(offer.Id, chunk.Id);
        Assert.Equal(0, chunk.Offset);
        Assert.Equal("testing", Encoding.UTF8.GetString(chunk.Data));
        Assert.Equal((offer.Id, true), LabFileTransfer.ParseDecision(LabFileTransfer.Decision(offer.Id, true)));
        Assert.Equal(offer.Id, LabFileTransfer.ParseTransferId(LabFileTransfer.TransferId(offer.Id)));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../file.txt")]
    [InlineData("..\\file.txt")]
    [InlineData("C:secret.txt")]
    [InlineData("CON.txt")]
    [InlineData("COM1.log")]
    [InlineData("report.txt.")]
    [InlineData("foo/bar.txt")]
    [InlineData("report.txt ")]
    public void UnsafeTransferNameRejected(string name)
    {
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.SafeFileName(name));
    }

    [Fact]
    public void MalformedOffersDecisionsAndChunksFailClosed()
    {
        var id = Guid.NewGuid();
        var offer = new LabFileOffer(id, "ok.txt", 1, new byte[32]);
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.ParseOffer(Array.Empty<byte>()));
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.ParseDecision(new byte[16]));
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.ParseChunk(new byte[24]));
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.ParseTransferId(new byte[15]));
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransfer.OfferBytes(offer with { Length = LabFileTransfer.MaxFileBytes + 1 }));
        Assert.Throws<InvalidDataException>(() =>
            LabFileTransfer.Chunk(id, 0, new byte[LabFileTransfer.MaxChunkBytes + 1]));
        byte[] bytes = LabFileTransfer.OfferBytes(offer);
        bytes[56] = 1; bytes[57] = 0;
        Assert.Throws<InvalidDataException>(() => LabFileTransfer.ParseOffer(bytes));
    }

    [Fact]
    public void ReceiverCommitsOnlyAfterOrderedBytesAndMatchingSha256()
    {
        string root = Path.Combine(Path.GetTempPath(), "TJRA-TransferTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Encoding.UTF8.GetBytes("Verified transfer");
            var offer = new LabFileOffer(Guid.NewGuid(), "sample.bin", data.Length, SHA256.HashData(data));
            string target = Path.Combine(root, offer.FileName);
            using (var receiver = new LabFileReceiver(offer, target))
            {
                Assert.Throws<InvalidDataException>(() => receiver.AddChunk(offer.Id, 1, data));
                receiver.AddChunk(offer.Id, 0, data[..5]);
                Assert.Throws<InvalidDataException>(() => receiver.Finish(offer.Id));
                receiver.AddChunk(offer.Id, 5, data[5..]);
                Assert.Equal(target, receiver.Finish(offer.Id));
            }
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.DoesNotContain(Directory.EnumerateFiles(root), f => f.EndsWith(".partial"));
            Assert.Throws<InvalidDataException>(() => new LabFileReceiver(offer, target));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void HashMismatchDeletesPartialFileOnDispose()
    {
        string root = Path.Combine(Path.GetTempPath(), "TJRA-TransferBad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var offer = new LabFileOffer(Guid.NewGuid(), "bad.bin", 3, new byte[32]);
            using (var receiver = new LabFileReceiver(offer, Path.Combine(root, offer.FileName)))
            {
                receiver.AddChunk(offer.Id, 0, new byte[] { 1, 2, 3 });
                Assert.Throws<InvalidDataException>(() => receiver.Finish(offer.Id));
            }
            Assert.Empty(Directory.EnumerateFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
