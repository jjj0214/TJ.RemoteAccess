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

    [Fact]
    public async Task FramedFileTransportRoundTripsBatchedDataWithVerifiedCommit()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TJRA-Transport-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var sourcePath = Path.Combine(dir, "source.bin");
            var targetPath = Path.Combine(dir, "received.bin");
            byte[] data = RandomNumberGenerator.GetBytes(150_123);
            File.WriteAllBytes(sourcePath, data);
            var offer = await LabFileTransfer.OfferFromFileAsync(sourcePath, CancellationToken.None);
            using var wire = new MemoryStream();
            using var sendLock = new SemaphoreSlim(1, 1);
            await LabWire.WriteAsync(wire, sendLock, LabMessage.FileOffer,
                LabFileTransfer.OfferBytes(offer), CancellationToken.None);
            await LabFileTransfer.SendChunksAsync(sourcePath, offer,
                (type, payload, stop) => LabWire.WriteAsync(wire, sendLock, type, payload, stop),
                CancellationToken.None);
            wire.Position = 0;
            var request = await LabWire.ReadAsync(wire, CancellationToken.None);
            Assert.Equal(LabMessage.FileOffer, request.Type);
            Assert.Equal(offer.Id, LabFileTransfer.ParseOffer(request.Payload).Id);
            using var receiver = new LabFileReceiver(offer, targetPath);
            while (wire.Position < wire.Length)
            {
                var packet = await LabWire.ReadAsync(wire, CancellationToken.None);
                if (packet.Type == LabMessage.FileChunk)
                {
                    var chunk = LabFileTransfer.ParseChunk(packet.Payload);
                    receiver.AddChunk(chunk.Id, chunk.Offset, chunk.Data);
                }
                else
                {
                    Assert.Equal(LabMessage.FileComplete, packet.Type);
                    Assert.Equal(targetPath, receiver.Finish(LabFileTransfer.ParseTransferId(packet.Payload)));
                }
            }
            Assert.Equal(data, File.ReadAllBytes(targetPath));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.partial"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
    [Fact]
    public async Task LargerThan64MiBFileIsStreamingAndShaVerified()
    {
        const int fileBytes = 72 * 1024 * 1024;
        string root = Path.Combine(Path.GetTempPath(), "TJRA-Large-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "large.bin");
            string destination = Path.Combine(root, "large-received.bin");
            using (var seed = File.Create(source)) seed.SetLength(fileBytes);
            var offer = await LabFileTransfer.OfferFromFileAsync(source, CancellationToken.None);
            Assert.True(offer.Length > 64L * 1024 * 1024);
            int chunkCount = 0;
            int maxPayload = 0;
            using (var receiver = new LabFileReceiver(offer, destination))
            {
                await LabFileTransfer.SendChunksAsync(source, offer, (type, payload, _) =>
                {
                    if (type == LabMessage.FileChunk)
                    {
                        maxPayload = Math.Max(maxPayload, payload.Length);
                        var chunk = LabFileTransfer.ParseChunk(payload.Span);
                        receiver.AddChunk(chunk.Id, chunk.Offset, chunk.Data);
                        chunkCount++;
                    }
                    else
                    {
                        Assert.Equal(LabMessage.FileComplete, type);
                        Assert.Equal(destination, receiver.Finish(
                            LabFileTransfer.ParseTransferId(payload.Span)));
                    }
                    return Task.CompletedTask;
                }, CancellationToken.None);
            }
            Assert.True(chunkCount > 1000);
            Assert.InRange(maxPayload, 1, LabFileTransfer.MaxChunkBytes + 24);
            Assert.Equal(fileBytes, new FileInfo(destination).Length);
            await using var verified = File.OpenRead(destination);
            Assert.Equal(offer.Sha256, await SHA256.HashDataAsync(verified));
            Assert.Empty(Directory.EnumerateFiles(root, "*.partial"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ChangedSourceMustNotEmitSuccessfulCompletion()
    {
        string root = Path.Combine(Path.GetTempPath(), "TJRA-Changed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "changed.bin");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 4 });
            var offer = await LabFileTransfer.OfferFromFileAsync(source, CancellationToken.None);
            File.WriteAllBytes(source, new byte[] { 1, 2, 3, 5 });
            bool completed = false;
            await Assert.ThrowsAsync<IOException>(() => LabFileTransfer.SendChunksAsync(
                source, offer, (type, _, _) =>
                {
                    if (type == LabMessage.FileComplete) completed = true;
                    return Task.CompletedTask;
                }, CancellationToken.None));
            Assert.False(completed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
