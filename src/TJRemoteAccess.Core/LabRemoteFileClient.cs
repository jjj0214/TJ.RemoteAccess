// SPDX-License-Identifier: GPL-2.0-or-later
// Serial request/response over existing TLS. One remote filesystem request at a time.
// Refuses unsolicited and mismatched replies, and cancels pending UI on disconnect.
namespace TJ.RemoteAccess.Lab;

internal sealed class LabRemoteFileClient
{
    private readonly Func<LabMessage, ReadOnlyMemory<byte>, Task> _send;
    private readonly object _gate = new();
    private Guid? _id;
    private TaskCompletionSource<LabRemoteFileReply>? _pending;

    internal LabRemoteFileClient(Func<LabMessage, ReadOnlyMemory<byte>, Task> send) =>
        _send = send;

    internal async Task<LabRemoteFileReply> ExecuteAsync(
        string action, string path, string newName, CancellationToken stop)
    {
        var command = LabRemoteFileWire.CheckCommand(
            new LabRemoteFileCommand(Guid.NewGuid(), action, path, newName));
        var source = new TaskCompletionSource<LabRemoteFileReply>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_pending is not null) throw new InvalidOperationException(
                "Another remote file operation is in progress.");
            _id = command.Id;
            _pending = source;
        }
        try
        {
            await _send(LabMessage.FileManagerRequest, LabRemoteFileWire.EncodeCommand(command));
            return await source.Task.WaitAsync(TimeSpan.FromMinutes(3), stop);
        }
        finally
        {
            lock (_gate)
            {
                if (_id == command.Id)
                {
                    _id = null;
                    _pending = null;
                }
            }
        }
    }

    internal void HandleReply(ReadOnlySpan<byte> body)
    {
        var reply = LabRemoteFileWire.DecodeReply(body);
        TaskCompletionSource<LabRemoteFileReply>? target;
        lock (_gate)
        {
            if (_id != reply.Id || _pending is null)
                throw new InvalidDataException("Unsolicited remote file list or mutation response.");
            target = _pending;
        }
        target.TrySetResult(reply);
    }

    internal void Disconnect()
    {
        lock (_gate)
        {
            _pending?.TrySetCanceled();
            _id = null;
            _pending = null;
        }
    }
}
