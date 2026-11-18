// SPDX-License-Identifier: GPL-2.0-or-later
using TJ.RemoteAccess.Lab;
using Xunit;

public sealed class LabRemoteFileClientTests
{
    [Fact]
    public async Task ClientMatchesOnlyRequestedResponseAndRejectsUnsolicitedReplies()
    {
        var source = new TaskCompletionSource<LabRemoteFileCommand>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new LabRemoteFileClient((type, data) =>
        {
            Assert.Equal(LabMessage.FileManagerRequest, type);
            source.TrySetResult(LabRemoteFileWire.DecodeCommand(data.Span));
            return Task.CompletedTask;
        });
        var awaiting = client.ExecuteAsync("List", "", "", CancellationToken.None);
        var command = await source.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Throws<InvalidDataException>(() => client.HandleReply(
            LabRemoteFileWire.EncodeReply(new(Guid.NewGuid(), true, "", Array.Empty<LabRemoteFileItem>()))));
        var response = new LabRemoteFileReply(command.Id, true, "", new[]
        {
            new LabRemoteFileItem("hello.txt", false, 7, DateTime.UtcNow)
        });
        client.HandleReply(LabRemoteFileWire.EncodeReply(response));
        var result = await awaiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result.Success);
        Assert.Equal("hello.txt", result.Items[0].Name);
        Assert.Throws<InvalidDataException>(() => client.HandleReply(
            LabRemoteFileWire.EncodeReply(response)));
    }

    [Fact]
    public async Task DisconnectCancelsPendingRemoteFileRequests()
    {
        var signaled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new LabRemoteFileClient((_, _) =>
        {
            signaled.TrySetResult(true);
            return Task.CompletedTask;
        });
        var pending = client.ExecuteAsync("List", "", "", CancellationToken.None);
        await signaled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        client.Disconnect();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
