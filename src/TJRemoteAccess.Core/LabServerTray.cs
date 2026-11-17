// SPDX-License-Identifier: GPL-2.0-or-later
// Windows tray UI for the independent, interactive TJ.RemoteAccess Server.
// Runs on the WinForms UI thread. Closing the window hides it; the explicit
// Exit menu item tears down listeners, TLS sessions, and pressed input state.
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class ServerTrayInstance
{
    // Windows Local namespace isolates RDP/console sessions. A second launch
    // signals the first instance to restore its window instead of opening
    // another tray icon or competing for the TLS listening port.
    internal const string MutexName = @"Local\TJ.RemoteAccess.Preview.Server.Instance";
    internal const string RestoreEventName = @"Local\TJ.RemoteAccess.Preview.Server.Restore";
}

internal readonly record struct ServerTraySnapshot(bool Listening, int Viewers, bool Controller,
                                                    bool ServiceSupervision = false)
{
    internal string Status => !Listening ? "Stopped" :
        Viewers > 0 ? "Connected" : "Listening";

    internal string Tooltip => !Listening ? "TJ.RemoteAccess Server - stopped" :
        $"TJ.RemoteAccess Server - {Viewers}/3 viewers";

    internal string ViewerSummary => $"Viewers: {Math.Clamp(Viewers, 0, 3)}/3";

    internal string ControllerSummary =>
        Controller && Listening ? "Controller: connected" : "Controller: none";

    internal bool RequiresExitConfirmation => Listening && Viewers > 0;

    internal static bool ShouldHideOnClose(CloseReason reason, bool explicitlyExiting) =>
        !explicitlyExiting && reason == CloseReason.UserClosing;
}

internal sealed class LabServerTray : IDisposable
{
    private readonly NotifyIcon _notify;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _viewers = new() { Enabled = false };
    private readonly ToolStripMenuItem _controller = new() { Enabled = false };
    private readonly ToolStripMenuItem _start;
    private readonly ToolStripMenuItem _stop;
    private readonly ToolStripMenuItem _serviceSupervision;
    private readonly Icon _inactiveIcon;
    private readonly Icon _listeningIcon;
    private readonly Icon _connectedIcon;
    private bool _exiting;
    private bool _hiddenTipShown;
    private ServerTraySnapshot? _snapshot;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    internal LabServerTray(Action open, Action start, Action stop,
                           Action clients, Action password,
                           Action toggleServiceSupervision, Action exit)
    {
        _inactiveIcon = DrawIcon(Color.FromArgb(108, 118, 128));
        _listeningIcon = DrawIcon(Color.FromArgb(37, 130, 204));
        _connectedIcon = DrawIcon(Color.FromArgb(33, 151, 91));

        _menu.Items.Add(_status);
        _menu.Items.Add(_viewers);
        _menu.Items.Add(_controller);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Open Server", null, (_, _) => open());
        _start = new ToolStripMenuItem("Start listening", null, (_, _) => start());
        _stop = new ToolStripMenuItem("Stop listening", null, (_, _) => stop());
        _menu.Items.Add(_start);
        _menu.Items.Add(_stop);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Access settings...", null, (_, _) => clients());
        _serviceSupervision = new ToolStripMenuItem("Allow Windows service supervision",
            null, (_, _) => toggleServiceSupervision());
        _menu.Items.Add(_serviceSupervision);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit TJ.RemoteAccess", null, (_, _) => {
            if (_snapshot is { RequiresExitConfirmation: true } &&
                MessageBox.Show("Connected viewers will be disconnected. Exit Server?",
                    "TJ.RemoteAccess Server", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            _exiting = true;
            exit();
        });

        _notify = new NotifyIcon {
            Icon = _inactiveIcon,
            ContextMenuStrip = _menu,
            Text = "TJ.RemoteAccess Server - stopped",
            Visible = true
        };
        _notify.MouseDoubleClick += (_, e) => {
            if (e.Button == MouseButtons.Left) open();
        };
        Update(new ServerTraySnapshot(false, 0, false));
    }

    internal bool ShouldHideOnClose(CloseReason reason) =>
        ServerTraySnapshot.ShouldHideOnClose(reason, _exiting);

    internal void NotifyHidden()
    {
        if (_hiddenTipShown) return;
        _hiddenTipShown = true;
        _notify.ShowBalloonTip(2500, "TJ.RemoteAccess Server",
            "Still running in the notification area. Right-click to manage; use Exit to stop.",
            ToolTipIcon.Info);
    }

    internal void Update(ServerTraySnapshot state)
    {
        if (_snapshot == state) return;
        _snapshot = state;
        _status.Text = "Status: " + state.Status;
        _viewers.Text = state.ViewerSummary;
        _controller.Text = state.ControllerSummary;
        _start.Enabled = !state.Listening;
        _stop.Enabled = state.Listening;
        _serviceSupervision.Checked = state.ServiceSupervision;
        _notify.Text = state.Tooltip;
        _notify.Icon = !state.Listening ? _inactiveIcon :
            state.Viewers == 0 ? _listeningIcon : _connectedIcon;
    }

    private static Icon DrawIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            graphics.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 11,
                                      FontStyle.Bold, GraphicsUnit.Pixel);
            using var ink = new SolidBrush(Color.White);
            using var format = new StringFormat {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString("TJ", font, ink,
                new RectangleF(2, 2, 28, 28), format);
        }
        IntPtr handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        _notify.Visible = false;
        _notify.Icon = null;
        _notify.Dispose();
        _menu.Dispose();
        _inactiveIcon.Dispose();
        _listeningIcon.Dispose();
        _connectedIcon.Dispose();
    }
}
