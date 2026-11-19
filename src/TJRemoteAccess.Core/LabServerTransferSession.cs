// SPDX-License-Identifier: GPL-2.0-or-later
// Per-authenticated-session file transfers. Never handles arbitrary remote paths.
using System.Net.Security;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerTransferSession : IDisposable
{
    private readonly Form _owner;
    private readonly SslStream _tls;
    private readonly SemaphoreSlim _writer;
    private readonly string _peer;
    private readonly Func<bool> _authorized;
    private readonly Action<string> _status;
    private readonly Action<Exception> _abortSession;
    private readonly LabRemoteFileStore _fileStore;
    private readonly LabRemoteFileAccessPolicy _filePolicy;
    private Task? _outgoingDownload;
    private readonly object _pauseLock = new();
    private TaskCompletionSource<bool> _downloadResume = CompletedSignal();
    private bool _downloadPaused;

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult(true);
        return signal;
    }

    private Task WaitForResumeAsync(CancellationToken token)
    {
        lock (_pauseLock) return _downloadResume.Task.WaitAsync(token);
    }
    private LabFileReceiver? _receive;
    private LabFileOffer? _sendOffer;
    private string? _sendPath;
    private bool _awaitingResult;
    private LabServerRemoteFiles? _browser;

    internal LabServerTransferSession(Form owner, SslStream tls, SemaphoreSlim writer,
        string peer, Func<bool> authorized, Action<string> status,
        Action<Exception> abortSession,
        LabRemoteFileStore fileStore, LabRemoteFileAccessPolicy filePolicy)
    {
        _fileStore = fileStore; _filePolicy = filePolicy;
        _owner = owner;
        _tls = tls;
        _writer = writer;
        _peer = peer;
        _authorized = authorized;
        _status = status;
        _abortSession = abortSession;
    }

    internal void SetRemoteFiles(LabServerRemoteFiles browser) => _browser = browser;

    internal static bool IsFileMessage(LabMessage type) => type is
        LabMessage.FileOffer or LabMessage.FileDecision or LabMessage.FileChunk or
        LabMessage.FileComplete or LabMessage.FileResult or LabMessage.FileRequest or
        LabMessage.FilePause or LabMessage.FileResume or LabMessage.FileAbort;

    private async Task SendAsync(LabMessage type, byte[] data, CancellationToken stop) =>
        await LabWire.WriteAsync(_tls, _writer, type, data, stop).ConfigureAwait(false);

    internal async Task HandleAsync(LabPacket packet, CancellationToken stop)
    {
        // A Viewer cannot use file transfer to bypass controller revocation.
        if (!_authorized() || !_filePolicy.RootUnchanged)
            throw new System.Security.Authentication.AuthenticationException(
                "File transfer requires a Controller and an unchanged local share root.");
        if (packet.Type is LabMessage.FileOffer or LabMessage.FileChunk or LabMessage.FileComplete)
        {
            if (!_filePolicy.Upload)
                throw new System.Security.Authentication.AuthenticationException(
                    "Server owner disabled incoming file transfer.");
        }
        if (packet.Type == LabMessage.FileRequest && !_filePolicy.Download)
        {
            var request = LabRemoteFileWire.ParseDownloadRequest(packet.Payload);
            await SendAsync(LabMessage.FileResult,
                LabFileTransfer.Decision(request.Id, false), stop).ConfigureAwait(false);
            return;
        }
        switch (packet.Type)
        {
            case LabMessage.FileAbort:
            {
                var cancel = LabFileTransfer.ParseDecision(packet.Payload);
                if (_receive is null || _receive.Id != cancel.Id)
                    throw new InvalidDataException("Unsolicited upload abort.");
                if (!cancel.Accepted) _receive.Discard();
                _receive.Dispose();
                _receive = null;
                _status("Incoming upload aborted by Viewer." +
                    (cancel.Accepted ? " Partial retained for a verified retry." : " Partial discarded."));
                break;
            }
            case LabMessage.FilePause:
            case LabMessage.FileResume:
            {
                Guid id = LabFileTransfer.ParseTransferId(packet.Payload);
                if (_sendOffer is null || id != _sendOffer.Id || !_awaitingResult)
                    throw new InvalidDataException("Unsolicited file pause/resume.");
                lock (_pauseLock)
                {
                    if (packet.Type == LabMessage.FilePause && !_downloadPaused)
                    {
                        _downloadPaused = true;
                        _downloadResume = new TaskCompletionSource<bool>(
                            TaskCreationOptions.RunContinuationsAsynchronously);
                    }
                    else if (packet.Type == LabMessage.FileResume)
                    {
                        _downloadPaused = false;
                        _downloadResume.TrySetResult(true);
                    }
                }
                _status(packet.Type == LabMessage.FilePause ? "Download paused by Viewer." :
                    "Download resumed by Viewer.");
                break;
            }
            case LabMessage.FileOffer:
            {
                if (_receive is not null || _sendOffer is not null)
                    throw new InvalidDataException("Another file transfer is active.");
                LabFileOffer offer = LabFileTransfer.ParseOffer(packet.Payload);
                bool allow = await LabTransferConsent.ApproveUploadAsync(_owner, offer, _peer,
                    stop, _fileStore.Root).ConfigureAwait(false);
                // User approval does not survive a controller/desktop revocation.
                allow = allow && _authorized() && !stop.IsCancellationRequested;
                if (allow)
                {
                    try { _receive = LabFileReceiver.ForServer(offer, _fileStore); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidDataException) { allow = false; }
                }
                await SendAsync(LabMessage.FileDecision,
                    allow && _receive is { Received: > 0 } ?
                    LabFileTransfer.ResumeDecision(offer.Id, _receive.Received,
                        _receive.ResumePrefixSha256!) :
                    LabFileTransfer.Decision(offer.Id, allow), stop).ConfigureAwait(false);
                if (allow) _status("Incoming file accepted: " + offer.FileName);
                break;
            }
            case LabMessage.FileChunk:
            {
                var chunk = LabFileTransfer.ParseChunk(packet.Payload);
                if (_receive is null) throw new InvalidDataException("File chunk without offer.");
                _receive.AddChunk(chunk.Id, chunk.Offset, chunk.Data);
                break;
            }
            case LabMessage.FileComplete:
            {
                Guid id = LabFileTransfer.ParseTransferId(packet.Payload);
                if (_receive is null || _receive.Id != id)
                    throw new InvalidDataException("File completion without matching offer.");
                bool ok;
                try
                {
                    string saved = _receive.Finish(id);
                    _status("File received and verified: " + Path.GetFileName(saved));
                    ok = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidDataException) { ok = false; }
                finally { _receive.Dispose(); _receive = null; }
                await SendAsync(LabMessage.FileResult, LabFileTransfer.Decision(id, ok), stop)
                    .ConfigureAwait(false);
                break;
            }
            case LabMessage.FileRequest:
            {
                if (_receive is not null || _sendOffer is not null)
                    throw new InvalidDataException("Another transfer is active.");
                var request = LabRemoteFileWire.ParseDownloadRequest(packet.Payload);
                Guid id = request.Id;
                string? path = null;
                if (request.RelativeFile.Length == 0)
                {
                    path = await LabTransferConsent.SelectServerFileAsync(_owner, _peer, stop)
                        .ConfigureAwait(false);
                }
                else if (_browser?.BrowserApproved == true)
                {
                    try
                    {
                        // Resolve only within the authenticated Server's Received root.
                        _fileStore.SelectedFile(request.RelativeFile);
                        if (await _browser.ApproveSelectedDownloadAsync(request.RelativeFile, stop)
                            .ConfigureAwait(false))
                            path = _fileStore.SelectedFile(request.RelativeFile);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or
                        UnauthorizedAccessException) { path = null; }
                }
                // The operator can take time to choose; recheck before disclosure.
                if (!_authorized() || stop.IsCancellationRequested) path = null;
                if (path is null)
                {
                    await SendAsync(LabMessage.FileResult, LabFileTransfer.Decision(id, false), stop)
                        .ConfigureAwait(false);
                    break;
                }
                try
                {
                    LabFileOffer offer = (await LabFileTransfer.OfferFromFileAsync(path, stop)
                        .ConfigureAwait(false)) with { Id = id };
                    _sendOffer = offer;
                    _sendPath = path;
                    await SendAsync(LabMessage.FileOffer, LabFileTransfer.OfferBytes(offer), stop)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or
                    UnauthorizedAccessException)
                {
                    await SendAsync(LabMessage.FileResult, LabFileTransfer.Decision(id, false), stop)
                        .ConfigureAwait(false);
                }
                break;
            }
            case LabMessage.FileDecision:
            {
                var decision = LabFileTransfer.ParseResumeDecision(packet.Payload);
                if (_sendOffer is null || decision.Id != _sendOffer.Id ||
                    _sendPath is null || _awaitingResult ||
                    decision.Offset > _sendOffer.Length)
                    throw new InvalidDataException("Unsolicited or invalid file decision.");
                if (!decision.Accepted)
                {
                    _status("Viewer declined incoming file.");
                    _sendOffer = null; _sendPath = null;
                    break;
                }
                // Never block the Server's packet-read loop on a multi-gigabyte
                // download: incoming mouse/keyboard packets must remain responsive.
                // TLS writes still share the existing frame/control semaphore.
                var offeredFile = _sendOffer;
                var sourcePath = _sendPath;
                _awaitingResult = true;
                _outgoingDownload = SendOutgoingAsync(sourcePath, offeredFile, stop,
                    decision.Offset, decision.PrefixSha256);
                _ = _outgoingDownload.ContinueWith(task =>
                {
                    if (!stop.IsCancellationRequested && task.IsFaulted)
                    {
                        var failure = task.Exception!.GetBaseException();
                        _status("Download stopped: " + failure.Message);
                        _abortSession(failure); // Fail closed; discard partial file.
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                _status("Sending file in background; remote inputs remain enabled.");
                break;
            }
            case LabMessage.FileResult:
            {
                var result = LabFileTransfer.ParseDecision(packet.Payload);
                if (_sendOffer is null || _sendOffer.Id != result.Id || !_awaitingResult ||
                    _outgoingDownload is null)
                    throw new InvalidDataException("Unsolicited transfer result.");
                // A TLS receipt can arrive immediately after the final write,
                // before its sender Task has transitioned to Completed.
                await _outgoingDownload.WaitAsync(TimeSpan.FromSeconds(10), stop)
                    .ConfigureAwait(false);
                _status(result.Accepted ? "Viewer verified received file." :
                    "Viewer rejected incomplete or corrupted file.");
                _sendOffer = null; _sendPath = null; _awaitingResult = false;
                _outgoingDownload = null;
                lock (_pauseLock)
                {
                    _downloadPaused = false;
                    _downloadResume.TrySetResult(true);
                }
                break;
            }
            default:
                throw new InvalidDataException("Unsupported transfer message.");
        }
    }

    private Task SendOutgoingAsync(string path, LabFileOffer offer, CancellationToken stop,
        long offset, byte[]? prefixSha256) =>
        LabFileTransfer.SendChunksAsync(path, offer,
            async (type, data, token) =>
            {
                // Recheck the active controller and desktop before every chunk,
                // including the final completion message.
                await WaitForResumeAsync(token).ConfigureAwait(false);
                if (!_authorized() || !_filePolicy.Download)
                    throw new System.Security.Authentication.AuthenticationException(
                        "Transfer authorization or download access revoked during send.");
                await LabWire.WriteAsync(_tls, _writer, type, data, token)
                    .ConfigureAwait(false);
            }, stop, offset, prefixSha256);

    public void Dispose()
    {
        lock (_pauseLock) _downloadResume.TrySetCanceled();
        _receive?.Dispose();
        _receive = null;
        _sendOffer = null;
        _sendPath = null;
    }
}
