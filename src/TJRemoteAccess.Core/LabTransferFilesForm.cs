// SPDX-License-Identifier: GPL-2.0-or-later
// User-facing, dual-pane file manager for the existing authenticated TLS session.
// Remote side is restricted to the current user's Received folder, not the system drive.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabTransferFilesForm : Form
{
    private readonly Func<string, string, string, CancellationToken, Task<LabRemoteFileReply>> _remote;
    private readonly Func<string, string, CancellationToken, Task> _upload;
    private readonly Func<string, string, CancellationToken, Task> _download;
    private readonly Func<bool> _isController;
    private readonly Action _disconnect;
    private readonly ListView _localList = MakeList();
    private readonly ListView _remoteList = MakeList();
    private readonly Label _localPath = MakeCaption();
    private readonly Label _remotePathLabel = MakeCaption();
    private readonly Label _status = new() {
        AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(38, 56, 75), Text = "Server permission is required before browsing remote files."
    };
    private readonly ProgressBar _progress = new() {
        Dock = DockStyle.Fill, Style = ProgressBarStyle.Blocks, Minimum = 0, Maximum = 100
    };
    private readonly Label _metrics = new() {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(64, 77, 94), Text = "Select files to transfer."
    };
    private readonly ListView _history = new() {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        HideSelection = false, GridLines = false, BackColor = Color.White
    };
    private DateTime _progressStartedUtc = DateTime.UtcNow;
    private long _progressTotal = -1;
    private long _lastProgressUiAt;
    private int _succeeded;
    private int _failed;
    private readonly Button _sendButton = MakeButton("Send →", true);
    private readonly Button _receiveButton = MakeButton("← Receive", true);
    private readonly Button _newFolderButton = MakeButton("New folder");
    private readonly Button _renameButton = MakeButton("Rename");
    private readonly Button _deleteButton = MakeButton("Delete");
    private readonly Button _copyRemoteButton = MakeButton("Copy");
    private readonly Button _pasteRemoteButton = MakeButton("Paste");
    private readonly Button _cancelButton = MakeButton("Cancel transfer");
    private readonly List<Button> _actions = new();
    private readonly List<Button> _navigation = new();
    private string _localFolder = Directory.Exists(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) ?
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private string _remoteFolder = "";
    private string? _copiedRemoteFile;
    private CancellationTokenSource? _operation;
    private bool _busy;

    internal string CurrentLocalFolder => _localFolder;

    internal LabTransferFilesForm(
        Func<string, string, string, CancellationToken, Task<LabRemoteFileReply>> remote,
        Func<string, string, CancellationToken, Task> upload,
        Func<string, string, CancellationToken, Task> download,
        Func<bool> isController,
        Action disconnect)
    {
        _remote = remote; _upload = upload; _download = download;
        _isController = isController; _disconnect = disconnect;
        Text = "TJ.RemoteAccess | Transfer Files";
        AutoScaleMode = AutoScaleMode.Dpi; // Respect high-DPI Windows displays.
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(920, 690);
        Size = new Size(1190, 730);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(245, 247, 250);
        ForeColor = Color.FromArgb(33, 48, 68);
        Padding = new Padding(12);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1,
            RowCount = 3, BackColor = BackColor };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));

        var top = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14) };
        top.Controls.Add(new Label {
            Text = "Transfer Files  |  파일 송수신", Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
            ForeColor = Color.FromArgb(27, 55, 90), AutoSize = true, Location = new Point(15, 8)
        });
        top.Controls.Add(new Label {
            Text = "Remote: approved Received folder only  ·  TLS Controller required  ·  Each transfer needs Server approval",
            ForeColor = Color.FromArgb(94, 107, 124), AutoSize = true, Location = new Point(16, 40)
        });
        layout.Controls.Add(top, 0, 0);

        var middle = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3,
            RowCount = 1, Padding = new Padding(0, 8, 0, 8) };
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136));
        middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
        middle.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        middle.Controls.Add(BuildPane("내 PC  /  Local computer", _localPath, _localList,
            ("Drives", () => ChooseDrive()), ("Folder...", () => ChooseLocalFolder()),
            ("Up", () => GoLocalUp()), ("Refresh", RefreshLocal)), 0, 0);
        middle.Controls.Add(BuildPane("원격 PC  /  Remote · Received", _remotePathLabel, _remoteList,
            ("Root", () => _ = ExecuteAsync(ct => BrowseRemoteAsync("", ct))),
            ("Up", () => _ = ExecuteAsync(ct => BrowseRemoteAsync(ParentRemote(_remoteFolder), ct))),
            ("Refresh", () => _ = ExecuteAsync(ct => BrowseRemoteAsync(_remoteFolder, ct)))), 2, 0);
        var operations = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true, Padding = new Padding(5, 55, 5, 0) };
        operations.Controls.Add(_sendButton);
        operations.Controls.Add(_receiveButton);
        operations.Controls.Add(new Label { Height = 15, Width = 115 });
        operations.Controls.Add(_newFolderButton);
        operations.Controls.Add(_renameButton);
        operations.Controls.Add(_deleteButton);
        operations.Controls.Add(_copyRemoteButton);
        operations.Controls.Add(_pasteRemoteButton);
        foreach (Button b in operations.Controls.OfType<Button>())
        { b.Width = 117; b.Height = 36; b.Margin = new Padding(0, 4, 0, 4); _actions.Add(b); }
        middle.Controls.Add(operations, 1, 0);
        layout.Controls.Add(middle, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3,
            RowCount = 4, BackColor = Color.White, Padding = new Padding(12, 9, 12, 8) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 99));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(_status, 0, 0);
        footer.SetColumnSpan(_status, 3);
        footer.Controls.Add(_progress, 0, 1);
        _cancelButton.Dock = DockStyle.Fill;
        _cancelButton.Enabled = false;
        footer.Controls.Add(_cancelButton, 1, 1);
        var close = MakeButton("Close");
        close.Dock = DockStyle.Fill;
        close.Click += (_, _) => Close();
        footer.Controls.Add(close, 2, 1);
        _history.Columns.Add("Result", 85);
        _history.Columns.Add("Direction", 90);
        _history.Columns.Add("File / folder", 330);
        _history.Columns.Add("Details", 450);
        _history.Resize += (_, _) =>
        {
            _history.Columns[3].Width = Math.Max(140,
                _history.ClientSize.Width - _history.Columns[0].Width -
                _history.Columns[1].Width - _history.Columns[2].Width - 26);
        };
        footer.Controls.Add(_metrics, 0, 2);
        footer.SetColumnSpan(_metrics, 3);
        footer.Controls.Add(_history, 0, 3);
        footer.SetColumnSpan(_history, 3);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        _sendButton.Click += async (_, _) => await ExecuteAsync(UploadSelectedAsync);
        _receiveButton.Click += async (_, _) => await ExecuteAsync(DownloadSelectedAsync);
        _newFolderButton.Click += async (_, _) => await ExecuteAsync(async ct =>
        {
            string? name = PromptName("New remote folder", "");
            if (name is null) return;
            await CommandAsync("CreateFolder", _remoteFolder, name, ct);
        });
        _renameButton.Click += async (_, _) => await ExecuteAsync(async ct =>
        {
            var selected = SelectedRemote();
            if (selected is null) return;
            string? name = PromptName("Rename remote entry", selected.Name);
            if (name is null || name == selected.Name) return;
            await CommandAsync("Rename", Join(_remoteFolder, selected.Name), name, ct);
        });
        _deleteButton.Click += async (_, _) => await ExecuteAsync(async ct =>
        {
            var selected = SelectedRemote();
            if (selected is null) return;
            if (MessageBox.Show(this, "Delete remote " + selected.Name +
                "? Server user must approve. Only empty folders can be removed.",
                "Confirm remote deletion", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await CommandAsync("Delete", Join(_remoteFolder, selected.Name), "", ct);
        });
        _copyRemoteButton.Click += (_, _) =>
        {
            var selected = SelectedRemote();
            if (selected is null) return;
            if (selected.IsDirectory)
            {
                SetStatus("Remote Copy currently supports individual files, not folders.");
                return;
            }
            _copiedRemoteFile = Join(_remoteFolder, selected.Name);
            SetStatus("Selected for remote copy: " + _copiedRemoteFile +
                ". Open the destination folder and click Paste.");
            RefreshActions();
        };
        _pasteRemoteButton.Click += async (_, _) => await ExecuteAsync(async ct =>
        {
            if (_copiedRemoteFile is null)
                throw new InvalidOperationException("Choose a remote file to copy first.");
            string source = _copiedRemoteFile;
            try
            {
                await CommandAsync("Copy", source, _remoteFolder, ct);
                RecordOutcome("Remote copy", source, true,
                    "Server approved copy to Received/" + _remoteFolder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                InvalidDataException or InvalidOperationException)
            {
                RecordOutcome("Remote copy", source, false, ex.Message);
                throw;
            }
        });
        _cancelButton.Click += (_, _) => CancelTransfer();
        InstallLocalContextMenu();
        _localList.DoubleClick += (_, _) =>
        {
            if (_busy || _localList.SelectedItems.Count == 0) return;
            if (_localList.SelectedItems[0].Tag is LocalEntry entry && entry.IsDirectory)
            { _localFolder = entry.FullPath; RefreshLocal(); }
        };
        _remoteList.DoubleClick += async (_, _) =>
        {
            if (_busy || SelectedRemote() is not { IsDirectory: true } entry) return;
            await ExecuteAsync(ct => BrowseRemoteAsync(Join(_remoteFolder, entry.Name), ct));
        };
        Shown += async (_, _) =>
        {
            RefreshLocal();
            await ExecuteAsync(ct => BrowseRemoteAsync("", ct));
        };
        FormClosing += (_, e) =>
        {
            // Closing the dialog never silently interrupts an in-flight transfer.
            if (_busy)
            {
                e.Cancel = true;
                SetStatus("Finish the transfer or use Cancel transfer before closing.");
            }
        };
        RefreshActions();
    }

    internal void ReportStatus(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(ReportStatus), message); }
            catch (InvalidOperationException) { }
            return;
        }
        SetStatus(message);
    }

    internal void ReportProgress(long done, long total)
    {
        if (IsDisposed) return;
        // Throttle UI updates during multi-gigabyte transfers. One callback
        // per 48-KiB chunk would otherwise flood the WinForms message queue.
        long now = Environment.TickCount64;
        if (done > 0 && done < total && now - Interlocked.Read(ref _lastProgressUiAt) < 120)
            return;
        Interlocked.Exchange(ref _lastProgressUiAt, now);
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(() => RenderProgress(done, total))); }
            catch (InvalidOperationException) { }
            return;
        }
        RenderProgress(done, total);
    }

    private void RenderProgress(long done, long total)
    {
        if (IsDisposed) return;
        done = Math.Clamp(done, 0L, Math.Max(0L, total));
        if (done == 0 || total != _progressTotal)
        {
            _progressStartedUtc = DateTime.UtcNow;
            _progressTotal = total;
        }
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = total <= 0 ? 0 : (int)Math.Clamp((double)done * 100 / total, 0, 100);
        double seconds = Math.Max(0.1, (DateTime.UtcNow - _progressStartedUtc).TotalSeconds);
        double rate = done / seconds;
        string eta = rate < 1 || total <= done ? "--" :
            TimeSpan.FromSeconds((total - done) / rate).ToString(@"hh\:mm\:ss");
        _metrics.Text = FormatSize(done) + " / " + FormatSize(total) +
            "  |  " + FormatSize((long)rate) + "/s  |  ETA " + eta +
            "  |  OK " + _succeeded + " / Failed " + _failed;
    }

    internal void SessionClosed()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(SessionClosed)); return; }
        _operation?.Cancel();
        _copiedRemoteFile = null;
        // The active operation owns _busy and its CancellationTokenSource.
        // Let its finally block clean up; never enable a second transfer early.
        SetStatus("TLS session disconnected. Remote file operations unavailable.");
    }

    private static Button MakeButton(string text, bool primary = false) => new() {
        Text = text, AutoSize = false, Width = 117, Height = 33, FlatStyle = FlatStyle.Flat,
        ForeColor = primary ? Color.White : Color.FromArgb(35, 52, 76),
        BackColor = primary ? Color.FromArgb(40, 103, 171) : Color.FromArgb(237, 242, 249),
        Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
        UseVisualStyleBackColor = false
    };

    private static Label MakeCaption() => new() {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true, ForeColor = Color.FromArgb(64, 77, 94)
    };

    private static ListView MakeList()
    {
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
            GridLines = false, MultiSelect = true, HideSelection = false,
            BackColor = Color.White, ForeColor = Color.FromArgb(38, 49, 67) };
        list.Columns.Add("Name", 270);
        list.Columns.Add("Type", 85);
        list.Columns.Add("Size", 95, HorizontalAlignment.Right);
        list.Columns.Add("Modified", 135);
        int sortedColumn = 0;
        bool descending = false;
        list.ColumnClick += (_, e) =>
        {
            descending = sortedColumn == e.Column && !descending;
            sortedColumn = e.Column;
            list.ListViewItemSorter = new EntryComparer(sortedColumn, descending);
            list.Sort();
        };
        list.Resize += (_, _) =>
        {
            int fixedWidth = list.Columns.Cast<ColumnHeader>().Skip(1).Sum(c => c.Width);
            list.Columns[0].Width = Math.Max(125, list.ClientSize.Width - fixedWidth - 23);
        };
        return list;
    }

    private Control BuildPane(string title, Label path, ListView list,
        params (string Text, Action Click)[] actions)
    {
        var frame = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3,
            ColumnCount = 1, BackColor = Color.White, Padding = new Padding(9) };
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.Controls.Add(new Label {
            Text = title, Font = new Font("Segoe UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(27, 55, 90), Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.Controls.Add(path, 0, 0);
        var commands = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false, Dock = DockStyle.Fill };
        foreach (var action in actions)
        {
            var button = MakeButton(action.Text);
            button.Width = action.Text.Length > 7 ? 77 : 62;
            button.Height = 29;
            button.Margin = new Padding(2);
            button.Click += (_, _) => action.Click();
            commands.Controls.Add(button);
            _navigation.Add(button);
        }
        bar.Controls.Add(commands, 1, 0);
        frame.Controls.Add(bar, 0, 1);
        frame.Controls.Add(list, 0, 2);
        return frame;
    }

    private sealed record LocalEntry(string FullPath, bool IsDirectory, long Size);

    private sealed class EntryComparer(int column, bool descending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            var a = (ListViewItem)x!;
            var b = (ListViewItem)y!;
            int value;
            if (column == 2)
            {
                long sizeA = a.Tag is LocalEntry la ? la.Size :
                    a.Tag is LabRemoteFileItem ra ? ra.Size : 0;
                long sizeB = b.Tag is LocalEntry lb ? lb.Size :
                    b.Tag is LabRemoteFileItem rb ? rb.Size : 0;
                value = sizeA.CompareTo(sizeB);
            }
            else
                value = string.Compare(a.SubItems[column].Text, b.SubItems[column].Text,
                    StringComparison.CurrentCultureIgnoreCase);
            if (value == 0) value = string.Compare(a.Text, b.Text,
                StringComparison.CurrentCultureIgnoreCase);
            return descending ? -value : value;
        }
    }

    private void RefreshActions()
    {
        bool editable = !_busy && _isController();
        foreach (Button b in _actions) b.Enabled = editable;
        _pasteRemoteButton.Enabled = editable && _copiedRemoteFile is not null;
        foreach (Button b in _navigation) b.Enabled = !_busy;
        _cancelButton.Enabled = _busy;
    }

    private void SetStatus(string text) => _status.Text = text;

    private void RecordOutcome(string direction, string path, bool success, string detail)
    {
        if (success) _succeeded++;
        else _failed++;
        var row = new ListViewItem(success ? "PASS" : "FAILED") {
            ForeColor = success ? Color.FromArgb(23, 106, 67) : Color.FromArgb(176, 48, 48)
        };
        row.SubItems.Add(direction);
        row.SubItems.Add(path);
        row.SubItems.Add(detail);
        _history.Items.Insert(0, row);
        _metrics.Text = "Successful operations: " + _succeeded +
            "   |   Failed / skipped operations: " + _failed;
    }

    private async Task TransferOneAsync(bool upload, string localPath, string remotePath,
        CancellationToken stop)
    {
        string direction = upload ? "Upload" : "Download";
        string display = upload ? localPath : remotePath;
        try
        {
            if (upload) await _upload(localPath, remotePath, stop);
            else await _download(remotePath, localPath, stop);
            RecordOutcome(direction, display, true, "Server approved; SHA-256 verified.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            RecordOutcome(direction, display, false, ex.Message);
            // Continue independent files, but stop immediately on disconnect.
            if (stop.IsCancellationRequested || !_isController()) throw;
        }
    }

    private void ChooseDrive()
    {
        using var menu = new ContextMenuStrip();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string path = drive.RootDirectory.FullName;
                menu.Items.Add(path + "  (" + drive.DriveType + ")", null, (_, _) =>
                {
                    _localFolder = path;
                    RefreshLocal();
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus("Unable to list local drives: " + ex.Message);
            return;
        }
        if (menu.Items.Count == 0) { SetStatus("No accessible local drives."); return; }
        menu.Show(_localList, new Point(10, 10));
    }

    private void ChooseLocalFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Local folder to browse",
            SelectedPath = Directory.Exists(_localFolder) ? _localFolder : "" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _localFolder = dialog.SelectedPath;
        RefreshLocal();
    }

    private void GoLocalUp()
    {
        var parent = Directory.GetParent(_localFolder);
        if (parent is null) return;
        _localFolder = parent.FullName;
        RefreshLocal();
    }

    // A context menu provides the same basic local-file tools as TightVNC.
    // It never accesses the remote Server or bypasses Server-side consent.
    private void InstallLocalContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("New local folder...", null, (_, _) => EditLocal("CreateFolder"));
        menu.Items.Add("Rename selected local entry...", null, (_, _) => EditLocal("Rename"));
        menu.Items.Add("Delete selected local entry...", null, (_, _) => EditLocal("Delete"));
        _localList.ContextMenuStrip = menu;
    }

    private void EditLocal(string operation)
    {
        if (_busy) { SetStatus("Complete the current transfer first."); return; }
        try
        {
            if (operation == "CreateFolder")
            {
                string? name = PromptName("New local folder", "");
                if (name is null) return;
                string folder = Path.Combine(_localFolder, name);
                if (File.Exists(folder) || Directory.Exists(folder))
                    throw new IOException("A local file or folder already uses that name.");
                Directory.CreateDirectory(folder);
                SetStatus("Created local folder: " + name);
            }
            else
            {
                if (_localList.SelectedItems.Count != 1 ||
                    _localList.SelectedItems[0].Tag is not LocalEntry selection)
                    throw new InvalidDataException("Select exactly one local file or folder.");
                if ((File.GetAttributes(selection.FullPath) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked local entries cannot be changed.");
                if (operation == "Rename")
                {
                    string original = Path.GetFileName(selection.FullPath);
                    string? name = PromptName("Rename local entry", original);
                    if (name is null || name == original) return;
                    string target = Path.Combine(_localFolder, name);
                    if (File.Exists(target) || Directory.Exists(target))
                        throw new IOException("A local file or folder already uses that name.");
                    if (selection.IsDirectory) Directory.Move(selection.FullPath, target);
                    else File.Move(selection.FullPath, target);
                    SetStatus("Renamed local entry: " + name);
                }
                else
                {
                    if (MessageBox.Show(this,
                        "Permanently delete this local " + (selection.IsDirectory ? "EMPTY folder" : "file") +
                        "? This cannot be undone.\n\n" + selection.FullPath,
                        "Delete local entry", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2)
                        != DialogResult.Yes) return;
                    if (selection.IsDirectory) Directory.Delete(selection.FullPath, recursive: false);
                    else File.Delete(selection.FullPath);
                    SetStatus("Deleted local entry: " + Path.GetFileName(selection.FullPath));
                }
            }
            RefreshLocal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException)
        { SetStatus("Local file action failed: " + ex.Message); }
    }

    private void RefreshLocal()
    {
        _localPath.Text = _localFolder;
        _localList.BeginUpdate();
        try
        {
            _localList.Items.Clear();
            var visiblePaths = Directory.EnumerateFileSystemEntries(_localFolder).Take(161).ToArray();
            if (visiblePaths.Length > 160)
                throw new InvalidDataException("Local folder contains more than 160 entries; showing a partial list is unsafe.");
            var entries = visiblePaths
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => (info.Attributes & FileAttributes.Directory) != 0)
                .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var info in entries)
            {
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                bool folder = (info.Attributes & FileAttributes.Directory) != 0;
                var row = new ListViewItem(info.Name) {
                    Tag = new LocalEntry(info.FullName, folder, folder ? 0 : info.Length)
                };
                row.SubItems.Add(folder ? "Folder" : "File");
                row.SubItems.Add(folder ? "" : FormatSize(info.Length));
                row.SubItems.Add(info.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                if (folder) row.ForeColor = Color.FromArgb(35, 100, 165);
                _localList.Items.Add(row);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetStatus("Unable to view local folder: " + ex.Message); }
        finally { _localList.EndUpdate(); }
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ?
            (bytes / 1024.0).ToString("N1") + " KB" :
            bytes < 1024L * 1024 * 1024 ?
            (bytes / (1024.0 * 1024)).ToString("N1") + " MB" :
            (bytes / (1024.0 * 1024 * 1024)).ToString("N2") + " GB";

    private async Task<LabRemoteFileReply> CommandAsync(string action, string path,
        string newName, CancellationToken stop)
    {
        if (!_isController()) throw new UnauthorizedAccessException("Controller access required.");
        var reply = await _remote(action, path, newName, stop);
        if (!reply.Success)
            throw new InvalidOperationException(reply.Error.Length > 0 ? reply.Error :
                "Server rejected the remote file operation.");
        _remoteFolder = reply.Path;
        DisplayRemote(reply);
        SetStatus(action == "List" ? "Remote folder loaded: " + reply.Items.Length + " entries." :
            "Server approved and completed: " + action + ".");
        return reply;
    }

    private async Task BrowseRemoteAsync(string relative, CancellationToken stop) =>
        await CommandAsync("List", relative, "", stop);

    private void DisplayRemote(LabRemoteFileReply reply)
    {
        _remotePathLabel.Text = "Received" + (reply.Path.Length > 0 ? "/" + reply.Path : "");
        _remoteList.BeginUpdate();
        try
        {
            _remoteList.Items.Clear();
            foreach (var entry in reply.Items)
            {
                var row = new ListViewItem(entry.Name) { Tag = entry };
                row.SubItems.Add(entry.IsDirectory ? "Folder" : "File");
                row.SubItems.Add(entry.IsDirectory ? "" : FormatSize(entry.Size));
                row.SubItems.Add(entry.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                if (entry.IsDirectory) row.ForeColor = Color.FromArgb(35, 100, 165);
                _remoteList.Items.Add(row);
            }
            // A directory exceeding 160 entries is rejected by the Server;
            // successful lists are complete, never silently truncated.
        }
        finally { _remoteList.EndUpdate(); }
    }

    private LabRemoteFileItem? SelectedRemote()
    {
        if (_remoteList.SelectedItems.Count == 1)
            return _remoteList.SelectedItems[0].Tag as LabRemoteFileItem;
        SetStatus("Select exactly one remote file or folder.");
        return null;
    }

    private static string Join(string parent, string name) =>
        parent.Length == 0 ? name : parent + "/" + name;

    private static string ParentRemote(string relative) =>
        relative.Length == 0 ? "" : relative.Contains('/') ?
            relative[..relative.LastIndexOf('/')] : "";

    private async Task ExecuteAsync(Func<CancellationToken, Task> work)
    {
        if (_busy) return;
        _busy = true;
        _operation = new CancellationTokenSource();
        _progress.Style = ProgressBarStyle.Marquee;
        RefreshActions();
        try
        {
            await work(_operation.Token);
        }
        catch (OperationCanceledException) { SetStatus("Operation cancelled."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or TimeoutException or
            System.Security.Authentication.AuthenticationException)
        { SetStatus("Operation failed: " + ex.Message); }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _busy = false;
            _progress.Style = ProgressBarStyle.Continuous;
            RefreshActions();
        }
    }

    private const int MaxBatchEntries = 128;
    private const long MaxBatchBytes = 8L * 1024 * 1024 * 1024;

    // Preflight everything before the first Server mutation; never claim a
    // complete folder transfer when links, depth, item or byte limits are hit.
    private static void CheckLocalTree(string path, int depth, CancellationToken stop,
        ref int entries, ref long bytes)
    {
        stop.ThrowIfCancellationRequested();
        if (++entries > MaxBatchEntries) throw new InvalidDataException("Batch exceeds 128 files/folders.");
        var attrs = File.GetAttributes(path);
        if ((attrs & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked local folders or files cannot be transferred.");
        LabFileTransfer.SafeFileName(Path.GetFileName(path));
        if ((attrs & FileAttributes.Directory) != 0)
        {
            if (depth >= 8) throw new InvalidDataException("Folder depth exceeds 8.");
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
                CheckLocalTree(child, depth + 1, stop, ref entries, ref bytes);
        }
        else
        {
            long size = new FileInfo(path).Length;
            if (size > LabFileTransfer.MaxFileBytes)
                throw new InvalidDataException("A file exceeds the 4 GiB per-file limit.");
            bytes = checked(bytes + size);
            if (bytes > MaxBatchBytes)
                throw new InvalidDataException("Batch exceeds 8 GiB.");
        }
    }

    private async Task<(int Entries, long Bytes)> CheckRemoteTreeAsync(
        string remotePath, int depth, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        if (depth > 8) throw new InvalidDataException("Remote folder depth exceeds 8.");
        var listing = await _remote("List", remotePath, "", stop);
        if (!listing.Success) throw new InvalidOperationException(listing.Error);
        int count = 1; long total = 0;
        foreach (var item in listing.Items)
        {
            if (item.IsDirectory)
            {
                var nested = await CheckRemoteTreeAsync(Join(remotePath, item.Name), depth + 1, stop);
                count += nested.Entries;
                total += nested.Bytes;
            }
            else
            {
                count++; total += item.Size;
                if (item.Size > LabFileTransfer.MaxFileBytes)
                    throw new InvalidDataException("A remote file exceeds 4 GiB.");
            }
            if (count > MaxBatchEntries || total > MaxBatchBytes)
                throw new InvalidDataException("Batch exceeds 128 entries or 8 GiB.");
        }
        return (count, total);
    }

    private async Task UploadSelectedAsync(CancellationToken stop)
    {
        var selected = _localList.SelectedItems.Cast<ListViewItem>()
            .Select(row => row.Tag as LocalEntry).Where(entry => entry is not null).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select local files or folders first.");
        int planned = 0; long totalBytes = 0;
        foreach (var selectedEntry in selected)
            CheckLocalTree(selectedEntry!.FullPath, 0, stop, ref planned, ref totalBytes);
        int sentEntries = 0;
        int beforeSuccess = _succeeded, beforeFailed = _failed;
        string startingRemoteFolder = _remoteFolder;
        SetStatus("Uploading up to " + planned + " entries (" + FormatSize(totalBytes) +"). Server approval is required per change.");
        foreach (var item in selected)
        {
            stop.ThrowIfCancellationRequested();
            if (item!.IsDirectory)
                await UploadFolderAsync(item.FullPath, startingRemoteFolder, 0, stop, () => ++sentEntries);
            else
            {
                if (++sentEntries > MaxBatchEntries) throw new InvalidDataException("Batch limit: 128 entries.");
                await TransferOneAsync(true, item.FullPath, startingRemoteFolder, stop);
            }
        }
        await BrowseRemoteAsync(startingRemoteFolder, stop);
        int succeeded = _succeeded - beforeSuccess, failed = _failed - beforeFailed;
        SetStatus("Upload finished: " + succeeded + " succeeded, " + failed +
            " failed/skipped (" + sentEntries + " entries visited). " +
            (failed == 0 ? "All confirmed." : "Review the transfer history below."));
    }

    private async Task UploadFolderAsync(string source, string remoteParent, int depth,
        CancellationToken stop, Func<int> count)
    {
        if (depth >= 8) throw new InvalidDataException("Folder depth limited to 8.");
        if (count() > MaxBatchEntries) throw new InvalidDataException("Batch exceeds 128 files/folders.");
        var info = new DirectoryInfo(source);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked or missing local folder.");
        string name = LabFileTransfer.SafeFileName(info.Name);
        try
        {
            await CommandAsync("CreateFolder", remoteParent, name, stop);
            RecordOutcome("Upload folder", source, true, "Created on Server.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException)
        {
            RecordOutcome("Upload folder", source, false,
                ex.Message + " The entire subtree was skipped.");
            if (stop.IsCancellationRequested || !_isController()) throw;
            return;
        }
        string remoteChild = Join(remoteParent, name);
        foreach (var file in info.EnumerateFileSystemInfos())
        {
            stop.ThrowIfCancellationRequested();
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked local file or folder cannot be transferred.");
            if (file is DirectoryInfo)
                await UploadFolderAsync(file.FullName, remoteChild, depth + 1, stop, count);
            else
            {
                if (count() > MaxBatchEntries) throw new InvalidDataException("Batch exceeds 128 files/folders.");
                await TransferOneAsync(true, file.FullName, remoteChild, stop);
            }
        }
    }

    private async Task DownloadSelectedAsync(CancellationToken stop)
    {
        var selected = _remoteList.SelectedItems.Cast<ListViewItem>()
            .Select(row => row.Tag as LabRemoteFileItem).Where(item => item is not null).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Select remote files or folders first.");
        int plannedEntries = 0; long plannedBytes = 0;
        foreach (var candidate in selected)
        {
            stop.ThrowIfCancellationRequested();
            if (candidate!.IsDirectory)
            {
                var plan = await CheckRemoteTreeAsync(Join(_remoteFolder, candidate.Name), 1, stop);
                plannedEntries += plan.Entries; plannedBytes += plan.Bytes;
            }
            else
            {
                plannedEntries++; plannedBytes += candidate.Size;
                if (candidate.Size > LabFileTransfer.MaxFileBytes)
                    throw new InvalidDataException("Remote file exceeds 4 GiB.");
            }
            if (plannedEntries > MaxBatchEntries || plannedBytes > MaxBatchBytes)
                throw new InvalidDataException("Batch exceeds 128 entries or 512 MiB.");
        }
        SetStatus("Downloading up to " + plannedEntries + " entries (" + FormatSize(plannedBytes) +").");
        int beforeSuccess = _succeeded, beforeFailed = _failed;
        int count = 0;
        foreach (var item in selected)
        {
            stop.ThrowIfCancellationRequested();
            var remotePath = Join(_remoteFolder, item!.Name);
            if (item.IsDirectory)
            {
                string localPath = Path.Combine(_localFolder, item.Name);
                if (File.Exists(localPath) || (Directory.Exists(localPath) &&
                    (File.GetAttributes(localPath) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("Local destination is an existing link or file.");
                Directory.CreateDirectory(localPath);
                await DownloadFolderAsync(remotePath, localPath, 1, stop, () => ++count);
            }
            else
            {
                if (++count > MaxBatchEntries) throw new InvalidDataException("Batch exceeds 128 files/folders.");
                await TransferOneAsync(false, _localFolder, remotePath, stop);
            }
        }
        RefreshLocal();
        int succeeded = _succeeded - beforeSuccess, failed = _failed - beforeFailed;
        SetStatus("Download finished: " + succeeded + " succeeded, " + failed +
            " failed/skipped (" + count + " entries visited). " +
            (failed == 0 ? "All confirmed." : "Review the transfer history below."));
    }

    private async Task DownloadFolderAsync(string remotePath, string localPath, int depth,
        CancellationToken stop, Func<int> count)
    {
        if (depth > 8) throw new InvalidDataException("Folder depth limited to 8.");
        if (count() > MaxBatchEntries) throw new InvalidDataException("Batch exceeds 128 files/folders.");
        RecordOutcome("Download folder", remotePath, true, "Local destination folder created.");
        var reply = await _remote("List", remotePath, "", stop);
        if (!reply.Success) throw new InvalidOperationException(reply.Error);
        foreach (var entry in reply.Items)
        {
            stop.ThrowIfCancellationRequested();
            string remoteChild = Join(remotePath, entry.Name);
            if (entry.IsDirectory)
            {
                string localChild = Path.Combine(localPath, entry.Name);
                if (File.Exists(localChild) || (Directory.Exists(localChild) &&
                    (File.GetAttributes(localChild) & FileAttributes.ReparsePoint) != 0))
                    throw new InvalidDataException("Local destination contains a link or file.");
                Directory.CreateDirectory(localChild);
                await DownloadFolderAsync(remoteChild, localChild, depth + 1, stop, count);
            }
            else
            {
                if (count() > MaxBatchEntries) throw new InvalidDataException("Batch exceeds 128 files/folders.");
                await TransferOneAsync(false, localPath, remoteChild, stop);
            }
        }
    }

    private string? PromptName(string title, string original)
    {
        using var form = new Form { Text = title, Width = 440, Height = 185,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false };
        var field = new TextBox { Dock = DockStyle.Top, Text = original, Margin = new Padding(14) };
        var okay = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 43,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 3, 10, 3) };
        buttons.Controls.Add(okay);
        buttons.Controls.Add(cancel);
        form.Controls.Add(field);
        form.Controls.Add(buttons);
        form.AcceptButton = okay;
        form.CancelButton = cancel;
        if (form.ShowDialog(this) != DialogResult.OK) return null;
        return LabFileTransfer.SafeFileName(field.Text);
    }

    private void CancelTransfer()
    {
        if (!_busy) return;
        if (MessageBox.Show(this,
            "To safely discard an in-progress partial file, the TLS session must close.\n" +
            "Cancel this operation and disconnect the Viewer?",
            "Cancel transfer", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _operation?.Cancel();
        _disconnect(); // Closed session disposes server-side partial files.
        SetStatus("Transfer canceled; remote session disconnected for safe cleanup.");
    }
}
