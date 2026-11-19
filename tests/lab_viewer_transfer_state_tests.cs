// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabViewerTransferStateTests
{
    [Fact]
    public async Task UploadUsesExplicitApprovalOrderedChunksAndServerVerification()
    {
        string root = Path.Combine(Path.GetTempPath(), "TJRA-ClientUpload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "upload.txt");
            byte[] body = RandomNumberGenerator.GetBytes(145000);
            File.WriteAllBytes(file, body);
            var messages = new ConcurrentQueue<LabPacket>();
            var offered = new TaskCompletionSource<LabFileOffer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var complete = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var client = new LabViewerTransfers(
                (type, payload) =>
                {
                    messages.Enqueue(new LabPacket(type, payload.ToArray()));
                    if (type == LabMessage.FileOffer)
                        offered.TrySetResult(LabFileTransfer.ParseOffer(payload.Span));
                    if (type == LabMessage.FileComplete)
                        complete.TrySetResult(LabFileTransfer.ParseTransferId(payload.Span));
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromResult<string?>(null),
                _ => { });
            var action = client.UploadAsync(file, CancellationToken.None);
            var offer = await offered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(client.Busy);
            await client.HandleAsync(new LabPacket(LabMessage.FileDecision,
                LabFileTransfer.Decision(offer.Id, true)), CancellationToken.None);
            Assert.Equal(offer.Id, await complete.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            await client.HandleAsync(new LabPacket(LabMessage.FileResult,
                LabFileTransfer.Decision(offer.Id, true)), CancellationToken.None);
            await action.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(client.Busy);
            using var target = new MemoryStream();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            foreach (var packet in messages.Where(p => p.Type == LabMessage.FileChunk))
            {
                var chunk = LabFileTransfer.ParseChunk(packet.Payload);
                Assert.Equal(offset, chunk.Offset);
                offset += chunk.Data.Length;
                hash.AppendData(chunk.Data);
                target.Write(chunk.Data);
            }
            Assert.Equal(body.Length, offset);
            Assert.Equal(body, target.ToArray());
            Assert.Equal(offer.Sha256, hash.GetHashAndReset());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadRequiresRequestedIdAndExplicitLocalDestination()
    {
        string root = Path.Combine(Path.GetTempPath(), "TJRA-ClientDownload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("download payload checksum confirmed");
            string file = Path.Combine(root, "download.txt");
            var requested = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var responses = new ConcurrentQueue<LabPacket>();
            using var client = new LabViewerTransfers(
                (type, payload) =>
                {
                    responses.Enqueue(new LabPacket(type, payload.ToArray()));
                    if (type == LabMessage.FileRequest)
                        requested.TrySetResult(LabFileTransfer.ParseTransferId(payload.Span));
                    return Task.CompletedTask;
                },
                (_, _) => Task.FromResult<string?>(file),
                _ => { });
            var download = client.DownloadAsync(CancellationToken.None);
            Guid id = await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var offer = new LabFileOffer(id, "download.txt", body.Length, SHA256.HashData(body));
            await client.HandleAsync(new LabPacket(LabMessage.FileOffer,
                LabFileTransfer.OfferBytes(offer)), CancellationToken.None);
            Assert.Contains(responses, p => p.Type == LabMessage.FileDecision &&
                LabFileTransfer.ParseDecision(p.Payload).Accepted);
            await client.HandleAsync(new LabPacket(LabMessage.FileChunk,
                LabFileTransfer.Chunk(id, 0, body)), CancellationToken.None);
            await client.HandleAsync(new LabPacket(LabMessage.FileComplete,
                LabFileTransfer.TransferId(id)), CancellationToken.None);
            await download.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(body, File.ReadAllBytes(file));
            Assert.Contains(responses, p => p.Type == LabMessage.FileResult &&
                LabFileTransfer.ParseDecision(p.Payload).Accepted);
            Assert.False(client.Busy);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task UnsolicitedDownloadOfferIsRejected()
    {
        using var client = new LabViewerTransfers(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult<string?>(null),
            _ => { });
        var offer = new LabFileOffer(Guid.NewGuid(), "bad.txt", 1, new byte[32]);
        await Assert.ThrowsAsync<System.Security.Authentication.AuthenticationException>(() =>
            client.HandleAsync(new LabPacket(LabMessage.FileOffer,
                LabFileTransfer.OfferBytes(offer)), CancellationToken.None));
    }
    [Fact]
    public async Task UploadCanPauseWithoutStoppingTheTlsSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "TJRA-Pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "pause.bin");
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(180_000));
            var offered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            int chunkCount = 0;
            using var viewer = new LabViewerTransfers(async (type, payload) =>
            {
                if (type == LabMessage.FileOffer)
                    offered.TrySetResult(LabFileTransfer.ParseOffer(payload.Span).Id);
                if (type == LabMessage.FileChunk && Interlocked.Increment(ref chunkCount) == 1)
                {
                    firstChunk.TrySetResult();
                    await releaseFirst.Task;
                }
                if (type == LabMessage.FileComplete)
                    completed.TrySetResult(LabFileTransfer.ParseTransferId(payload.Span));
            }, (_, _) => Task.FromResult<string?>(null), _ => { });
            var upload = viewer.UploadAsync(path, CancellationToken.None);
            Guid id = await offered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await viewer.HandleAsync(new LabPacket(LabMessage.FileDecision,
                LabFileTransfer.Decision(id, true)), CancellationToken.None);
            await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await viewer.SetPausedAsync(true);
            Assert.True(viewer.Paused);
            releaseFirst.TrySetResult();
            await Task.Delay(120);
            Assert.Equal(1, Volatile.Read(ref chunkCount));
            await viewer.SetPausedAsync(false);
            Assert.Equal(id, await completed.Task.WaitAsync(TimeSpan.FromSeconds(15)));
            await viewer.HandleAsync(new LabPacket(LabMessage.FileResult,
                LabFileTransfer.Decision(id, true)), CancellationToken.None);
            Assert.True(await upload.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(viewer.Paused);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DownloadPauseAndResumeUseAuthenticatedTransferId()
    {
        var events = new ConcurrentQueue<LabPacket>();
        var requested = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var viewer = new LabViewerTransfers((type, payload) =>
        {
            events.Enqueue(new LabPacket(type, payload.ToArray()));
            if (type == LabMessage.FileRequest)
                requested.TrySetResult(LabFileTransfer.ParseTransferId(payload.Span));
            return Task.CompletedTask;
        }, (_, _) => Task.FromResult<string?>(null), _ => { });
        var download = viewer.DownloadAsync(CancellationToken.None);
        Guid id = await requested.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewer.SetPausedAsync(true));
        await viewer.HandleAsync(new LabPacket(LabMessage.FileOffer,
            LabFileTransfer.OfferBytes(new LabFileOffer(id, "no-save.bin", 0,
                SHA256.HashData(Array.Empty<byte>())))), CancellationToken.None);
        Assert.False(await download.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.False(viewer.Busy);
    }
}
