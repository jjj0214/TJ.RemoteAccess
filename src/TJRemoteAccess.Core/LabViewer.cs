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
            var settings = LabCertificates.LoadSettings<LabViewerSettings>(
                LabCertificates.ViewerFile);
            Application.Run(new LabViewerForm(settings));
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
    private readonly LabViewerSettings _settings;
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
    private int _lastX, _lastY;
    private long _lastMoveAt;
    private bool _connected;
    private readonly System.Windows.Forms.Timer _keepAlive = new() { Interval = 5000 };

    internal LabViewerForm(LabViewerSettings settings)
    {
        _settings = settings;
        Text = "TJ.RemoteAccess Lab Viewer - TEST ONLY";
        MinimumSize = new Size(600, 400);
        Width = 1180;
        Height = 780;
        KeyPreview = true;
        _host.Text = settings.Host;
        _port.Value = settings.Port is >= 1024 and <= 65535 ? settings.Port : 15444;
        var toolbar = new FlowLayoutPanel {
            Dock = DockStyle.Top, Height = 43, Padding = new Padding(5)
        };
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
        _status.Text = "Disconnected. TLS authentication and client pairing required.";
    }

    private bool CanControl => _connected && _role == LabRole.Controller;

    private async Task ConnectAsync()
    {
        if (_connected || _tcp is not null) return;
        try
        {
            _connect.Enabled = false;
            _status.Text = "Authenticating TLS...";
            var clientCert = LabCertificates.LoadPrivateCertificate(
                _settings.ClientCertificateThumbprint);
            if (!LabCertificates.ValidatePinned(clientCert, LabCertificates.Hash(clientCert),
                                                LabCertificates.ClientEku))
                throw new AuthenticationException("Viewer certificate has invalid EKU/expiry.");

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            await _tcp.ConnectAsync(_host.Text.Trim(), (int)_port.Value, token)
                      .AsTask().WaitAsync(TimeSpan.FromSeconds(12), token);
            _tls = new SslStream(_tcp.GetStream(), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = "localhost", // exact DER pin, not DNS-based PKI trust
                ClientCertificates = new X509CertificateCollection { clientCert },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    LabCertificates.ValidatePinned(cert, _settings.ServerSha256,
                                                   LabCertificates.ServerEku)
            };
            await _tls.AuthenticateAsClientAsync(options, token)
                      .WaitAsync(TimeSpan.FromSeconds(12), token);
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
            _disconnect.Enabled = true;
            _status.Text = $"Connected ({_role}), desktop {_desktopWidth}x{_desktopHeight}. " +
                           "Secure desktop/UAC not supported.";
            // JPEG decode runs off UI thread; only final bitmap swap uses UI.
            _ = Task.Run(() => ReadFramesAsync(_tls, token), token);
        }
        catch (Exception ex)
        {
            Disconnect();
            _status.Text = "Connection failed: " + ex.Message;
        }
        finally { _connect.Enabled = !_connected; }
    }

    private async Task ReadFramesAsync(SslStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var packet = await LabWire.ReadAsync(stream, token);
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
                if (IsDisposed) {
                    bitmap.Dispose();
                    return;
                }
                try {
                    BeginInvoke(new Action(() => {
                        if (IsDisposed) {
                            bitmap.Dispose();
                            return;
                        }
                        var old = _picture.Image;
                        _picture.Image = bitmap;
                        old?.Dispose();
                        _frameCount++;
                        if (_frameCount % 30 == 0)
                            _status.Text = $"Connected ({_role}) | {_frameCount} frames | " +
                                           $"{_desktopWidth}x{_desktopHeight} | LAB ONLY";
                    }));
                }
                catch (InvalidOperationException) {
                    bitmap.Dispose();
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                   or OperationCanceledException or ObjectDisposedException)
        {
            if (!token.IsCancellationRequested && !IsDisposed)
                ShowStatus("Disconnected: " + ex.Message);
        }
        finally {
            if (!IsDisposed)
                try { BeginInvoke(new Action(Disconnect)); }
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
        _connect.Enabled = true;
        _disconnect.Enabled = false;
        if (!IsDisposed) _status.Text = "Disconnected (LAB test connection closed).";
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
