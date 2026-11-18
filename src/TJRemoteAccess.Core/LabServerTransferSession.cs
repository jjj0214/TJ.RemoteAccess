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
    private LabFileReceiver? _receive;
    private LabFileOffer? _sendOffer;
    private string? _sendPath;
    private bool _awaitingResult;

    internal LabServerTransferSession(Form owner, SslStream tls, SemaphoreSlim writer,
        string peer, Func<bool> authorized, Action<string> status)
    {
        _owner = owner;
        _tls = tls;
        _writer = writer;
        _peer = peer;
        _authorized = authorized;
        _status = status;
    }

    internal static bool IsFileMessage(LabMessage type) => type is
        LabMessage.FileOffer or LabMessage.FileDecision or LabMessage.FileChunk or
        LabMessage.FileComplete or LabMessage.FileResult or LabMessage.FileRequest;

    private async Task SendAsync(LabMessage type, byte[] data, CancellationToken stop) =>
        await LabWire.WriteAsync(_tls, _writer, type, data, stop).ConfigureAwait(false);

    internal async Task HandleAsync(LabPacket packet, CancellationToken stop)
    {
        // A Viewer cannot use file transfer to bypass input privilege revocation.
        if (!_authorized())
            throw new System.Security.Authentication.AuthenticationException(
                "File transfer requires a currently authorized controller on the normal desktop.");
        switch (packet.Type)
        {
            case LabMessage.FileOffer:
            {
                if (_receive is not null || _sendOffer is not null)
                    throw new InvalidDataException("Another file transfer is active.");
                LabFileOffer offer = LabFileTransfer.ParseOffer(packet.Payload);
                bool allow = await LabTransferConsent.ApproveUploadAsync(_owner, offer, _peer, stop)
                    .ConfigureAwait(false);
                // User approval does not survive a controller/desktop revocation.
                allow = allow && _authorized() && !stop.IsCancellationRequested;
                if (allow)
                {
                    try { _receive = LabFileReceiver.ForServer(offer); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        InvalidDataException) { allow = false; }
                }
                await SendAsync(LabMessage.FileDecision,
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
                Guid id = LabFileTransfer.ParseTransferId(packet.Payload);
                string? path = await LabTransferConsent.SelectServerFileAsync(_owner, _peer, stop)
                    .ConfigureAwait(false);
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
                var decision = LabFileTransfer.ParseDecision(packet.Payload);
                if (_sendOffer is null || decision.Id != _sendOffer.Id ||
                    _sendPath is null || _awaitingResult)
                    throw new InvalidDataException("Unsolicited file decision.");
                if (!decision.Accepted)
                {
                    _status("Viewer declined incoming file.");
                    _sendOffer = null; _sendPath = null;
                    break;
                }
                await LabFileTransfer.SendChunksAsync(_sendPath, _sendOffer,
                    async (type, data, token) =>
                    {
                        // Revoking controller/desktop authorization also stops
                        // in-flight downloads before another file chunk is emitted.
                        if (!_authorized())
                            throw new System.Security.Authentication.AuthenticationException(
                                "Transfer authorization revoked during send.");
                        await LabWire.WriteAsync(_tls, _writer, type, data, token)
                            .ConfigureAwait(false);
                    }, stop).ConfigureAwait(false);
                _awaitingResult = true;
                _status("File sent to Viewer; awaiting SHA-256 confirmation.");
                break;
            }
            case LabMessage.FileResult:
            {
                var result = LabFileTransfer.ParseDecision(packet.Payload);
                if (_sendOffer is null || _sendOffer.Id != result.Id || !_awaitingResult)
                    throw new InvalidDataException("Unsolicited transfer result.");
                _status(result.Accepted ? "Viewer verified received file." :
                    "Viewer rejected incomplete or corrupted file.");
                _sendOffer = null; _sendPath = null; _awaitingResult = false;
                break;
            }
            default:
                throw new InvalidDataException("Unsupported transfer message.");
        }
    }

    public void Dispose()
    {
        _receive?.Dispose();
        _receive = null;
        _sendOffer = null;
        _sendPath = null;
    }
}
