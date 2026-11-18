// SPDX-License-Identifier: GPL-2.0-or-later
// Server-side, operator-approved file browsing. Never grants arbitrary drive access.
using System.Net.Security;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerRemoteFiles
{
    private readonly Form _owner;
    private readonly SslStream _tls;
    private readonly SemaphoreSlim _sendLock;
    private readonly string _viewer;
    private readonly Func<bool> _isController;
    private readonly LabRemoteFileStore _files = LabRemoteFileStore.ForCurrentUser();
    private bool _browserApproved;

    internal LabServerRemoteFiles(Form owner, SslStream tls, SemaphoreSlim sendLock,
        string viewer, Func<bool> isController)
    {
        _owner = owner; _tls = tls; _sendLock = sendLock;
        _viewer = viewer; _isController = isController;
    }

    internal bool BrowserApproved => _browserApproved && _isController();

    internal async Task HandleAsync(LabPacket packet, CancellationToken stop)
    {
        if (!_isController())
            throw new System.Security.Authentication.AuthenticationException(
                "File manager needs a current Controller on the unlocked desktop.");
        var command = LabRemoteFileWire.DecodeCommand(packet.Payload);
        bool permitted = _browserApproved;
        if (!permitted)
        {
            permitted = await ConsentAsync("Open the TJ.RemoteAccess Received folder for " +
                "this authenticated Viewer?\n\nFiles in other drives/folders remain private.", stop);
            _browserApproved = permitted && _isController();
        }
        if (!permitted || !_isController())
        {
            await ReplyAsync(new(command.Id, false, command.Path,
                Array.Empty<LabRemoteFileItem>(), "Server user declined file browsing."), stop);
            return;
        }
        if (command.Action != "List")
        {
            string actionDescription = command.Action switch
            {
                "CreateFolder" => "Create folder '" + command.NewName + "' in " + Display(command.Path),
                "Copy" => "Copy one file " + Display(command.Path) + " into " + Display(command.NewName) + " (never overwrite)",
                "Rename" => "Rename " + Display(command.Path) + " to '" + command.NewName + "'",
                "Delete" => "Delete " + Display(command.Path) +
                    "? Only one file or empty folder may be removed.",
                _ => throw new InvalidDataException("Invalid remote file action.")
            };
            permitted = await ConsentAsync(actionDescription +
                "\n\nApprove this ONE remote file operation?", stop);
        }
        if (!permitted || !_isController())
        {
            await ReplyAsync(new(command.Id, false, command.Path,
                Array.Empty<LabRemoteFileItem>(), "Server user declined the requested action."), stop);
            return;
        }
        LabRemoteFileReply result;
        try
        {
            result = command.Action == "List" ?
                _files.List(command.Id, command.Path) : _files.Mutate(command, stop, _isController);
        }
        catch (InvalidDataException ex) when (ex.Message is
            "Folder contains more than 160 entries; incomplete listing refused." or
            "Folder contains a linked entry; listing refused.")
        {
            result = new(command.Id, false, command.Path,
                Array.Empty<LabRemoteFileItem>(), ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException)
        {
            // Report only a generic code to avoid leaking local filesystem layout.
            result = new(command.Id, false, command.Path,
                Array.Empty<LabRemoteFileItem>(), "Cannot access the selected file or folder.");
        }
        await ReplyAsync(result, stop);
    }

    internal async Task<bool> ApproveSelectedDownloadAsync(string relative, CancellationToken stop)
    {
        if (!BrowserApproved) return false;
        bool ok = await ConsentAsync(
            "The remote Viewer requests a copy of this file from the approved Received folder:\n\n" +
            Display(relative) + "\n\nApprove this ONE download?", stop);
        return ok && BrowserApproved;
    }

    private static string Display(string relative) =>
        "Received" + (relative.Length == 0 ? "" : "/" + relative);

    private Task ReplyAsync(LabRemoteFileReply result, CancellationToken stop) =>
        LabWire.WriteAsync(_tls, _sendLock, LabMessage.FileManagerReply,
            LabRemoteFileWire.EncodeReply(result), stop);

    private async Task<bool> ConsentAsync(string question, CancellationToken stop)
    {
        if (_owner.IsDisposed || !_owner.IsHandleCreated || !_isController()) return false;
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _owner.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_owner.IsDisposed || !_isController())
                    { completion.TrySetResult(false); return; }
                    var answer = MessageBox.Show(_owner,
                        "Viewer: " + _viewer + "\n\n" + question,
                        "TJ.RemoteAccess - Transfer Files",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    completion.TrySetResult(answer == DialogResult.Yes &&
                        _isController() && !stop.IsCancellationRequested);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            }));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), stop);
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            ObjectDisposedException or TimeoutException or OperationCanceledException)
        { return false; }
    }
}
