// SPDX-License-Identifier: GPL-2.0-or-later
// Explicit on-device consent for every file transfer. Remote peers do not select local paths.
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabTransferConsent
{
    internal static async Task<bool> ApproveUploadAsync(Form owner, LabFileOffer offer,
        string peerDescription, CancellationToken stop)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (owner.IsDisposed || !owner.IsHandleCreated) return false;
        try
        {
            owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (owner.IsDisposed) { completion.TrySetResult(false); return; }
                    var choice = MessageBox.Show(owner,
                        "An authenticated remote Viewer requests to SEND a file to this PC.\n" +
                        "Server approval is required for every transfer.\n\n" +
                        "Viewer: " + peerDescription + "\n" +
                        "File: " + offer.FileName + "\nSize: " + offer.Length.ToString("N0") +
                        " bytes\nDestination: current-user TJ.RemoteAccess\\Received\n\nApprove?",
                        "TJ.RemoteAccess - Incoming file",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    completion.TrySetResult(choice == DialogResult.Yes);
                }
                catch (Exception e) { completion.TrySetException(e); }
            }));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), stop)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
            TimeoutException or OperationCanceledException)
        {
            return false;
        }
    }

    internal static async Task<string?> SelectServerFileAsync(Form owner,
        string peerDescription, CancellationToken stop)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (owner.IsDisposed || !owner.IsHandleCreated) return null;
        try
        {
            owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (owner.IsDisposed) { completion.TrySetResult(null); return; }
                    var approve = MessageBox.Show(owner,
                        "An authenticated remote Viewer requests to RECEIVE a file from this PC.\n\n" +
                        "Viewer: " + peerDescription + "\n\nSelect one file to share? " +
                        "Only files explicitly chosen here can be sent.",
                        "TJ.RemoteAccess - Remote file request",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    if (approve != DialogResult.Yes)
                    {
                        completion.TrySetResult(null); return;
                    }
                    using var pick = new OpenFileDialog {
                        Title = "Choose file to send to Viewer", Filter = "All files (*.*)|*.*",
                        CheckFileExists = true, Multiselect = false
                    };
                    completion.TrySetResult(pick.ShowDialog(owner) == DialogResult.OK
                        ? pick.FileName : null);
                }
                catch (Exception e) { completion.TrySetException(e); }
            }));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), stop)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
            TimeoutException or OperationCanceledException)
        {
            return null;
        }
    }
}
