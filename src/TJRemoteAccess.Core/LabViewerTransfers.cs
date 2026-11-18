// SPDX-License-Identifier: GPL-2.0-or-later
// Viewer transfer state machine over the already pinned/authenticated screen TLS channel.
// One transfer at a time; remote storage paths are never accepted.
using System.Security.Authentication;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabViewerTransfers : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<LabMessage, ReadOnlyMemory<byte>, Task> _send;
    private readonly Func<LabFileOffer, CancellationToken, Task<string?>> _selectDestination;
    private readonly Action<string> _status;
    private Guid? _uploadId;
    private Guid? _downloadId;
    private TaskCompletionSource<bool>? _uploadDecision;
    private TaskCompletionSource<bool>? _uploadResult;
    private TaskCompletionSource<bool>? _downloadResult;
    private LabFileReceiver? _receive;

    internal LabViewerTransfers(
        Func<LabMessage, ReadOnlyMemory<byte>, Task> send,
        Func<LabFileOffer, CancellationToken, Task<string?>> selectDestination,
        Action<string> status)
    {
        _send = send;
        _selectDestination = selectDestination;
        _status = status;
    }

    internal static bool IsFileMessage(LabMessage type) => type is
        LabMessage.FileOffer or LabMessage.FileDecision or LabMessage.FileChunk or
        LabMessage.FileComplete or LabMessage.FileResult;

    internal bool Busy { get { lock (_sync) return _uploadId.HasValue || _downloadId.HasValue; } }

    internal async Task UploadAsync(string path, CancellationToken stop)
    {
        LabFileOffer offer = await LabFileTransfer.OfferFromFileAsync(path, stop);
        TaskCompletionSource<bool> decision = NewSignal();
        TaskCompletionSource<bool> result = NewSignal();
        lock (_sync)
        {
            if (_uploadId.HasValue || _downloadId.HasValue)
                throw new InvalidOperationException("File transfer is already running.");
            _uploadId = offer.Id;
            _uploadDecision = decision;
            _uploadResult = result;
        }
        try
        {
            _status("Requesting Server approval to send " + offer.FileName + "...");
            await _send(LabMessage.FileOffer, LabFileTransfer.OfferBytes(offer));
            bool approved = await decision.Task.WaitAsync(TimeSpan.FromMinutes(2), stop);
            if (!approved)
            {
                _status("Server declined file transfer.");
                return;
            }
            _status("Sending " + offer.FileName + " (up to 64 MiB)...");
            await LabFileTransfer.SendChunksAsync(path, offer,
                (type, body, ct) => _send(type, body), stop);
            bool verified = await result.Task.WaitAsync(TimeSpan.FromMinutes(2), stop);
            _status(verified ? "Server verified received file: " + offer.FileName :
                "Server rejected incomplete/corrupted file.");
        }
        finally
        {
            lock (_sync)
            {
                if (_uploadId == offer.Id)
                {
                    _uploadId = null; _uploadDecision = null; _uploadResult = null;
                }
            }
        }
    }

    internal async Task DownloadAsync(CancellationToken stop)
    {
        Guid id = Guid.NewGuid();
        TaskCompletionSource<bool> result = NewSignal();
        lock (_sync)
        {
            if (_uploadId.HasValue || _downloadId.HasValue)
                throw new InvalidOperationException("File transfer is already running.");
            _downloadId = id;
            _downloadResult = result;
        }
        try
        {
            _status("Requesting on-device Server consent to select a file...");
            await _send(LabMessage.FileRequest, LabFileTransfer.TransferId(id));
            bool verified = await result.Task.WaitAsync(TimeSpan.FromMinutes(5), stop);
            _status(verified ? "File downloaded and SHA-256 verified." :
                "Server declined or file was rejected.");
        }
        finally
        {
            lock (_sync)
            {
                if (_downloadId == id)
                {
                    _downloadId = null; _downloadResult = null;
                    _receive?.Dispose(); _receive = null;
                }
            }
        }
    }

    internal async Task HandleAsync(LabPacket packet, CancellationToken stop)
    {
        switch (packet.Type)
        {
            case LabMessage.FileDecision:
            {
                var response = LabFileTransfer.ParseDecision(packet.Payload);
                TaskCompletionSource<bool>? promise;
                lock (_sync)
                {
                    if (_uploadId != response.Id) throw new InvalidDataException("Unsolicited upload approval.");
                    promise = _uploadDecision;
                }
                promise?.TrySetResult(response.Accepted);
                break;
            }
            case LabMessage.FileResult:
            {
                var response = LabFileTransfer.ParseDecision(packet.Payload);
                TaskCompletionSource<bool>? promise;
                lock (_sync)
                {
                    promise = response.Id == _uploadId ? _uploadResult :
                              response.Id == _downloadId ? _downloadResult : null;
                }
                if (promise is null) throw new InvalidDataException("Unsolicited transfer receipt.");
                promise.TrySetResult(response.Accepted);
                break;
            }
            case LabMessage.FileOffer:
            {
                LabFileOffer offer = LabFileTransfer.ParseOffer(packet.Payload);
                lock (_sync)
                {
                    if (_downloadId != offer.Id || _receive is not null)
                        throw new AuthenticationException("Unrequested server file.");
                }
                string? destination = await _selectDestination(offer, stop);
                LabFileReceiver? receiver = null;
                if (destination is not null)
                {
                    try { receiver = new LabFileReceiver(offer, destination); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or
                        UnauthorizedAccessException) { _status("Unable to save selected file: " + ex.Message); }
                }
                lock (_sync)
                {
                    if (_downloadId != offer.Id)
                    {
                        receiver?.Dispose();
                        throw new IOException("Download session was replaced while user selected a path.");
                    }
                    _receive = receiver;
                }
                await _send(LabMessage.FileDecision,
                    LabFileTransfer.Decision(offer.Id, receiver is not null));
                if (receiver is null)
                {
                    lock (_sync) _downloadResult?.TrySetResult(false);
                }
                else _status("Receiving " + offer.FileName + "...");
                break;
            }
            case LabMessage.FileChunk:
            {
                var chunk = LabFileTransfer.ParseChunk(packet.Payload);
                LabFileReceiver receiver;
                lock (_sync)
                    receiver = _receive ?? throw new InvalidDataException("Unrequested transfer chunk.");
                receiver.AddChunk(chunk.Id, chunk.Offset, chunk.Data);
                if (receiver.Received % (1024 * 1024) < chunk.Data.Length)
                    _status("Receiving file " + receiver.Received.ToString("N0") +
                            "/" + receiver.Total.ToString("N0") + " bytes");
                break;
            }
            case LabMessage.FileComplete:
            {
                Guid id = LabFileTransfer.ParseTransferId(packet.Payload);
                LabFileReceiver receiver;
                lock (_sync)
                    receiver = _receive ?? throw new InvalidDataException("Unrequested transfer completion.");
                bool verified;
                try { receiver.Finish(id); verified = true; }
                catch (Exception ex) when (ex is InvalidDataException or IOException or
                    UnauthorizedAccessException) { verified = false; }
                finally { lock (_sync) { _receive?.Dispose(); _receive = null; } }
                await _send(LabMessage.FileResult, LabFileTransfer.Decision(id, verified));
                lock (_sync) _downloadResult?.TrySetResult(verified);
                break;
            }
            default: throw new InvalidDataException("Unexpected transfer packet.");
        }
    }

    internal void Disconnect()
    {
        lock (_sync)
        {
            _uploadDecision?.TrySetCanceled();
            _uploadResult?.TrySetCanceled();
            _downloadResult?.TrySetCanceled();
            _uploadDecision = null; _uploadResult = null; _downloadResult = null;
            _uploadId = null; _downloadId = null;
            _receive?.Dispose(); _receive = null;
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => Disconnect();
}
