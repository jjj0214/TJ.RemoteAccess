// SPDX-License-Identifier: GPL-2.0-or-later
// INTERACTIVE LAB ONLY. Authenticated desktop sharing; no Windows Service or
// secure desktop/UAC support. Never exposes legacy VNC/RFB TCP.
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabServerProgram
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            var settings = LabCertificates.LoadSettings<LabServerSettings>(LabCertificates.ServerFile);
            bool explicitlyAllowLan = args.Any(x => x == "--allow-lan");
            if (!IPAddress.TryParse(settings.ListenAddress, out var ip))
                throw new InvalidDataException("ListenAddress must be an explicit IP address.");
            if (!IPAddress.IsLoopback(ip) && !explicitlyAllowLan)
                throw new InvalidOperationException(
                    "LAN exposure requires an explicit --allow-lan argument. " +
                    "TLS and verified Server identity remain mandatory.");
            if (settings.Port is < 1024 or > 65535)
                throw new InvalidDataException("Port must be 1024..65535.");
            using var cert = LabCertificates.LoadPrivateCertificate(
                settings.ServerCertificateThumbprint);
            if (!LabCertificates.ValidatePinned(cert, LabCertificates.Hash(cert),
                                                LabCertificates.ServerEku))
                throw new InvalidDataException("Configured local server certificate invalid.");
            Application.Run(new LabServerForm(settings, ip, cert));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "TJ.RemoteAccess Lab Server: startup blocked",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

internal sealed class LabServerForm : Form
{
    private readonly LabServerSettings _settings;
    private readonly IPAddress _address;
    private readonly X509Certificate2 _serverCert;
    private readonly LabServerClientRoster _clientRoster;
    private readonly LabPasswordAccess _passwordAccess = new();
    private readonly LabPasswordAttemptLimiter _passwordLimiter = new();
    private readonly RichTextBox _events = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly Label _summary = new() {
        AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(12)
    };
    private readonly Button _start = new() { Text = "Start (interactive session)" };
    private readonly Button _stopButton = new() { Text = "Stop", Enabled = false };
    private readonly Button _clientsButton = new() { Text = "Client access", AutoSize = true };
    private readonly Button _passwordButton = new() { Text = "Password access", AutoSize = true };
    private readonly object _guard = new();
    private readonly HashSet<Guid> _sessions = new();
    private Guid? _controller;
    private bool _running;
    private readonly HashSet<ushort> _pressedKeys = new();
    private byte _pressedMouse;
    private readonly SemaphoreSlim _handshakeLimit = new(4);
    private TcpListener? _listener;
    private TcpListener? _passwordListener;
    private CancellationTokenSource? _shutdown;
    private readonly Rectangle _desktop;

