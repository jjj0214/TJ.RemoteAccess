// SPDX-License-Identifier: GPL-2.0-or-later
// Product Viewer controls inspired by the familiar TightVNC toolbar concepts.
// All features operate on authenticated TJRA/TLS sessions, never raw VNC.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed partial class LabViewerForm
{
#if TJRA_STANDALONE_PREVIEW
    private void SetZoom()
    {
        if (_zoom.SelectedIndex <= 0)
        {
            _viewport.AutoScroll = false;
            _picture.Dock = DockStyle.Fill;
            _picture.SizeMode = PictureBoxSizeMode.Zoom;
            return;
        }
        double factor = _zoom.SelectedIndex switch {
            1 => .50, 2 => .75, 3 => 1.0, 4 => 1.25,
            5 => 1.50, 6 => 2.0, _ => 1.0
        };
        _picture.Dock = DockStyle.None;
        _viewport.AutoScroll = true;
        _picture.SizeMode = PictureBoxSizeMode.StretchImage;
        _picture.Location = Point.Empty;
        _picture.Size = new Size(
            Math.Max(1, (int)Math.Round(_desktopWidth * factor)),
            Math.Max(1, (int)Math.Round(_desktopHeight * factor)));
    }

    private void SetPaused(bool paused)
    {
        _paused = paused && _connected;
        _pause.Enabled = _connected && !_paused;
        _refresh.Enabled = _connected;
        _status.Text = _paused
            ? "Screen paused locally (TLS stays connected). Select Resume / refresh."
            : _connected ? "Live screen resumed; waiting for next frame." : "Disconnected.";
    }

    private void ToggleFullScreen()
    {
        if (!_isFullScreen)
        {
            _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _isFullScreen = true;
            _fullScreen.Text = "Exit full screen";
        }
        else
        {
            WindowState = FormWindowState.Normal;
            FormBorderStyle = FormBorderStyle.Sizable;
            if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
                Bounds = _restoreBounds;
            _isFullScreen = false;
            _fullScreen.Text = "Full screen";
        }
    }

    private void ShowSpecialKeys()
    {
        var menu = new ContextMenuStrip();
        void Chord(string title, params Keys[] chord)
        {
            var item = menu.Items.Add(title);
            item.Enabled = CanControl;
            item.Click += async (_, _) => await SendChordAsync(chord);
        }
        Chord("Ctrl + Esc (Start menu)", Keys.ControlKey, Keys.Escape);
        Chord("Alt + Tab", Keys.Menu, Keys.Tab);
        Chord("Win + D (desktop)", Keys.LWin, Keys.D);
        Chord("Ctrl + Alt + End (normal desktop)", Keys.ControlKey, Keys.Menu, Keys.End);
        var secure = menu.Items.Add("Ctrl + Alt + Del (secure desktop unavailable)");
        secure.Enabled = false; // No Winlogon/UAC injection or privilege escalation.
        menu.Closed += (_, _) => menu.Dispose();
        menu.Show(_keys, new Point(0, _keys.Height));
    }

    private async Task SendChordAsync(Keys[] chord)
    {
        if (!CanControl || chord.Length == 0) return;
        var pushed = new List<ushort>();
        try
        {
            foreach (var key in chord)
            {
                if (!CanControl) break;
                var vk = (ushort)key;
                if (vk is < 1 or > 254) continue;
                await SendAsync(LabMessage.Key, LabWire.Key(vk, true));
                pushed.Add(vk);
            }
        }
        finally
        {
            pushed.Reverse();
            foreach (var vk in pushed)
                await SendAsync(LabMessage.Key, LabWire.Key(vk, false));
        }
    }

    private void ShowConnectionOptions()
    {
        using var form = new Form {
            Text = "TJ.RemoteAccess - Connection options", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            MinimizeBox = false, Width = 510, Height = 285
        };
        var layout = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(15), AutoScroll = true
        };
        var viewOnly = new CheckBox {
            Text = "View only (never send remote mouse / keyboard)",
            AutoSize = true, Checked = _viewOnly.Checked
        };
        var fit = new CheckBox {
            Text = "Fit remote desktop to window (otherwise use selected zoom)",
            AutoSize = true, Checked = _zoom.SelectedIndex == 0
        };
        var info = new Label {
            Width = 440, Height = 70,
            Text = "Secure connection: TLS 1.2/1.3 with a pinned Server identity.\n" +
                   "Image compression is shared by all viewers. Changing JPEG quality or\n" +
                   "color depth during an active shared session is not supported."
        };
        var save = new Button { Text = "Apply", AutoSize = true };
        save.Click += (_, _) => {
            if (fit.Checked) _zoom.SelectedIndex = 0;
            else if (_zoom.SelectedIndex == 0) _zoom.SelectedIndex = 3;
            _viewOnly.Checked = viewOnly.Checked;
            form.Close();
        };
        layout.Controls.Add(viewOnly);
        layout.Controls.Add(fit);
        layout.Controls.Add(info);
        layout.Controls.Add(save);
        form.Controls.Add(layout);
        form.ShowDialog(this);
    }

    private async Task SendFileFromViewerAsync()
    {
        if (!CanControl || _fileTransfer is null) return;
        using var chooser = new OpenFileDialog {
            Title = "Send a file to the connected Server (Server owner must approve)",
            CheckFileExists = true, Multiselect = false
        };
        if (chooser.ShowDialog(this) != DialogResult.OK) return;
        try { await _fileTransfer.OfferFileAsync(chooser.FileName); }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowStatus("File send failed: " + ex.Message);
        }
    }
#endif
}
