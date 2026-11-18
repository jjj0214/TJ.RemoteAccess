// SPDX-License-Identifier: GPL-2.0-or-later
// Clipboard transport is opt-in per operation and is serviced ONLY on the
// logged-in Server user's STA UI thread. Do not run clipboard APIs in LocalSystem.
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabClipboardConsent
{
    internal static async Task<bool> SetTextAsync(Form owner, string text,
        string peer, Func<bool> authorized, CancellationToken stop)
    {
        if (owner.IsDisposed || !owner.IsHandleCreated || !authorized()) return false;
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (owner.IsDisposed || !authorized())
                    { signal.TrySetResult(false); return; }
                    var answer = MessageBox.Show(owner,
                        "The authenticated remote Controller requests to PASTE text " +
                        "into this PC's clipboard.\n\n" +
                        "Viewer: " + peer + "\nLength: " + text.Length.ToString("N0") +
                        " characters\n\nApprove this one-time clipboard change?",
                        "TJ.RemoteAccess - Remote clipboard write",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes || !authorized() || stop.IsCancellationRequested)
                    { signal.TrySetResult(false); return; }
                    Clipboard.SetText(text, TextDataFormat.UnicodeText);
                    signal.TrySetResult(true);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                    or InvalidOperationException or System.Threading.ThreadStateException)
                { signal.TrySetResult(false); }
            }));
            return await signal.Task.WaitAsync(TimeSpan.FromMinutes(2), stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or TimeoutException or OperationCanceledException)
        { return false; }
    }

    internal static async Task<string?> GetTextAsync(Form owner, string peer,
        Func<bool> authorized, CancellationToken stop)
    {
        if (owner.IsDisposed || !owner.IsHandleCreated || !authorized()) return null;
        var signal = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (owner.IsDisposed || !authorized())
                    { signal.TrySetResult(null); return; }
                    var answer = MessageBox.Show(owner,
                        "The authenticated remote Controller requests the contents " +
                        "of THIS PC's text clipboard. It may contain secrets.\n\n" +
                        "Viewer: " + peer + "\n\nShare only this one text value?",
                        "TJ.RemoteAccess - Remote clipboard read",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (answer != DialogResult.Yes || !authorized() || stop.IsCancellationRequested ||
                        !Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    { signal.TrySetResult(null); return; }
                    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    // Reject unbounded or malformed content instead of silently truncating.
                    LabClipboardWire.Text(Guid.NewGuid(), text);
                    signal.TrySetResult(text);
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
                    or InvalidDataException or InvalidOperationException
                    or System.Threading.ThreadStateException)
                { signal.TrySetResult(null); }
            }));
            return await signal.Task.WaitAsync(TimeSpan.FromMinutes(2), stop).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException
            or TimeoutException or OperationCanceledException)
        { return null; }
    }
}