    internal LabServerForm(LabServerSettings settings, IPAddress address,
                           X509Certificate2 serverCert)
    {
        _settings = settings;
        _address = address;
        _serverCert = serverCert;
        _clientRoster = new LabServerClientRoster(legacyPin: settings.AllowedClientSha256);
        _clientRoster.Load();
        _desktop = SystemInformation.VirtualScreen;
        Text = "TJ.RemoteAccess Lab Server - TEST ONLY";
        MinimumSize = new Size(660, 330);
        Width = 830;
        Height = 440;
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 45 };
        _start.AutoSize = true;
        _stopButton.AutoSize = true;
        controls.Controls.Add(_start);
        controls.Controls.Add(_stopButton);
        controls.Controls.Add(_clientsButton);
        controls.Controls.Add(_passwordButton);
        Controls.Add(_events);
        Controls.Add(_summary);
        Controls.Add(controls);
        _start.Click += (_, _) => StartServer();
        _stopButton.Click += (_, _) => StopServer();
        _clientsButton.Click += (_, _) => {
            using var dialog = new LabServerClientAccessForm(_clientRoster, _settings, _serverCert);
            dialog.ShowDialog(this);
        };
        _passwordButton.Click += (_, _) => LabPasswordAccessDialog.Show(this, _passwordAccess, _settings, _serverCert);
        FormClosing += (_, _) => StopServer();
        UpdateSummary();
        Log("Interactive screen share only. Lock screen/UAC are NOT supported.");
        Log("Mutual TLS certificate access remains available; optional password TLS is disabled by default.");
    }

    private void UpdateSummary()
    {
        lock (_guard)
        {
            _summary.Text = $"Bind {_address}:{_settings.Port}  |  " +
                $"Viewers {_sessions.Count}/3  |  " +
                $"Controller {(_controller is null ? "none" : "1")}  |  " +
                $"Desktop {_desktop.Width} x {_desktop.Height}";
        }
    }

    private void Log(string value)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(Log), value); } catch (InvalidOperationException) { }
            return;
        }
        _events.AppendText($"[{DateTime.Now:HH:mm:ss}] {value}{Environment.NewLine}");
        if (_events.TextLength > 45000)
            _events.Text = _events.Text[^25000..];
        _events.ScrollToCaret();
        UpdateSummary();
    }

    private void StartServer()
    {
        if (_listener is not null) return;
        try
        {
            _shutdown = new CancellationTokenSource();
            _listener = new TcpListener(_address, _settings.Port);
            _listener.Start(backlog: 6);
            var passwordEnabled = _passwordAccess.Current().Enabled;
            if (passwordEnabled)
            {
                if (_settings.Port >= 65535)
                    throw new InvalidOperationException("No port available for password TLS listener.");
                _passwordListener = new TcpListener(_address, _settings.Port + 1);
                _passwordListener.Start(backlog: 4);
                Log($"Password TLS enabled on {_address}:{_settings.Port + 1} (server pin required).");
            }
            lock (_guard)
            {
                _running = true;
            }
            _start.Enabled = false;
            _stopButton.Enabled = true;
            Log("Listening with mandatory TLS 1.2/1.3 + client certificate pinning.");
            // TLS, screen capture and input handling must not run on UI thread.
            var run = _shutdown;
            var token = run.Token;
            var frames = new LabSharedFrameStore();
            _ = Task.Run(() => CaptureFramesShared(frames, run), token);
            var certListener = _listener;
            _ = Task.Run(() => AcceptLoop(certListener, frames, false, token), token);
            if (_passwordListener is { } passListener)
                _ = Task.Run(() => AcceptLoop(passListener, frames, true, token), token);
        }
        catch (Exception ex)
        {
            Log("Start failed: " + ex.Message);
            StopServer();
        }
    }

    private void StopServer()
    {
        _shutdown?.Cancel();
        _listener?.Stop();
        _passwordListener?.Stop();
        _passwordListener = null;
        _listener = null;
        _start.Enabled = true;
        _stopButton.Enabled = false;
        lock (_guard)
        {
            _running = false;
            LabNativeInput.ReleaseAll(_pressedKeys, ref _pressedMouse);
            _controller = null;
            _sessions.Clear();
        }
        Log("Server stopped (existing TLS connections closing).");
    }

    private async Task AcceptLoop(TcpListener listener, LabSharedFrameStore frames,
                                  bool passwordMode, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(token);
                tcp.NoDelay = true;
                _ = HandleClientAsync(tcp, frames, passwordMode, token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                tcp?.Dispose();
                if (!token.IsCancellationRequested) Log("Accept failed: " + ex.Message);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, LabSharedFrameStore frames,
                                         bool passwordMode, CancellationToken shutdown)
    {
        Guid session = Guid.NewGuid();
        bool registered = false;
        LabRole role = LabRole.ViewOnly;
        bool handshakePermit = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var token = lifetime.Token;
        using (tcp)
        {
            try
            {
                // Limit pre-auth resources and abort a stalled handshake.
                handshakePermit = await _handshakeLimit.WaitAsync(
                    TimeSpan.FromSeconds(2), token);
                if (!handshakePermit) return;
                using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
                var tlsOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _serverCert,
                    ClientCertificateRequired = !passwordMode,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        passwordMode || _clientRoster.IsCertificateAllowed(cert)
                };
                await tls.AuthenticateAsServerAsync(tlsOptions, token)
                         .WaitAsync(TimeSpan.FromSeconds(12), token);
                // Authentication modes are deliberately isolated by listener.
                // Certificate listener keeps its existing mutual-TLS policy.
                X509Certificate? remoteCert = passwordMode ? null : tls.RemoteCertificate ??
                    throw new AuthenticationException("Missing Viewer certificate.");
                string clientPin = passwordMode ? "" : LabCertificates.Hash(remoteCert!);
                string passwordVerifier = "";
                bool mayControl;
                if (passwordMode)
                {
                    string peerIp = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
                    if (!_passwordLimiter.MayAttempt(peerIp) || !_passwordAccess.Current().Enabled)
                        throw new AuthenticationException("Password access temporarily unavailable.");
                    var login = await LabWire.ReadAsync(tls, token, LabWire.MaxPasswordPacketBytes)
                                             .WaitAsync(TimeSpan.FromSeconds(10), token);
                    string entered;
                    try
                    {
                        if (login.Type != LabMessage.PasswordLogin)
                            throw new InvalidDataException("Password login required.");
                        entered = LabWire.ParsePasswordLogin(login.Payload);
                    }
                    catch (InvalidDataException)
                    {
                        _passwordLimiter.Failed(peerIp);
                        throw new AuthenticationException("Password login rejected.");
                    }
                    if (!_passwordAccess.TryAuthenticate(entered, out var access))
                    {
                        _passwordLimiter.Failed(peerIp);
                        throw new AuthenticationException("Password login rejected.");
                    }
                    _passwordLimiter.Succeeded(peerIp);
                    if (!access.Enabled) throw new AuthenticationException("Password access disabled.");
                    passwordVerifier = access.VerifierBase64;
                    mayControl = access.CanControl;
                }
                else
                {
                    var admission = _clientRoster.GetAuthorized(clientPin) ??
                        throw new AuthenticationException("Viewer identity not authorized.");
                    mayControl = admission.CanControl;
                }

                // A TLS-authenticated channel still needs an application-level
                // protocol/version check before reserving one of three slots.
                var hello = await LabWire.ReadAsync(tls, token, LabWire.MaxControlPacketBytes)
                                         .WaitAsync(TimeSpan.FromSeconds(5), token);
                if (hello.Type != LabMessage.Hello ||
                    hello.Payload.Length != 1 || hello.Payload[0] != LabWire.Version)
                    throw new InvalidDataException("Unsupported viewer protocol.");

                if (passwordMode)
                {
                    if (!_passwordAccess.IsSessionAuthorized(passwordVerifier, false))
                        throw new AuthenticationException("Password access was revoked.");
                    mayControl = _passwordAccess.Current().CanControl;
                }
                else
                {
                    var admission = _clientRoster.GetAuthorized(clientPin) ??
                        throw new AuthenticationException("Client was revoked during admission.");
                    mayControl = admission.CanControl;
                }
                lock (_guard)
                {
                    if (!_running || token.IsCancellationRequested)
                        throw new OperationCanceledException("Server is stopping.", token);
                    if (_sessions.Count >= 3)
                        throw new InvalidOperationException("Maximum three viewers reached.");
                    _sessions.Add(session);
                    registered = true;
                    if (_controller is null && mayControl) {
                        _controller = session;
                        role = LabRole.Controller;
                    }
                }
                // Bound TLS, protocol Hello and admission by the same
                // semaphore to limit pending/unauthenticated sessions.
                _handshakeLimit.Release();
                handshakePermit = false;
                Log($"{(passwordMode ? "Password TLS" : "Mutual TLS")} client connected ({role}).");
                using var writeMutex = new SemaphoreSlim(1, 1);
                await LabWire.WriteAsync(tls, writeMutex, LabMessage.Welcome,
                    LabWire.Welcome(new LabWelcome(role, _desktop.Width, _desktop.Height,
                                                   _desktop.Left, _desktop.Top)), token);

                var capture = SendFrames(tls, writeMutex, frames, remoteCert, clientPin,
                                         passwordMode, passwordVerifier, role, token);
                // If capture fails (locked desktop, lost display, encoder),
                // terminate the TLS session instead of leaving a frozen viewer.
                _ = capture.ContinueWith(task => {
                    if (!shutdown.IsCancellationRequested)
                        Log("Desktop capture or client authorization ended: " +
                            task.Exception?.GetBaseException().Message);
                    try { lifetime.Cancel(); } catch (ObjectDisposedException) { }
                    tcp.Dispose(); // Unblock the read loop.
                }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        var packet = await LabWire.ReadAsync(
                            tls, token, LabWire.MaxControlPacketBytes)
                            .WaitAsync(TimeSpan.FromSeconds(25), token);
                        if (passwordMode)
                        {
                            if (!_passwordAccess.IsSessionAuthorized(passwordVerifier, role == LabRole.Controller))
                                throw new AuthenticationException("Password access revoked or downgraded.");
                        }
                        else
                        {
                            var rights = _clientRoster.GetAuthorized(clientPin);
                            if (rights is null || (role == LabRole.Controller && !rights.CanControl))
                                throw new AuthenticationException("Viewer access revoked or downgraded.");
                        }
                        // The keepalive is still authenticated and can never
                        // turn a view-only session into a controller.
                        if (packet.Type == LabMessage.KeepAlive && packet.Payload.Length == 0)
                            continue;
                        if (packet.Type is not (LabMessage.Key or LabMessage.Pointer))
                            throw new InvalidDataException("Unexpected viewer packet.");
                        lock (_guard)
                        {
                            // This is the FINAL input permission check, not a
                            // hint from the viewer or the welcome packet.
                            if (!_running || token.IsCancellationRequested ||
                                _controller != session || !_sessions.Contains(session))
                                continue;
                            if (packet.Type == LabMessage.Key) {
                                var key = LabWire.ParseKey(packet.Payload);
                                LabNativeInput.ApplyKey(_pressedKeys, key.Key, key.Pressed);
                            }
                            else {
                                var pointer = LabWire.ParsePointer(packet.Payload);
                                LabNativeInput.ApplyPointer(_desktop,
                                    pointer.X, pointer.Y, pointer.Buttons,
                                    ref _pressedMouse);
                            }
                        }
                    }
                }
                finally
                {
                    lifetime.Cancel();
                    try { await capture; }
                    catch (Exception) { /* capture cancellation or socket closure */ }
                }
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException
                or InvalidDataException or InvalidOperationException
                or OperationCanceledException or ObjectDisposedException
                or System.Net.Sockets.SocketException or TimeoutException)
            {
                if (!shutdown.IsCancellationRequested)
                    Log("Connection closed: " + ex.Message);
            }
            finally
            {
                if (handshakePermit) _handshakeLimit.Release();
                lock (_guard)
                {
                    if (registered) _sessions.Remove(session);
                    if (_controller == session)
                    {
                        // Release held inputs BEFORE another viewer can gain
                        // control. Existing viewers stay view-only until reconnect.
                        LabNativeInput.ReleaseAll(_pressedKeys, ref _pressedMouse);
                        _controller = null;
                    }
                }
                if (registered) Log("Viewer disconnected; input state released.");
            }
        }
    }

    // Exactly one producer captures/encodes each desktop frame, regardless of
    // the number of authorized viewers. A slow Viewer skips obsolete frames.
    private async Task CaptureFramesShared(LabSharedFrameStore frames, CancellationTokenSource run)
    {
        var token = run.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                bool hasViewer;
                lock (_guard) { hasViewer = _running && _sessions.Count != 0; }
                if (hasViewer)
                {
                    var jpeg = CaptureJpeg(_desktop);
                    if (!token.IsCancellationRequested)
                        frames.Publish(jpeg);
                }
                // Do not capture a private desktop when no one is connected.
                await Task.Delay(hasViewer ? 160 : 350, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && !IsDisposed)
            {
                Log("Shared desktop capture failed; Server stopped: " + ex.Message);
                try
                {
                    BeginInvoke(new Action(() => {
                        // A delayed fault from a previous run must not stop
                        // a newly started Server generation.
                        if (!IsDisposed && ReferenceEquals(_shutdown, run))
                            StopServer();
                    }));
                }
                catch (InvalidOperationException) { }
            }
        }
    }
    private async Task SendFrames(SslStream tls, SemaphoreSlim writeMutex,
                                   LabSharedFrameStore frames,
                                   X509Certificate? clientCertificate, string clientPin,
                                   bool passwordMode, string passwordVerifier,
                                   LabRole role, CancellationToken token)
    {
        long lastSequence = 0;
        while (!token.IsCancellationRequested)
        {
            if (passwordMode)
            {
                if (!_passwordAccess.IsSessionAuthorized(passwordVerifier, role == LabRole.Controller))
                    throw new AuthenticationException("Password session disabled or reconfigured.");
            }
            else
            {
                var rights = _clientRoster.GetAuthorized(clientPin);
                if (!_clientRoster.IsCertificateAllowed(clientCertificate) ||
                    rights is null || (role == LabRole.Controller && !rights.CanControl))
                    throw new AuthenticationException("Viewer authorization disabled or downgraded.");
            }
            // A slow Viewer sends only the newest shared frame; no queue.
            var frame = frames.Latest;
            if (frame is not null && frame.Sequence != lastSequence)
            {
                await LabWire.WriteAsync(tls, writeMutex,
                                         LabMessage.JpegFrame, frame.Jpeg, token)
                    .WaitAsync(TimeSpan.FromSeconds(10), token);
                lastSequence = frame.Sequence;
            }
            await Task.Delay(50, token);
        }
    }

    private static byte[] CaptureJpeg(Rectangle desktop)
    {
        using var bitmap = new Bitmap(desktop.Width, desktop.Height,
                                      PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size);
        var factor = Math.Min(1.0, Math.Min(1600.0 / desktop.Width,
                                           900.0 / desktop.Height));
        using var reduced = new Bitmap(
            Math.Max(1, (int)Math.Round(desktop.Width * factor)),
            Math.Max(1, (int)Math.Round(desktop.Height * factor)));
        using (var graphics = Graphics.FromImage(reduced))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(bitmap, 0, 0, reduced.Width, reduced.Height);
        }
        using var memory = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 65L);
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
        reduced.Save(memory, jpeg, parameters);
        return memory.ToArray();
    }
}

