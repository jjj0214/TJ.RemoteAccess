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
    private readonly Action<long, long>? _progress;
    private Guid? _uploadId;
    private Guid? _downloadId;
    private TaskCompletionSource<LabResumeDecision>? _uploadDecision;
    private TaskCompletionSource<bool>? _uploadResult;
    private TaskCompletionSource<bool>? _downloadResult;
    private LabFileReceiver? _receive;
    private TaskCompletionSource<bool> _resumeSignal = CompletedSignal();
    private bool _paused;
    private bool _activeBytes;

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = NewSignal();
        signal.TrySetResult(true);
        return signal;
    }

    internal bool Paused { get { lock (_sync) return _paused; } }

    internal async Task SetPausedAsync(bool pause)
    {
        Guid? download;
        lock (_sync)
        {
            if (!_activeBytes)
                throw new InvalidOperationException("Pause is available after Server approval and transfer start.");
            if (_paused == pause) return;
            _paused = pause;
            if (pause) _resumeSignal = NewSignal();
            else _resumeSignal.TrySetResult(true);
            download = _downloadId;
        }
        // For downloads, the sender must stop producing chunks. Merely
        // suspending the Viewer read loop would block all desktop frames.
        if (download.HasValue)
            await _send(pause ? LabMessage.FilePause : LabMessage.FileResume,
                LabFileTransfer.TransferId(download.Value));
    }

    private Task WaitForResumeAsync(CancellationToken stop)
    {
        lock (_sync) return _resumeSignal.Task.WaitAsync(stop);
    }

    private void ResetPause()
    {
        _paused = false;
        _activeBytes = false;
        _resumeSignal.TrySetResult(true);
    }

    internal LabViewerTransfers(
        Func<LabMessage, ReadOnlyMemory<byte>, Task> send,
        Func<LabFileOffer, CancellationToken, Task<string?>> selectDestination,
        Action<string> status, Action<long, long>? progress = null)
    {
        _send = send;
        _selectDestination = selectDestination;
        _status = status;
        _progress = progress;
    }

    internal static bool IsFileMessage(LabMessage type) => type is
        LabMessage.FileOffer or LabMessage.FileDecision or LabMessage.FileChunk or
        LabMessage.FileComplete or LabMessage.FileResult;

    internal bool Busy { get { lock (_sync) return _uploadId.HasValue || _downloadId.HasValue; } }

    internal async Task<bool> UploadAsync(string path, CancellationToken stop, string remoteFolder = "")
    {
        LabFileOffer offer = (await LabFileTransfer.OfferFromFileAsync(path, stop)) with
            { TargetFolder = LabRemoteFileWire.CheckRelative(remoteFolder) };
        TaskCompletionSource<LabResumeDecision> decision = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> result = NewSignal();
        lock (_sync)
        {
            if (_uploadId.HasValue || _downloadId.HasValue)
                throw new InvalidOperationException("File transfer is already running.");
            _uploadId = offer.Id;
            _uploadDecision = decision;
            _uploadResult = result;
        }
        bool offered = false;
        try
        {
            _status("Requesting Server approval to send " + offer.FileName + "...");
            await _send(LabMessage.FileOffer, LabFileTransfer.OfferBytes(offer));
            offered = true;
            var approval = await decision.Task.WaitAsync(TimeSpan.FromMinutes(2), stop);
            if (!approval.Accepted)
            {
                _status("Server declined file transfer.");
                return false;
            }
            if (approval.Offset > offer.Length)
                throw new InvalidDataException("Server resume offset exceeds file length.");
            _status(approval.Offset > 0 ? "Resuming " + offer.FileName + " from " +
                approval.Offset.ToString("N0") + " bytes..." :
                "Sending " + offer.FileName + " (up to 4 GiB)...");
            long sent = approval.Offset;
            lock (_sync) _activeBytes = true;
            _progress?.Invoke(sent, offer.Length);
            await LabFileTransfer.SendChunksAsync(path, offer,
                async (type, body, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    await WaitForResumeAsync(ct);
                    await _send(type, body);
                    if (type == LabMessage.FileChunk)
                    {
                        sent += body.Length - 24; // Payload: 16-byte GUID + 8-byte offset.
                        _progress?.Invoke(sent, offer.Length);
                    }
                }, stop, approval.Offset, approval.PrefixSha256);
            bool verified = await result.Task.WaitAsync(TimeSpan.FromMinutes(2), stop);
            _status(verified ? "Server verified received file: " + offer.FileName :
                "Server rejected incomplete/corrupted file.");
            return verified;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or
            UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            // A source error must release the Server's in-flight receiver state
            // so the next independent file can still be attempted.
            if (offered)
            {
                try
                {
                    bool retainValidPart = !stop.IsCancellationRequested &&
                        failure is not InvalidDataException;
                    await _send(LabMessage.FileAbort,
                        LabFileTransfer.Decision(offer.Id, retainValidPart));
                }
                catch (Exception sendError) when (sendError is IOException or
                    InvalidOperationException or OperationCanceledException or
                    ObjectDisposedException) { }
            }
            throw;
        }
        finally
        {
            lock (_sync)
            {
                if (_uploadId == offer.Id)
                {
                    _uploadId = null; _uploadDecision = null; _uploadResult = null;
                    ResetPause();
                }
            }
        }
    }

    internal async Task<bool> DownloadAsync(CancellationToken stop, string remoteFile = "")
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
            await _send(LabMessage.FileRequest, remoteFile.Length == 0 ?
                LabFileTransfer.TransferId(id) : LabRemoteFileWire.DownloadRequest(id, remoteFile));
            // Wall-clock five minutes is insufficient for a 4-GiB transfer
            // on slow LAN links. Incoming packets keep the session alive;
            // retain a bounded overall deadline for a genuinely stalled peer.
            bool verified = await result.Task.WaitAsync(TimeSpan.FromHours(2), stop);
            _status(verified ? "File downloaded and SHA-256 verified." :
                "Server declined or file was rejected.");
            return verified;
        }
        finally
        {
            lock (_sync)
            {
                if (_downloadId == id)
                {
                    _downloadId = null; _downloadResult = null;
                    ResetPause();
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
                var response = LabFileTransfer.ParseResumeDecision(packet.Payload);
                TaskCompletionSource<LabResumeDecision>? promise;
                lock (_sync)
                {
                    if (_uploadId != response.Id) throw new InvalidDataException("Unsolicited upload approval.");
                    promise = _uploadDecision;
                }
                promise?.TrySetResult(response);
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
                    try { receiver = new LabFileReceiver(offer, destination, resumable: true); }
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
                    _activeBytes = receiver is not null;
                }
                await _send(LabMessage.FileDecision,
                    receiver is { Received: > 0 } ?
                    LabFileTransfer.ResumeDecision(offer.Id, receiver.Received,
                        receiver.ResumePrefixSha256!) :
                    LabFileTransfer.Decision(offer.Id, receiver is not null));
                if (receiver is null)
                {
                    lock (_sync) _downloadResult?.TrySetResult(false);
                }
                else
                {
                    _progress?.Invoke(0, offer.Length);
                    _status("Receiving " + offer.FileName + "...");
                }
                break;
            }
            case LabMessage.FileChunk:
            {
                var chunk = LabFileTransfer.ParseChunk(packet.Payload);
                LabFileReceiver receiver;
                lock (_sync)
                    receiver = _receive ?? throw new InvalidDataException("Unrequested transfer chunk.");
                receiver.AddChunk(chunk.Id, chunk.Offset, chunk.Data);
                _progress?.Invoke(receiver.Received, receiver.Total);
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

    internal void DiscardPendingDownload()
    {
        lock (_sync)
        {
            _receive?.Discard();
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
            ResetPause();
            _receive?.Dispose(); _receive = null;
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose() => Disconnect();
}
