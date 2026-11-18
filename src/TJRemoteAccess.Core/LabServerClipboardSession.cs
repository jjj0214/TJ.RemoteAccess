// SPDX-License-Identifier: GPL-2.0-or-later
// One-time, on-device-approved clipboard text operations on the pinned TLS channel.
// Never accepts remote filesystem paths or auto-synchronizes clipboard data.
using System.Net.Security;
using System.Security.Authentication;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerClipboardSession
{
    private readonly Form _owner;
    private readonly SslStream _tls;
    private readonly SemaphoreSlim _writer;
    private readonly string _peer;
    private readonly Func<bool> _authorized;

    internal LabServerClipboardSession(Form owner, SslStream tls,
        SemaphoreSlim writer, string peer, Func<bool> authorized)
    {
        _owner = owner;
        _tls = tls;
        _writer = writer;
        _peer = peer;
        _authorized = authorized;
    }

    internal static bool IsClipboardMessage(LabMessage type) =>
        type is LabMessage.ClipboardSet or LabMessage.ClipboardGet;

    internal async Task HandleAsync(LabPacket packet, CancellationToken stop)
    {
        if (!_authorized() || stop.IsCancellationRequested)
            throw new AuthenticationException("Clipboard action requires a current Controller on the normal desktop.");
        if (packet.Type == LabMessage.ClipboardSet)
        {
            var change = LabClipboardWire.ParseText(packet.Payload);
            bool approved = await LabClipboardConsent.SetTextAsync(
                _owner, change.Text, _peer, _authorized, stop).ConfigureAwait(false);
            if (!_authorized() || stop.IsCancellationRequested)
                throw new AuthenticationException("Clipboard permission revoked during approval.");
            await LabWire.WriteAsync(_tls, _writer, LabMessage.ClipboardDecision,
                LabClipboardWire.Decision(change.Id, approved), stop).ConfigureAwait(false);
            return;
        }
        if (packet.Type == LabMessage.ClipboardGet)
        {
            Guid id = LabClipboardWire.ParseRequest(packet.Payload);
            string? text = await LabClipboardConsent.GetTextAsync(
                _owner, _peer, _authorized, stop).ConfigureAwait(false);
            if (!_authorized() || stop.IsCancellationRequested)
                throw new AuthenticationException("Clipboard permission revoked during approval.");
            if (text is null)
                await LabWire.WriteAsync(_tls, _writer, LabMessage.ClipboardDecision,
                    LabClipboardWire.Decision(id, false), stop).ConfigureAwait(false);
            else
                await LabWire.WriteAsync(_tls, _writer, LabMessage.ClipboardText,
                    LabClipboardWire.Text(id, text), stop).ConfigureAwait(false);
            return;
        }
        throw new InvalidDataException("Unsupported clipboard request.");
    }
}
