// SPDX-License-Identifier: GPL-2.0-or-later
// Interactive mutually authenticated Windows desktop viewer (LAB ONLY).
using System.Drawing;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabViewerProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var launch = LabViewerLaunchOptions.Parse(args);
            var store = new LabViewerProfileStore();
            // Resolve before opening: an invalid ID must never silently
            // connect the previously selected, different server.
            launch.Resolve(store.Load());
            Application.Run(new LabViewerForm(store, launch));
        }
        catch (Exception ex)
        {
#if TJRA_STANDALONE_PREVIEW
            MessageBox.Show(ex.Message, "TJ.RemoteAccess Viewer Preview",
#else
            MessageBox.Show(ex.Message, "TJ.RemoteAccess Lab Viewer",
#endif
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class LabViewerForm : Form
{
    private readonly LabViewerProfileStore _profileStore;
    private readonly ComboBox _profilePicker = new() {
        Width = 275, DropDownStyle = ComboBoxStyle.DropDownList
    };
    private readonly Button _addProfile = new() { Text = "Add server", AutoSize = true };
    private readonly Button _addPassword = new() { Text = "Add password server", AutoSize = true };
#if TJRA_STANDALONE_PREVIEW
    private readonly Button _quickConnect = new() {
        Text = "Quick connect (IP + password)", AutoSize = true
    };
#endif
    private readonly Button _editProfile = new() { Text = "Edit", AutoSize = true };
    private readonly Button _removeProfile = new() { Text = "Remove", AutoSize = true };
    private readonly Button _detailsProfile = new() { Text = "Identity", AutoSize = true };
    private readonly Button _refreshProfile = new() { Text = "Refresh", AutoSize = true };
    private readonly TextBox _host = new() { Width = 210 };
    private readonly NumericUpDown _port = new() {
        Minimum = 1024, Maximum = 65535, Width = 80
    };
    private readonly Button _connect = new() { Text = "Connect", AutoSize = true };
    private readonly Button _disconnect = new() { Text = "Disconnect", AutoSize = true, Enabled = false };
    private readonly ComboBox _displayMode = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Panel _canvasHost = new() { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.FromArgb(25, 25, 25) };
    private readonly Button _pauseDisplay = new() { Text = "Pause display", AutoSize = true };
    private readonly Button _snapshot = new() { Text = "Save snapshot", AutoSize = true };
    private readonly Button _sendTaskManager = new() { Text = "Send Ctrl+Shift+Esc", AutoSize = true };
    private readonly Button _refreshScreen = new() { Text = "Refresh screen", AutoSize = true };
    private readonly Button _connectionInfo = new() { Text = "Connection info", AutoSize = true };
    private readonly Button _sendFile = new() { Text = "Send file...", AutoSize = true };
    private readonly Button _receiveFile = new() { Text = "Receive file...", AutoSize = true };
    private readonly Button _transferFiles = new() { Text = "Transfer Files...", AutoSize = true };
    private readonly LabRemoteFileClient _remoteFileClient;
    private readonly LabViewerTransfers _transfers;
    private readonly Button _connectionOptionsButton = new() { Text = "Connection options...", AutoSize = true };
    private readonly Button _reconnect = new() { Text = "Reconnect", AutoSize = true };
    private readonly Button _fullScreenButton = new() { Text = "Full screen", AutoSize = true };
    private LabViewerConnectionOptions _options;
    private readonly System.Windows.Forms.Timer _reconnectTimer = new() { Interval = 4000 };
    private int _reconnectAttempts;
    private LabViewerProfile? _activeProfile;
    private DateTimeOffset _connectedAt;
    private ToolStripMenuItem? _ctrlToggle;
    private ToolStripMenuItem? _altToggle;
    private readonly object _clipboardGuard = new();
    private Guid? _clipboardWriteId;
    private Guid? _clipboardReadId;
    private bool _manualDisconnect;
    private bool _fullScreen;
    private volatile bool _displayPaused;
    private LabTransferFilesForm? _transferFilesWindow;
    private string? _managerDownloadFolder;
    private string? _managerExpectedFile;
    private readonly Label _status = new() {
        Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(8)
    };
    private readonly RemoteCanvas _picture = new() {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 30, 30),
        SizeMode = PictureBoxSizeMode.Zoom
    };
    private readonly SemaphoreSlim _outgoingMutex = new(1);
    private readonly HashSet<ushort> _heldKeys = new();
    private CancellationTokenSource? _stop;
    private TcpClient? _tcp;
    private SslStream? _tls;
    private LabRole _role;
    private int _desktopWidth = 1, _desktopHeight = 1;
    private int _frameCount;
    // A busy UI must not accumulate an unbounded backlog of decoded Bitmaps.
    private int _frameUpdatePending;
    private int _lastX, _lastY;
    private long _lastMoveAt;
    private bool _connected;
    private bool _loadingProfiles;
    private bool _connecting;
    private readonly System.Windows.Forms.Timer _keepAlive = new() { Interval = 5000 };

    internal LabViewerForm(LabViewerProfileStore profileStore, LabViewerLaunchOptions? launch = null)
    {
        _profileStore = profileStore;
        _options = LabViewerConnectionOptions.Load();
        _remoteFileClient = new LabRemoteFileClient(SendTransferAsync);
        _transfers = new LabViewerTransfers(SendTransferAsync, SelectDownloadDestinationAsync,
            message => {
                ShowStatus(message);
                _transferFilesWindow?.ReportStatus(message);
            }, (done, total) => _transferFilesWindow?.ReportProgress(done, total));
#if TJRA_STANDALONE_PREVIEW
        Text = "TJ.RemoteAccess Viewer - PREVIEW";
#else
        Text = "TJ.RemoteAccess Lab Viewer - TEST ONLY";
#endif
        MinimumSize = new Size(720, 440);
        Width = 1210;
        Height = 790;
        KeyPreview = true;
        _port.Value = LabCertificates.DefaultPort;
        var toolbar = new FlowLayoutPanel {
            Dock = DockStyle.Top, Height = 98, Padding = new Padding(5),
            AutoScroll = true, WrapContents = true
        };
        toolbar.Controls.Add(new Label { Text = "Saved server", AutoSize = true,
            Padding = new Padding(0, 6, 0, 0) });
        toolbar.Controls.Add(_profilePicker);
        toolbar.Controls.Add(_addProfile);
        toolbar.Controls.Add(_addPassword);
#if TJRA_STANDALONE_PREVIEW
        toolbar.Controls.Add(_quickConnect);
        _quickConnect.Click += async (_, _) => {
            var request = LabViewerQuickPasswordDialog.Prompt(this, _host.Text);
            if (request is not null) await ConnectAsync(request);
        };
#endif
        toolbar.Controls.Add(_editProfile);
        toolbar.Controls.Add(_removeProfile);
        toolbar.Controls.Add(_detailsProfile);
        toolbar.Controls.Add(_refreshProfile);
        toolbar.Controls.Add(new Label {
            Text = "Host", TextAlign = ContentAlignment.MiddleLeft, AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        });
        toolbar.Controls.Add(_host);
        toolbar.Controls.Add(_port);
        toolbar.Controls.Add(_connect);
        toolbar.Controls.Add(_disconnect);
        toolbar.Controls.Add(_reconnect);
        toolbar.Controls.Add(_connectionOptionsButton);
        toolbar.Controls.Add(_connectionInfo);
        toolbar.Controls.Add(_fullScreenButton);
        _displayMode.Items.AddRange(new object[] { "Fit", "10%", "15%", "25%", "50%", "75%", "90%", "100%", "125%", "150%", "200%", "400%" });
        _displayMode.SelectedIndex = 0;
        toolbar.Controls.Add(_displayMode);
        toolbar.Controls.Add(_pauseDisplay);
        toolbar.Controls.Add(_refreshScreen);
        toolbar.Controls.Add(_snapshot);
        toolbar.Controls.Add(_sendTaskManager);
        toolbar.Controls.Add(_transferFiles);
        _canvasHost.Controls.Add(_picture);
        Controls.Add(_canvasHost);
        Controls.Add(_status);
        Controls.Add(toolbar);
        InstallViewerMenu(toolbar);
        _connect.Click += async (_, _) => await ConnectAsync();
        _profilePicker.SelectedIndexChanged += (_, _) => ApplySelectedProfile();
        _addProfile.Click += (_, _) => AddServerProfile();
        _addPassword.Click += (_, _) => AddPasswordServerProfile();
        _editProfile.Click += (_, _) => EditServerProfile();
        _removeProfile.Click += (_, _) => RemoveServerProfile();
        _detailsProfile.Click += (_, _) => {
            if (SelectedProfile is { } profile)
                LabViewerProfileDialogs.ShowIdentity(this, profile);
        };
        _refreshProfile.Click += (_, _) => ReloadProfiles();
        _disconnect.Click += (_, _) => { _manualDisconnect = true; Disconnect(); };
        _reconnect.Click += async (_, _) => {
            _manualDisconnect = true;
            Disconnect();
            _manualDisconnect = false;
            await ConnectAsync();
        };
        _connectionOptionsButton.Click += async (_, _) => await ConfigureConnectionOptionsAsync();
        _connectionInfo.Click += (_, _) => ShowConnectionInfo();
        _refreshScreen.Click += async (_, _) => await RequestScreenRefreshAsync();
        _fullScreenButton.Click += (_, _) => SetFullScreen(!_fullScreen);
        _displayMode.SelectedIndexChanged += (_, _) => ApplyDisplayScale();
        _pauseDisplay.Click += async (_, _) => await ToggleDisplayPauseAsync();
        _snapshot.Click += (_, _) => SaveSnapshot(); // Save a local copy of the current frame.
        _sendTaskManager.Click += async (_, _) => await SendTaskManagerShortcutAsync();
        _sendFile.Click += async (_, _) => await SendSelectedFileAsync();
        _receiveFile.Click += async (_, _) => await RequestFileAsync();
        _transferFiles.Click += (_, _) => OpenTransferFiles();
        _picture.MouseDown += async (_, e) => {
            _picture.Focus();
            await SendPointerAsync(e.Location, force: true);
        };
        _picture.MouseMove += async (_, e) => await SendPointerAsync(e.Location);
        _picture.MouseUp += async (_, e) => await SendPointerAsync(e.Location, force: true);
        _picture.MouseWheel += async (_, e) => {
            if (CanControl && e.Delta != 0)
                await SendAsync(LabMessage.Wheel, LabWire.Wheel((short)Math.Clamp(e.Delta, -1200, 1200)));
        };
        KeyDown += async (_, e) => {
            if (_fullScreen && e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true; SetFullScreen(false); return;
            }
            await OnRemoteKey(e, pressed: true);
        };
        KeyUp += async (_, e) => await OnRemoteKey(e, pressed: false);
        Deactivate += async (_, _) => await ReleaseHeldKeysAsync();
        _picture.LostFocus += async (_, _) => await ReleaseHeldKeysAsync();
        FormClosing += (_, _) => { _manualDisconnect = true; Disconnect(); };
        _keepAlive.Tick += async (_, _) => {
            if (_connected)
                await SendAsync(LabMessage.KeepAlive, ReadOnlyMemory<byte>.Empty);
        };
        _keepAlive.Start();
        _reconnectTimer.Tick += async (_, _) => {
            _reconnectTimer.Stop();
            if (!_connected && !_connecting && SelectedProfile is not null)
                await ConnectAsync();
        };
        ReloadProfiles(launch?.ProfileId);
        if (launch is { Connect: true })
        {
            Shown += async (_, _) => await ConnectAsync();
        }
#if TJRA_STANDALONE_PREVIEW
        _status.Text = SelectedProfile is null
            ? "Quick connect with IP + password, or add a certificate server."
            : "Ready. Select a server or use Quick connect.";
#else
        _status.Text = SelectedProfile is null ? "No server paired. Click Add server." : "Ready. Select a saved server to connect.";
#endif
    }

    // TightVNC-style commands remain reachable even with the crowded toolbar hidden.
    private void InstallViewerMenu(FlowLayoutPanel toolbar)
    {
        var menu = new MenuStrip { Dock = DockStyle.Top };
        var session = new ToolStripMenuItem("&Connection");
        session.DropDownItems.Add("Connection &info...", null, (_, _) => ShowConnectionInfo());
        session.DropDownItems.Add("&Refresh screen", null, async (_, _) => await RequestScreenRefreshAsync());
        session.DropDownItems.Add("&New connection...", null, (_, _) => {
            _manualDisconnect = true; Disconnect(); _profilePicker.Focus();
        });
        var view = new ToolStripMenuItem("&View");
        var toolbarItem = new ToolStripMenuItem("Show &toolbar") {
            Checked = true, CheckOnClick = true, ShortcutKeys = Keys.F8
        };
        toolbarItem.CheckedChanged += (_, _) => toolbar.Visible = toolbarItem.Checked;
        view.DropDownItems.Add(toolbarItem);
        view.DropDownItems.Add("&Fit to window", null, (_, _) => _displayMode.SelectedIndex = 0);
        view.DropDownItems.Add("&Actual size (100%)", null, (_, _) => _displayMode.SelectedIndex = 7);
        view.DropDownItems.Add("&Full screen", null, (_, _) => SetFullScreen(!_fullScreen));
        view.DropDownItems.Add("&Pause / Resume", null, async (_, _) => await ToggleDisplayPauseAsync());
        var keyMenu = new ToolStripMenuItem("Send &keys");
        keyMenu.DropDownItems.Add("Ctrl+Esc (Start)", null, async (_, _) =>
            await SendShortcutAsync((ushort)Keys.ControlKey, (ushort)Keys.Escape));
        keyMenu.DropDownItems.Add("Alt+Tab", null, async (_, _) =>
            await SendShortcutAsync((ushort)Keys.Menu, (ushort)Keys.Tab));
        keyMenu.DropDownItems.Add("Ctrl+Shift+Esc (Task Manager)", null, async (_, _) =>
            await SendTaskManagerShortcutAsync());
        _ctrlToggle = new ToolStripMenuItem("Hold Ctrl");
        _altToggle = new ToolStripMenuItem("Hold Alt");
        _ctrlToggle.Click += async (_, _) => await ToggleModifierAsync((ushort)Keys.ControlKey);
        _altToggle.Click += async (_, _) => await ToggleModifierAsync((ushort)Keys.Menu);
        keyMenu.DropDownItems.Add(_ctrlToggle);
        keyMenu.DropDownItems.Add(_altToggle);
        var clipboardMenu = new ToolStripMenuItem("&Clipboard");
        clipboardMenu.DropDownItems.Add("Send local &text to Server...", null,
            async (_, _) => await SendLocalClipboardAsync());
        clipboardMenu.DropDownItems.Add("Request Server text &clipboard...", null,
            async (_, _) => await RequestRemoteClipboardAsync());
        var filesMenu = new ToolStripMenuItem("&Files");
        filesMenu.DropDownItems.Add("Transfer Files...", null, (_, _) => OpenTransferFiles());
        filesMenu.DropDownItems.Add("Quick send one file...", null,
            async (_, _) => await SendSelectedFileAsync());
        filesMenu.DropDownItems.Add("Quick receive Server-selected file...", null,
            async (_, _) => await RequestFileAsync());
        menu.Items.Add(session);
        menu.Items.Add(view);
        menu.Items.Add(keyMenu);
        menu.Items.Add(clipboardMenu);
        menu.Items.Add(filesMenu);
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    private void OpenTransferFiles()
    {
        if (!CanControl)
        { ShowStatus("Connect as Controller to open Transfer Files."); return; }
        if (_transferFilesWindow is { IsDisposed: false } existing)
        { existing.BringToFront(); existing.Activate(); return; }
        var window = new LabTransferFilesForm(
            (action, relative, name, stop) =>
                _remoteFileClient.ExecuteAsync(action, relative, name, stop),
            async (localFile, remoteFolder, stop) =>
            {
                if (!CanControl || _stop?.IsCancellationRequested != false)
                    throw new IOException("Remote session no longer permits file transfer.");
                if (!await _transfers.UploadAsync(localFile, stop, remoteFolder))
                    throw new InvalidOperationException("Server did not approve or verify the upload.");
            },
            async (remoteFile, localFolder, stop) =>
            {
                if (!CanControl || _stop?.IsCancellationRequested != false)
                    throw new IOException("Remote session no longer permits file transfer.");
                _managerDownloadFolder = localFolder;
                _managerExpectedFile = remoteFile.Split('/')[^1];
                try
                {
                    if (!await _transfers.DownloadAsync(stop, remoteFile))
                        throw new InvalidOperationException("Server did not approve or verify the download.");
                }
                finally
                { _managerDownloadFolder = null; _managerExpectedFile = null; }
            },
            () => CanControl,
            () => { _manualDisconnect = true; Disconnect(); });
        _transferFilesWindow = window;
        window.FormClosed += (_, _) => {
            if (ReferenceEquals(_transferFilesWindow, window)) _transferFilesWindow = null;
        };
        window.Show(this);
    }

    private bool CanControl => _connected && !_displayPaused && !_options.ViewOnly && _role == LabRole.Controller;

    private async Task RequestScreenRefreshAsync()
    {
        if (!_connected || _displayPaused)
        {
            ShowStatus(_displayPaused ? "Resume display before refreshing." : "Connect before refreshing remote screen.");
            return;
        }
        await SendAsync(LabMessage.RefreshFrame, ReadOnlyMemory<byte>.Empty);
        ShowStatus("Requested full remote screen refresh over authenticated TLS.");
    }

    private async Task SendLocalClipboardAsync()
    {
        if (!CanControl || _stop is null)
        { ShowStatus("Controller permission required for clipboard transfer."); return; }
        Guid id = Guid.NewGuid();
        lock (_clipboardGuard)
        {
            if (_clipboardWriteId.HasValue || _clipboardReadId.HasValue)
            { ShowStatus("A clipboard request is already in progress."); return; }
            _clipboardWriteId = id;
        }
        try
        {
            if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
                throw new InvalidDataException("Local clipboard has no text.");
            string text = Clipboard.GetText(TextDataFormat.UnicodeText);
            byte[] payload = LabClipboardWire.Text(id, text);
            await SendTransferAsync(LabMessage.ClipboardSet, payload);
            ShowStatus("Waiting for Server user's clipboard write approval...");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or OperationCanceledException or
            System.Runtime.InteropServices.ExternalException)
        {
            lock (_clipboardGuard) if (_clipboardWriteId == id) _clipboardWriteId = null;
            ShowStatus("Unable to send clipboard text: " + ex.Message);
        }
    }

    private async Task RequestRemoteClipboardAsync()
    {
        if (!CanControl) { ShowStatus("Controller permission required for clipboard transfer."); return; }
        Guid id = Guid.NewGuid();
        lock (_clipboardGuard)
        {
            if (_clipboardWriteId.HasValue || _clipboardReadId.HasValue)
            { ShowStatus("A clipboard request is already in progress."); return; }
            _clipboardReadId = id;
        }
        try
        {
            await SendTransferAsync(LabMessage.ClipboardGet, LabClipboardWire.Request(id));
            ShowStatus("Waiting for Server user's permission to share text clipboard...");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            InvalidOperationException or OperationCanceledException)
        {
            lock (_clipboardGuard) if (_clipboardReadId == id) _clipboardReadId = null;
            ShowStatus("Unable to request remote clipboard: " + ex.Message);
        }
    }

    private async Task HandleClipboardResponseAsync(LabPacket packet, CancellationToken token)
    {
        if (packet.Type == LabMessage.ClipboardDecision)
        {
            var decision = LabClipboardWire.ParseDecision(packet.Payload);
            bool isWrite;
            lock (_clipboardGuard)
            {
                isWrite = _clipboardWriteId == decision.Id;
                if (isWrite) _clipboardWriteId = null;
                else if (_clipboardReadId == decision.Id && !decision.Approved) _clipboardReadId = null;
                else throw new InvalidDataException("Unsolicited clipboard decision.");
            }
            ShowStatus(isWrite ? (decision.Approved ? "Server clipboard text updated by approval." :
                "Server denied clipboard write.") : "Server denied clipboard sharing.");
            return;
        }
        if (packet.Type != LabMessage.ClipboardText)
            throw new InvalidDataException("Unexpected clipboard response.");
        var response = LabClipboardWire.ParseText(packet.Payload);
        lock (_clipboardGuard)
            if (_clipboardReadId != response.Id)
                throw new InvalidDataException("Unrequested remote clipboard text.");
        bool applied = false;
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(() => {
                try
                {
                    if (!CanControl || IsDisposed) { signal.TrySetResult(false); return; }
                    bool accepted = MessageBox.Show(this,
                        "The Server user approved sharing text (" + response.Text.Length +
                        " characters). Replace THIS PC's clipboard? It may contain secrets.",
                        "TJ.RemoteAccess - Receive clipboard text", MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    if (accepted && CanControl) Clipboard.SetText(response.Text, TextDataFormat.UnicodeText);
                    signal.TrySetResult(accepted && CanControl);
                }
                catch (Exception ex) when (ex is InvalidOperationException or
                    System.Runtime.InteropServices.ExternalException)
                { signal.TrySetResult(false); }
            }));
            applied = await signal.Task.WaitAsync(TimeSpan.FromMinutes(2), token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
            TimeoutException or OperationCanceledException)
        { applied = false; }
        finally
        {
            lock (_clipboardGuard) if (_clipboardReadId == response.Id) _clipboardReadId = null;
        }
        ShowStatus(applied ? "Remote clipboard text copied after local approval." :
            "Remote clipboard was not pasted.");
    }

    private void ShowConnectionInfo()
    {
        var profile = _activeProfile;
        if (!_connected || _tls is null || profile is null)
        {
            MessageBox.Show(this, "Not connected.", "Connection information");
            return;
        }
        string details = string.Join(Environment.NewLine,
            "Endpoint: " + profile.Host + ":" + profile.Port,
            "Profile: " + profile.Name,
            "Access: " + profile.AuthMode + " / " + _role,
            "TLS: " + _tls.SslProtocol,
            "Server SHA-256 pin: " + profile.ServerSha256,
            "Desktop: " + _desktopWidth + " x " + _desktopHeight,
            "Frames rendered: " + _frameCount,
            "Session started (UTC): " + _connectedAt.ToString("u"),
            "Preferred JPEG quality: " + _options.JpegQuality,
            "Preferred FPS: " + _options.FramesPerSecond,
            "Secure desktop / UAC: Not supported");
        MessageBox.Show(this, details, "TJ.RemoteAccess - Connection information",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task SendShortcutAsync(params ushort[] shortcut)
    {
        if (!CanControl) { ShowStatus("Controller permission required to send keys."); return; }
        ushort[] keys = shortcut.Where(key => !_heldKeys.Contains(key)).ToArray();
        try
        {
            foreach (ushort key in keys)
                await SendAsync(LabMessage.Key, LabWire.Key(key, true));
        }
        finally
        {
            for (int i = keys.Length - 1; i >= 0; i--)
                await SendAsync(LabMessage.Key, LabWire.Key(keys[i], false));
        }
    }

    private async Task ToggleModifierAsync(ushort key)
    {
        if (!CanControl) { ShowStatus("Controller permission required to hold keys."); return; }
        bool pressed = _heldKeys.Add(key);
        if (!pressed) _heldKeys.Remove(key);
        await SendAsync(LabMessage.Key, LabWire.Key(key, pressed));
        UpdateModifierMenus();
    }

    private void UpdateModifierMenus()
    {
        if (_ctrlToggle is not null)
        {
            _ctrlToggle.Checked = _heldKeys.Contains((ushort)Keys.ControlKey);
            _ctrlToggle.Enabled = CanControl;
        }
        if (_altToggle is not null)
        {
            _altToggle.Checked = _heldKeys.Contains((ushort)Keys.Menu);
            _altToggle.Enabled = CanControl;
        }
    }

    private async Task ConfigureConnectionOptionsAsync()
    {
        var next = LabViewerConnectionOptionsDialog.Prompt(this, _options);
        if (next is null) return;
        var previous = _options;
        try { next.Save(); _options = next; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or OverflowException)
        { ShowStatus("Unable to save Connection Options: " + ex.Message); return; }
        if (_connected && next.FullScreen != previous.FullScreen)
            SetFullScreen(next.FullScreen);
        if (_connected && _role == LabRole.Controller && !next.ViewOnly &&
            (next.JpegQuality != previous.JpegQuality || next.FramesPerSecond != previous.FramesPerSecond))
            await SendAsync(LabMessage.CaptureOptions,
                LabWire.CaptureSettings((byte)next.JpegQuality, (byte)next.FramesPerSecond));
        UpdateProfileControls();
        ShowStatus(_connected && next.ViewOnly != previous.ViewOnly
            ? "View-only preference changed. Reconnect to negotiate the Server role."
            : "Connection Options saved (no passwords stored).");
    }

    private void SetFullScreen(bool enabled)
    {
        if (_fullScreen == enabled) return;
        _fullScreen = enabled;
        if (enabled)
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = FormWindowState.Normal;
        }
        _fullScreenButton.Text = enabled ? "Exit full screen (Esc)" : "Full screen";
    }

    private void ApplyDisplayScale()
    {
        if (_displayMode.SelectedIndex <= 0)
        {
            _canvasHost.AutoScroll = false;
            _picture.Dock = DockStyle.Fill;
            _picture.SizeMode = PictureBoxSizeMode.Zoom;
            return;
        }
        string text = (string)_displayMode.SelectedItem!;
        double scale = int.Parse(text.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture) / 100.0;
        _picture.Dock = DockStyle.None;
        _picture.Location = Point.Empty;
        _picture.SizeMode = PictureBoxSizeMode.StretchImage;
        _canvasHost.AutoScroll = true;
        if (_picture.Image is { } image)
            _picture.Size = new Size(
                Math.Max(1, (int)Math.Round(image.Width * scale)),
                Math.Max(1, (int)Math.Round(image.Height * scale)));
    }

    private async Task ToggleDisplayPauseAsync()
    {
        if (!_displayPaused) await ReleaseHeldKeysAsync();
        _displayPaused = !_displayPaused;
        _pauseDisplay.Text = _displayPaused ? "Resume display" : "Pause display";
        UpdateProfileControls();
        _status.Text = _displayPaused ? "Display paused locally; session stays active." : "Display resumed.";
    }

    private async Task SendSelectedFileAsync()
    {
        if (!CanControl || _stop is null) return;
        using var pick = new OpenFileDialog {
            Title = "Select files (up to 32; each <=4 GiB; Server approves each)",
            Filter = "All files (*.*)|*.*", CheckFileExists = true, Multiselect = true
        };
        if (pick.ShowDialog(this) != DialogResult.OK) return;
        if (pick.FileNames.Length > 32) { ShowStatus("Select at most 32 files per batch."); return; }
        try
        {
            foreach (string file in pick.FileNames)
            {
                if (!CanControl || _stop.Token.IsCancellationRequested) break;
                // Every file is a separate, user-approved TLS transfer.
                await _transfers.UploadAsync(file, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            UnauthorizedAccessException or OperationCanceledException or TimeoutException or
            InvalidOperationException or System.Security.Cryptography.CryptographicException)
        { ShowStatus("Send file failed: " + ex.Message); }
    }

    private async Task RequestFileAsync()
    {
        if (!CanControl || _stop is null) return;
        try { await _transfers.DownloadAsync(_stop.Token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            UnauthorizedAccessException or OperationCanceledException or TimeoutException or
            InvalidOperationException)
        { ShowStatus("Receive file failed: " + ex.Message); }
    }

    private async Task<string?> SelectDownloadDestinationAsync(LabFileOffer offer, CancellationToken stop)
    {
        if (IsDisposed || !IsHandleCreated) return null;
        var completion = new TaskCompletionSource<string?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed || !CanControl) { completion.TrySetResult(null); return; }
                if (_managerDownloadFolder is { } selectedFolder)
                {
                    try
                    {
                        string safeName = LabFileTransfer.SafeFileName(offer.FileName);
                        if (!string.Equals(safeName, _managerExpectedFile,
                                StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Server offered an unexpected filename.");
                        if (!Directory.Exists(selectedFolder) ||
                            (File.GetAttributes(selectedFolder) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Local destination is missing or redirected.");
                        string destination = Path.Combine(selectedFolder, safeName);
                        if (File.Exists(destination) || Directory.Exists(destination))
                            throw new IOException("Local file already exists; no overwrite permitted.");
                        completion.TrySetResult(destination);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or
                        UnauthorizedAccessException)
                    {
                        ShowStatus("Download destination denied: " + ex.Message);
                        completion.TrySetResult(null);
                    }
                    return;
                }
                using var save = new SaveFileDialog {
                    Title = "Save file offered by Server (4 GiB maximum)",
                    FileName = offer.FileName, Filter = "All files (*.*)|*.*",
                    AddExtension = false, OverwritePrompt = true
                };
                completion.TrySetResult(save.ShowDialog(this) == DialogResult.OK
                    ? save.FileName : null);
            }));
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), stop);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
            OperationCanceledException or TimeoutException)
        { return null; }
    }

    private void SaveSnapshot()
    {
        if (_picture.Image is null) { _status.Text = "No image to save."; return; }
        using var dialog = new SaveFileDialog {
            Filter = "PNG image (*.png)|*.png", DefaultExt = "png",
            AddExtension = true, FileName = "TJ.RemoteAccess-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { _picture.Image.Save(dialog.FileName, System.Drawing.Imaging.ImageFormat.Png); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { MessageBox.Show(this, ex.Message, "Unable to save image", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async Task SendTaskManagerShortcutAsync()
    {
        if (!CanControl) { _status.Text = "Control permission required to send remote keys."; return; }
        ushort[] keys = { (ushort)Keys.ControlKey, (ushort)Keys.ShiftKey, (ushort)Keys.Escape };
        try
        {
            foreach (ushort key in keys) await SendAsync(LabMessage.Key, LabWire.Key(key, true));
        }
        finally
        {
            for (int i = keys.Length - 1; i >= 0; i--)
                await SendAsync(LabMessage.Key, LabWire.Key(keys[i], false));
        }
    }

private LabViewerProfile? SelectedProfile =>
        _profilePicker.SelectedItem as LabViewerProfile;

    private void ReloadProfiles(string? preferredId = null)
    {
        if (_connected || _tcp is not null) return;
        try
        {
            var catalog = _profileStore.Load();
            string selected = preferredId ?? catalog.SelectedProfileId;
            _loadingProfiles = true;
            _profilePicker.BeginUpdate();
            try
            {
                _profilePicker.Items.Clear();
                foreach (var profile in catalog.Profiles)
                    _profilePicker.Items.Add(profile);
                var index = catalog.Profiles.FindIndex(p => p.Id == selected);
                _profilePicker.SelectedIndex = index >= 0 ? index :
                    (catalog.Profiles.Count == 0 ? -1 : 0);
            }
            finally
            {
                _profilePicker.EndUpdate();
                _loadingProfiles = false;
            }
            ApplySelectedProfile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                  or InvalidDataException or InvalidOperationException)
        {
            _loadingProfiles = true;
            _profilePicker.Items.Clear();
            _loadingProfiles = false;
            _host.Clear();
            _port.Value = LabCertificates.DefaultPort;
            _status.Text = "Unable to load server profiles: " + ex.Message;
            _connect.Enabled = false;
        }
        UpdateProfileControls();
    }

    private void ApplySelectedProfile()
    {
        if (_loadingProfiles || _connected || _tcp is not null) return;
        if (SelectedProfile is not { } profile)
        {
            _host.Clear();
            _port.Value = LabCertificates.DefaultPort;
            _status.Text = "No server paired. Click Add server.";
            UpdateProfileControls();
            return;
        }
        _host.Text = profile.Host;
        _port.Value = profile.Port;
        try
        {
            _profileStore.Select(profile.Id);
            _status.Text = $"Ready: {profile.Name} | TLS identity {profile.ServerSha256[..12]}...";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                  or InvalidDataException or KeyNotFoundException)
        {
            _status.Text = "Unable to select server: " + ex.Message;
        }
        UpdateProfileControls();
    }

    private void UpdateProfileControls()
    {
        bool busy = _connecting || _connected || _tcp is not null;
        _profilePicker.Enabled = !busy;
        _addProfile.Enabled = !busy;
        _addPassword.Enabled = !busy;
#if TJRA_STANDALONE_PREVIEW
        _quickConnect.Enabled = !busy;
#endif
        _editProfile.Enabled = !busy && SelectedProfile is not null;
        _removeProfile.Enabled = !busy && SelectedProfile is not null;
        _detailsProfile.Enabled = !busy && SelectedProfile is not null;
        _refreshProfile.Enabled = !busy;
        _host.Enabled = !busy && SelectedProfile is not null;
        _port.Enabled = !busy && SelectedProfile is not null;
        _connect.Enabled = !busy && SelectedProfile is not null;
        _disconnect.Enabled = busy;
        _reconnect.Enabled = !_connecting && SelectedProfile is not null;
        _transferFiles.Enabled = CanControl;
        _sendFile.Enabled = CanControl;
        _receiveFile.Enabled = CanControl;
        _connectionInfo.Enabled = _connected;
        _refreshScreen.Enabled = _connected && !_displayPaused;
        _pauseDisplay.Enabled = _connected;
        _snapshot.Enabled = _connected && _picture.Image is not null;
        _sendTaskManager.Enabled = CanControl;
        UpdateModifierMenus();
    }

    private void AddServerProfile()
    {
        if (_connected || _tcp is not null) return;
        var profile = LabViewerProfileDialogs.Import(this, _profileStore);
        if (profile is not null)
            ReloadProfiles(profile.Id);
    }

    private void AddPasswordServerProfile()
    {
        if (_connecting || _connected || _tcp is not null) return;
        var profile = LabViewerPasswordDialogs.Add(this, _profileStore);
        if (profile is not null)
            ReloadProfiles(profile.Id);
    }

    private void EditServerProfile()
    {
        if (_connected || _tcp is not null || SelectedProfile is not { } selected)
            return;
        var updated = LabViewerProfileDialogs.Edit(this, _profileStore, selected);
        if (updated is not null)
            ReloadProfiles(updated.Id);
    }

    private void RemoveServerProfile()
    {
        if (_connected || _tcp is not null || SelectedProfile is not { } selected)
            return;
        if (MessageBox.Show(this,
                $"Remove '{selected.Name}' from this Viewer?\n" +
                "The Windows client certificate and legacy configuration will be preserved.",
                "Remove saved server", MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        try
        {
            _profileStore.Remove(selected.Id);
            ReloadProfiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                  or KeyNotFoundException or InvalidDataException)
        {
            MessageBox.Show(this, ex.Message, "Unable to remove server",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    private async Task ConnectAsync(LabQuickPasswordRequest? quick = null)
    {
        if (_connecting || _connected || _tcp is not null) return;
        _connecting = true;
        try
        {
            UpdateProfileControls();
            LabViewerProfile profile;
            string? loginPassword;
            bool firstUse = false;
            if (quick is not null)
            {
                if (!LabWire.ValidPassword(quick.Password))
                    throw new InvalidDataException("Enter a valid Server password (6..128 printable characters).");
                var existing = _profileStore.FindPasswordEndpoint(quick.Host, quick.Port);
                firstUse = existing is null;
                profile = existing ?? new LabViewerProfile {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = ("Password " + quick.Host)[..Math.Min(80, 9 + quick.Host.Length)],
                    Host = quick.Host, Port = quick.Port,
                    AuthMode = "Password", ClientCertificateThumbprint = "",
                    ServerSha256 = ""
                };
                loginPassword = quick.Password;
            }
            else
            {
                var selected = SelectedProfile ?? throw new InvalidOperationException(
                    "Select or pair a saved server before connecting.");
                // Editing a saved endpoint never disables pinned certificate verification.
                var host = _host.Text.Trim();
                var port = (int)_port.Value;
                profile = (host != selected.Host || port != selected.Port)
                    ? _profileStore.Edit(selected.Id, selected.Name, host, port)
                    : selected;
                loginPassword = profile.AuthMode == "Password" ?
                    LabViewerPasswordDialogs.PromptPassword(this) : null;
                if (profile.AuthMode == "Password" && loginPassword is null) return;
            }
            _status.Text = $"Authenticating TLS: {profile.Name}...";
            bool passwordMode = profile.AuthMode == "Password";
            bool firstUseApproved = false;
            if (firstUse)
            {
#if TJRA_STANDALONE_PREVIEW
                // Phase 1: TLS fingerprint-only probe. No PasswordLogin or
                // Hello packet is sent; close this connection before the
                // operator compares SHA-256 on the actual Server computer.
                string presentedSha = await LabQuickPasswordTrust.ProbeServerIdentityAsync(
                    profile.Host, profile.Port, CancellationToken.None);
                _status.Text = "Verify Server identity before sending password.";
                firstUseApproved = LabViewerQuickPasswordDialog.ConfirmFirstUse(
                    this, profile.Host, profile.Port, presentedSha);
                if (!firstUseApproved)
                    throw new AuthenticationException(
                        "Server identity not approved. Password was NOT sent.");
                // Phase 2 MUST verify this exact pin again. A changed Server
                // certificate between probe/confirm and login is rejected.
                profile.ServerSha256 = presentedSha;
#else
                throw new AuthenticationException("First-use trust is not available in Lab.");
#endif
            }
            string? observedServerPin = null;
            using var clientCert = passwordMode ? null :
                LabCertificates.LoadPrivateCertificate(profile.ClientCertificateThumbprint);
            if (clientCert is not null &&
                !LabCertificates.ValidatePinned(clientCert, LabCertificates.Hash(clientCert),
                                                LabCertificates.ClientEku))
                throw new AuthenticationException("Viewer certificate has invalid EKU/expiry.");

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            await _tcp.ConnectAsync(profile.Host, profile.Port, token)
                      .AsTask().WaitAsync(TimeSpan.FromSeconds(12), token);
            _tls = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost", // exact DER pin, not DNS-based PKI trust
                ClientCertificates = clientCert is null ? null : new X509CertificateCollection { clientCert },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                {
                    if (passwordMode)
                    {
                        // The real password TLS session always uses an EXACT
                        // Server pin. For first use, it was confirmed using a
                        // separate, closed, fingerprint-only TLS probe.
                        observedServerPin = LabQuickPasswordTrust.ExamineServer(
                            cert, profile.ServerSha256);
                        return observedServerPin is not null;
                    }
                    return LabCertificates.ValidatePinned(cert, profile.ServerSha256,
                                                          LabCertificates.ServerEku);
                }
            };
            await _tls.AuthenticateAsClientAsync(options, token)
                      .WaitAsync(TimeSpan.FromSeconds(12), token);
            if (passwordMode)
            {
                bool identityApproved = !firstUse || firstUseApproved;
                // Encrypted TLS handshake alone does NOT authenticate an
                // unknown Server. NEVER send a password before verification.
                if (!LabQuickPasswordTrust.MaySendPassword(
                    _tls.IsAuthenticated, _tls.IsEncrypted,
                    observedServerPin, identityApproved))
                    throw new AuthenticationException(
                        "Server identity was not verified. Password was NOT sent.");
                byte[] secret = LabWire.PasswordLogin(loginPassword!);
                loginPassword = null;
                try {
                    await LabWire.WriteAsync(_tls, _outgoingMutex,
                                             LabMessage.PasswordLogin, secret, token);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
            }
            await LabWire.WriteAsync(_tls, _outgoingMutex,
                                     LabMessage.Hello,
                                     _options.ViewOnly ? new byte[] { LabWire.Version, 1 } :
                                     new byte[] { LabWire.Version }, token);
            var welcome = await LabWire.ReadAsync(_tls, token)
                                       .WaitAsync(TimeSpan.FromSeconds(8), token);
            if (welcome.Type != LabMessage.Welcome)
                throw new AuthenticationException("Server did not authorize this viewer.");
            var metadata = LabWire.ParseWelcome(welcome.Payload);
            if (firstUse)
            {
                // Successful welcome proves knowledge of the configured
                // Server password on this accepted TLS channel; pin the
                // exact Host:Port+SHA for future silent verification.
                profile = _profileStore.RememberQuickPasswordEndpoint(
                    profile.Name, profile.Host, profile.Port, observedServerPin!);
            }
            _desktopWidth = metadata.Width;
            _desktopHeight = metadata.Height;
            _role = metadata.Role;
            if (_role == LabRole.Controller)
                await LabWire.WriteAsync(_tls, _outgoingMutex, LabMessage.CaptureOptions,
                    LabWire.CaptureSettings((byte)_options.JpegQuality, (byte)_options.FramesPerSecond), token);
            _activeProfile = profile;
            _connectedAt = DateTimeOffset.UtcNow;
            _connected = true;
            _manualDisconnect = false;
            _reconnectAttempts = 0;
            _reconnectTimer.Stop();
            if (_options.FullScreen) SetFullScreen(true);
            _status.Text = $"Connected to {profile.Name} ({_role}), desktop {_desktopWidth}x{_desktopHeight}. " +
                           "Secure desktop/UAC not supported.";
            // JPEG decode runs off UI thread; only final bitmap swap uses UI.
            var activeStream = _tls;
            _ = Task.Run(() => ReadFramesAsync(activeStream, token), token);
        }
        catch (Exception ex)
        {
            Disconnect();
            _status.Text = "Connection failed: " + ex.Message;
            if (_reconnectAttempts > 0 && _reconnectAttempts < 3 &&
                _options.MayAutoReconnect(SelectedProfile?.AuthMode == "Certificate", _manualDisconnect))
            {
                _reconnectAttempts++;
                _reconnectTimer.Start();
            }
        }
        finally {
            _connecting = false;
            UpdateProfileControls();
        }
    }

    private async Task ReadFramesAsync(SslStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var frameDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                frameDeadline.CancelAfter(TimeSpan.FromSeconds(25));
                var packet = await LabWire.ReadAsync(stream, frameDeadline.Token);
                // File packets share the authenticated TLS stream, not the JPEG decoder.
                if (LabViewerTransfers.IsFileMessage(packet.Type))
                {
                    await _transfers.HandleAsync(packet, token);
                    continue;
                }
                if (packet.Type is LabMessage.ClipboardDecision or LabMessage.ClipboardText)
                {
                    await HandleClipboardResponseAsync(packet, token);
                    continue;
                }
                if (packet.Type == LabMessage.FileManagerReply)
                {
                    _remoteFileClient.HandleReply(packet.Payload);
                    continue;
                }
                if (packet.Type != LabMessage.JpegFrame)
                    throw new InvalidDataException("Unexpected server packet.");
                // Local freeze keeps reading authenticated frames to avoid
                // backpressure, while preserving the last rendered bitmap.
                if (_displayPaused) continue;
                if (packet.Payload.Length < 4 ||
                    packet.Payload[0] != 0xFF || packet.Payload[1] != 0xD8)
                    throw new InvalidDataException("Invalid JPEG frame.");
                using var memory = new MemoryStream(packet.Payload, writable: false);
                using var image = Image.FromStream(memory);
                if (image.Width is < 1 or > 4096 || image.Height is < 1 or > 2304 ||
                    (long)image.Width * image.Height > 9_000_000)
                    throw new InvalidDataException("Remote JPEG dimensions exceed lab limits.");
                var bitmap = new Bitmap(image);
                if (Interlocked.CompareExchange(ref _frameUpdatePending, 1, 0) != 0)
                {
                    // A busy UI never accumulates a queue of decoded Bitmaps.
                    bitmap.Dispose();
                    continue;
                }
                if (IsDisposed)
                {
                    Interlocked.Exchange(ref _frameUpdatePending, 0);
                    bitmap.Dispose();
                    return;
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            if (IsDisposed || !_connected || _displayPaused || !ReferenceEquals(_tls, stream))
                            {
                                bitmap.Dispose();
                                return;
                            }
                            var oldImage = _picture.Image;
                            _picture.Image = bitmap;
                            ApplyDisplayScale();
                            oldImage?.Dispose();
                            _frameCount++;
                            if (_frameCount % 30 == 0)
                            {
#if TJRA_STANDALONE_PREVIEW
                                _status.Text = $"Connected ({_role}) | {_frameCount} frames | " +
                                               $"{_desktopWidth}x{_desktopHeight} | TLS Preview";
#else
                                _status.Text = $"Connected ({_role}) | {_frameCount} frames | " +
                                               $"{_desktopWidth}x{_desktopHeight} | LAB ONLY";
#endif
                            }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _frameUpdatePending, 0);
                        }
                    }));
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                {
                    Interlocked.Exchange(ref _frameUpdatePending, 0);
                    bitmap.Dispose();
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                   or OperationCanceledException or ObjectDisposedException
                                   or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            if (!token.IsCancellationRequested && !IsDisposed && ReferenceEquals(_tls, stream))
                ShowStatus("Disconnected: " + ex.Message);
        }
        finally {
            if (!IsDisposed)
                try {
                    BeginInvoke(new Action(() => {
                        // An old reader must never close a newly connected session.
                        if (ReferenceEquals(_tls, stream))
                        {
                            bool retry = _options.MayAutoReconnect(
                                SelectedProfile?.AuthMode == "Certificate", _manualDisconnect);
                            Disconnect();
                            if (retry && _reconnectAttempts < 3)
                            {
                                _reconnectAttempts++;
                                _reconnectTimer.Start();
                                _status.Text = "Connection lost; certificate reconnect attempt " +
                                    _reconnectAttempts + "/3 scheduled.";
                            }
                        }
                    }));
                }
                catch (InvalidOperationException) { }
        }
    }

    private void ShowStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) {
            try { BeginInvoke(new Action<string>(ShowStatus), text); }
            catch (InvalidOperationException) { }
            return;
        }
        _status.Text = text;
    }

    private async Task SendTransferAsync(LabMessage type, ReadOnlyMemory<byte> payload)
    {
        // Transfer commands must propagate socket failure to the caller;
        // best-effort pointer/keyboard SendAsync intentionally remains separate.
        var stream = _tls ?? throw new IOException("Transfer connection is closed.");
        var token = _stop?.Token ?? throw new IOException("Transfer session has ended.");
        if (!_connected || token.IsCancellationRequested)
            throw new IOException("Transfer connection is not active.");
        await LabWire.WriteAsync(stream, _outgoingMutex, type, payload, token);
    }

    private async Task SendAsync(LabMessage type, ReadOnlyMemory<byte> payload)
    {
        var tls = _tls;
        var token = _stop?.Token ?? CancellationToken.None;
        if (!_connected || tls is null || token.IsCancellationRequested) return;
        try { await LabWire.WriteAsync(tls, _outgoingMutex, type, payload, token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException
                                   or ObjectDisposedException or InvalidOperationException)
        {
            if (!IsDisposed) ShowStatus("Input connection closed: " + ex.Message);
        }
    }

    private async Task OnRemoteKey(KeyEventArgs e, bool pressed)
    {
        if (!CanControl || !_picture.Focused) return;
        ushort vk = (ushort)e.KeyCode;
        if (vk is < 1 or > 254) return;
        bool shouldSend = pressed ? _heldKeys.Add(vk) : _heldKeys.Remove(vk);
        if (!shouldSend) return;
        e.SuppressKeyPress = true;
        await SendAsync(LabMessage.Key, LabWire.Key(vk, pressed));
    }

    private async Task ReleaseHeldKeysAsync()
    {
        if (!CanControl) {
            _heldKeys.Clear();
            UpdateModifierMenus();
            return;
        }
        var oldKeys = _heldKeys.ToArray();
        _heldKeys.Clear();
        foreach (ushort key in oldKeys)
            await SendAsync(LabMessage.Key, LabWire.Key(key, pressed: false));
        await SendAsync(LabMessage.Pointer, LabWire.Pointer((ushort)_lastX,
                                                           (ushort)_lastY, 0));
        UpdateModifierMenus();
    }

    private async Task SendPointerAsync(Point location, bool force = false)
    {
        if (!CanControl || _picture.Image is null) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastMoveAt < 33) return; // capped ~30 input/s
        _lastMoveAt = now;
        var control = _picture.ClientRectangle;
        float displayedWidth, displayedHeight, left, top;
        if (_picture.SizeMode == PictureBoxSizeMode.StretchImage)
        {
            displayedWidth = control.Width; displayedHeight = control.Height;
            left = 0; top = 0;
        }
        else
        {
            float factor = Math.Min((float)control.Width / _desktopWidth,
                                    (float)control.Height / _desktopHeight);
            displayedWidth = _desktopWidth * factor;
            displayedHeight = _desktopHeight * factor;
            left = (control.Width - displayedWidth) / 2.0f;
            top = (control.Height - displayedHeight) / 2.0f;
        }
        if (displayedWidth <= 0 || displayedHeight <= 0) return;
        float x = (location.X - left) / displayedWidth;
        float y = (location.Y - top) / displayedHeight;
        // A button-up event may arrive outside the image during a drag.
        // Always release at the nearest edge instead of leaving it held.
        if (!force && Control.MouseButtons == MouseButtons.None &&
            (x is < 0 or > 1 || y is < 0 or > 1)) return;
        x = Math.Clamp(x, 0, 1); y = Math.Clamp(y, 0, 1);
        ushort normalizedX = (ushort)Math.Clamp((int)Math.Round(x * 65535), 0, 65535);
        ushort normalizedY = (ushort)Math.Clamp((int)Math.Round(y * 65535), 0, 65535);
        _lastX = normalizedX;
        _lastY = normalizedY;
        byte buttons = 0;
        if ((Control.MouseButtons & MouseButtons.Left) != 0) buttons |= 1;
        if ((Control.MouseButtons & MouseButtons.Right) != 0) buttons |= 2;
        if ((Control.MouseButtons & MouseButtons.Middle) != 0) buttons |= 4;
        buttons = _options.MapMouseButtons(buttons);
        await SendAsync(LabMessage.Pointer, LabWire.Pointer(normalizedX,
                                                           normalizedY, buttons));
    }

    private void Disconnect()
    {
        _connected = false;
        _activeProfile = null;
        _reconnectTimer.Stop();
        _remoteFileClient.Disconnect();
        _transfers.Disconnect();
        _transferFilesWindow?.SessionClosed();
        _managerDownloadFolder = null;
        _managerExpectedFile = null;
        _stop?.Cancel();
        _tcp?.Dispose();
        _tls?.Dispose();
        _tcp = null;
        _tls = null;
        _stop?.Dispose();
        _stop = null;
        lock (_clipboardGuard)
        {
            _clipboardWriteId = null;
            _clipboardReadId = null;
        }
        _heldKeys.Clear();
        UpdateModifierMenus();
        _displayPaused = false;
        _pauseDisplay.Text = "Pause display";
        _role = LabRole.ViewOnly;
        _frameCount = 0;
        var oldImage = _picture.Image;
        _picture.Image = null;
        oldImage?.Dispose();
        if (!IsDisposed) {
            ReloadProfiles();
            UpdateProfileControls();
            _status.Text = "Disconnected. Choose a saved server to reconnect.";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) {
            _picture.Image?.Dispose();
            _keepAlive.Dispose();
            _reconnectTimer.Dispose();
            _transfers.Dispose();
            _outgoingMutex.Dispose();
        }
        base.Dispose(disposing);
    }
}

// PictureBox is not focusable by default. The remote desktop must be the
// selected keyboard target after a user clicks its image area.
internal sealed class RemoteCanvas : PictureBox
{
    internal RemoteCanvas()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
    }
    protected override bool IsInputKey(Keys keyData) => true;
}
