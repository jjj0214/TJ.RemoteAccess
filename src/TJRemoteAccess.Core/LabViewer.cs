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
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new LabViewerForm(new LabViewerProfileStore()));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TJ.RemoteAccess Lab Viewer",
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

    internal LabViewerForm(LabViewerProfileStore profileStore)
    {
        _profileStore = profileStore;
        Text = "TJ.RemoteAccess Lab Viewer - TEST ONLY";
        MinimumSize = new Size(720, 440);
        Width = 1210;
        Height = 790;
        KeyPreview = true;
        _port.Value = 15444;
        var toolbar = new FlowLayoutPanel {
            Dock = DockStyle.Top, Height = 98, Padding = new Padding(5),
            AutoScroll = true, WrapContents = true
        };
        toolbar.Controls.Add(new Label { Text = "Saved server", AutoSize = true,
            Padding = new Padding(0, 6, 0, 0) });
        toolbar.Controls.Add(_profilePicker);
        toolbar.Controls.Add(_addProfile);
        toolbar.Controls.Add(_addPassword);
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
        Controls.Add(_picture);
        Controls.Add(_status);
        Controls.Add(toolbar);
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
        _disconnect.Click += (_, _) => Disconnect();
        _picture.MouseDown += async (_, e) => {
            _picture.Focus();
            await SendPointerAsync(e.Location, force: true);
        };
        _picture.MouseMove += async (_, e) => await SendPointerAsync(e.Location);
        _picture.MouseUp += async (_, e) => await SendPointerAsync(e.Location, force: true);
        _picture.MouseWheel += (_, _) => { }; // wheel is unsupported in M2 Lab
        KeyDown += async (_, e) => await OnRemoteKey(e, pressed: true);
        KeyUp += async (_, e) => await OnRemoteKey(e, pressed: false);
        Deactivate += async (_, _) => await ReleaseHeldKeysAsync();
        FormClosing += (_, _) => Disconnect();
        _keepAlive.Tick += async (_, _) => {
            if (_connected)
                await SendAsync(LabMessage.KeepAlive, ReadOnlyMemory<byte>.Empty);
        };
        _keepAlive.Start();
        ReloadProfiles();
        _status.Text = SelectedProfile is null ? "No server paired. Click Add server." : "Ready. Select a saved server to connect.";
    }

    private bool CanControl => _connected && _role == LabRole.Controller;

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
            _port.Value = 15444;
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
            _port.Value = 15444;
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
        _editProfile.Enabled = !busy && SelectedProfile is not null;
        _removeProfile.Enabled = !busy && SelectedProfile is not null;
        _detailsProfile.Enabled = !busy && SelectedProfile is not null;
        _refreshProfile.Enabled = !busy;
        _host.Enabled = !busy && SelectedProfile is not null;
        _port.Enabled = !busy && SelectedProfile is not null;
        _connect.Enabled = !busy && SelectedProfile is not null;
        _disconnect.Enabled = busy;
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
    private async Task ConnectAsync()
    {
        if (_connecting || _connected || _tcp is not null) return;
        _connecting = true;
        try
        {
            UpdateProfileControls();
            var selected = SelectedProfile ?? throw new InvalidOperationException(
                "Select or pair a saved server before connecting.");
            // Editing the host is permitted, but it never changes the pinned
            // server identity or the client certificate for this profile.
            var host = _host.Text.Trim();
            var port = (int)_port.Value;
            var profile = (host != selected.Host || port != selected.Port)
                ? _profileStore.Edit(selected.Id, selected.Name, host, port)
                : selected;
            _status.Text = $"Authenticating TLS: {profile.Name}...";
            bool passwordMode = profile.AuthMode == "Password";
            string? loginPassword = passwordMode ?
                LabViewerPasswordDialogs.PromptPassword(this) : null;
            if (passwordMode && loginPassword is null) return;
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
                    LabCertificates.ValidatePinned(cert, profile.ServerSha256,
                                                   LabCertificates.ServerEku)
            };
            await _tls.AuthenticateAsClientAsync(options, token)
                      .WaitAsync(TimeSpan.FromSeconds(12), token);
            if (passwordMode)
            {
                byte[] secret = LabWire.PasswordLogin(loginPassword!);
                loginPassword = null;
                try {
                    await LabWire.WriteAsync(_tls, _outgoingMutex,
                                             LabMessage.PasswordLogin, secret, token);
                }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret); }
            }
            await LabWire.WriteAsync(_tls, _outgoingMutex,
                                     LabMessage.Hello, new[] { LabWire.Version }, token);
            var welcome = await LabWire.ReadAsync(_tls, token)
                                       .WaitAsync(TimeSpan.FromSeconds(8), token);
            if (welcome.Type != LabMessage.Welcome)
                throw new AuthenticationException("Server did not authorize this viewer.");
            var metadata = LabWire.ParseWelcome(welcome.Payload);
            _desktopWidth = metadata.Width;
            _desktopHeight = metadata.Height;
            _role = metadata.Role;
            _connected = true;
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
                if (packet.Type != LabMessage.JpegFrame)
                    throw new InvalidDataException("Unexpected server packet.");
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
                            if (IsDisposed || !_connected || !ReferenceEquals(_tls, stream))
                            {
                                bitmap.Dispose();
                                return;
                            }
                            var oldImage = _picture.Image;
                            _picture.Image = bitmap;
                            oldImage?.Dispose();
                            _frameCount++;
                            if (_frameCount % 30 == 0)
                                _status.Text = $"Connected ({_role}) | {_frameCount} frames | " +
                                               $"{_desktopWidth}x{_desktopHeight} | LAB ONLY";
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
                        if (ReferenceEquals(_tls, stream)) Disconnect();
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
        if (!CanControl) return;
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
            return;
        }
        var oldKeys = _heldKeys.ToArray();
        _heldKeys.Clear();
        foreach (ushort key in oldKeys)
            await SendAsync(LabMessage.Key, LabWire.Key(key, pressed: false));
        await SendAsync(LabMessage.Pointer, LabWire.Pointer((ushort)_lastX,
                                                           (ushort)_lastY, 0));
    }

    private async Task SendPointerAsync(Point location, bool force = false)
    {
        if (!CanControl || _picture.Image is null) return;
        long now = Environment.TickCount64;
        if (!force && now - _lastMoveAt < 33) return; // capped ~30 input/s
        _lastMoveAt = now;
        var control = _picture.ClientRectangle;
        float factor = Math.Min((float)control.Width / _desktopWidth,
                                (float)control.Height / _desktopHeight);
        if (factor <= 0) return;
        float displayedWidth = _desktopWidth * factor;
        float displayedHeight = _desktopHeight * factor;
        float left = (control.Width - displayedWidth) / 2.0f;
        float top = (control.Height - displayedHeight) / 2.0f;
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
        await SendAsync(LabMessage.Pointer, LabWire.Pointer(normalizedX,
                                                           normalizedY, buttons));
    }

    private void Disconnect()
    {
        _connected = false;
        _stop?.Cancel();
        _tcp?.Dispose();
        _tls?.Dispose();
        _tcp = null;
        _tls = null;
        _stop?.Dispose();
        _stop = null;
        _heldKeys.Clear();
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