internal static class LabNativeInput
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
    private const uint RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    private const uint MIDDLEDOWN = 0x0020, MIDDLEUP = 0x0040;

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan,
        uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx,
        uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    internal static void ApplyKey(HashSet<ushort> pressed, ushort vk, bool down)
    {
        if (vk is < 1 or > 254) return;
        if (down && pressed.Add(vk))
            keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        else if (!down && pressed.Remove(vk))
            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    internal static void ApplyPointer(Rectangle desktop, ushort x, ushort y,
                                       byte buttons, ref byte pressed)
    {
        int absoluteX = desktop.Left +
            (int)Math.Round(x / 65535.0 * Math.Max(0, desktop.Width - 1));
        int absoluteY = desktop.Top +
            (int)Math.Round(y / 65535.0 * Math.Max(0, desktop.Height - 1));
        SetCursorPos(absoluteX, absoluteY);
        uint flags = 0;
        if ((buttons & 1) != (pressed & 1)) flags |= (buttons & 1) != 0 ? LEFTDOWN : LEFTUP;
        if ((buttons & 2) != (pressed & 2)) flags |= (buttons & 2) != 0 ? RIGHTDOWN : RIGHTUP;
        if ((buttons & 4) != (pressed & 4)) flags |= (buttons & 4) != 0 ? MIDDLEDOWN : MIDDLEUP;
        if (flags != 0) mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        pressed = buttons;
    }

    internal static void ReleaseAll(HashSet<ushort> pressed, ref byte pressedMouse)
    {
        foreach (var key in pressed)
            keybd_event((byte)key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        pressed.Clear();
        uint flags = 0;
        if ((pressedMouse & 1) != 0) flags |= LEFTUP;
        if ((pressedMouse & 2) != 0) flags |= RIGHTUP;
        if ((pressedMouse & 4) != 0) flags |= MIDDLEUP;
        if (flags != 0) mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        pressedMouse = 0;
    }
}
