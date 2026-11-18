// SPDX-License-Identifier: GPL-2.0-or-later
// Local per-user Viewer preferences. Never stores passwords, certificates or trust pins.
using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed record LabViewerConnectionOptions(
    bool ViewOnly = false, bool FullScreen = false, bool AutoReconnect = false,
    int JpegQuality = 65, int FramesPerSecond = 6, bool SwapMouseButtons = false)
{
    internal static string SettingsFile =>
        Path.Combine(LabCertificates.ConfigDirectory, "viewer-connection-options.json");

    internal LabViewerConnectionOptions Validate()
    {
        LabWire.CaptureSettings(checked((byte)JpegQuality), checked((byte)FramesPerSecond));
        return this;
    }

    // Original TightVNC Viewer supports swapping only the two primary mouse buttons.
    // Middle-button state must remain unchanged; never alter remote click authority.
    internal byte MapMouseButtons(byte buttons)
    {
        if ((buttons & ~7) != 0) throw new InvalidDataException("Invalid mouse buttons.");
        if (!SwapMouseButtons) return buttons;
        return (byte)((buttons & 4) | ((buttons & 1) << 1) | ((buttons & 2) >> 1));
    }

    internal bool MayAutoReconnect(bool authenticatedWithCertificate, bool manualDisconnect) =>
        AutoReconnect && authenticatedWithCertificate && !manualDisconnect;

    internal static LabViewerConnectionOptions Load()
    {
        if (!File.Exists(SettingsFile)) return new();
        try
        {
            return (JsonSerializer.Deserialize<LabViewerConnectionOptions>(
                File.ReadAllText(SettingsFile)) ?? new()).Validate();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or
            IOException or UnauthorizedAccessException or OverflowException)
        { return new(); }
    }

    internal void Save()
    {
        Validate();
        Directory.CreateDirectory(LabCertificates.ConfigDirectory);
        string staging = SettingsFile + ".tmp";
        File.WriteAllText(staging, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(staging, SettingsFile, overwrite: true);
    }
}

internal static class LabViewerConnectionOptionsDialog
{
    internal static LabViewerConnectionOptions? Prompt(IWin32Window owner,
        LabViewerConnectionOptions current)
    {
        using var form = new Form {
            Text = "TJ.RemoteAccess - Connection Options",
            Width = 555, Height = 405, MinimumSize = new Size(505, 390),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(14),
            RowCount = 8, ColumnCount = 2
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var viewOnly = new CheckBox { Text = "View only (no input)", AutoSize = true, Checked = current.ViewOnly };
        var fullScreen = new CheckBox { Text = "Full screen on connect", AutoSize = true, Checked = current.FullScreen };
        var autoReconnect = new CheckBox {
            Text = "Reconnect certificate profiles automatically", AutoSize = true,
            Checked = current.AutoReconnect
        };
        var quality = new NumericUpDown {
            Minimum = 25, Maximum = 90, Value = current.JpegQuality,
            DecimalPlaces = 0, Width = 80
        };
        var fps = new NumericUpDown {
            Minimum = 2, Maximum = 20, Value = current.FramesPerSecond,
            DecimalPlaces = 0, Width = 80
        };
        var swapMouse = new CheckBox { Text = "Swap left / right mouse buttons", AutoSize = true,
            Checked = current.SwapMouseButtons };
        AddRow(0, "Input mode", viewOnly);
        AddRow(1, "Display", fullScreen);
        AddRow(2, "Recovery", autoReconnect);
        AddRow(3, "JPEG quality (25–90)", quality);
        AddRow(4, "Max FPS (2–20)", fps);
        AddRow(5, "Mouse", swapMouse);
        var message = new Label {
            Dock = DockStyle.Fill,
            Text = "JPEG/FPS settings are shared by all viewers and applied only " +
                   "by the active Controller. View-only requires reconnect. " +
                   "Auto-reconnect never stores or reuses passwords. " +
                   "Ctrl+Alt+Del / Winlogon / UAC are not supported.",
            AutoSize = false
        };
        layout.Controls.Add(message, 0, 6);
        layout.SetColumnSpan(message, 2);
        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 7);
        layout.SetColumnSpan(buttons, 2);
        form.Controls.Add(layout);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        if (form.ShowDialog(owner) != DialogResult.OK) return null;
        return new LabViewerConnectionOptions(viewOnly.Checked, fullScreen.Checked,
            autoReconnect.Checked, (int)quality.Value, (int)fps.Value, swapMouse.Checked).Validate();

        void AddRow(int row, string label, Control ctrl)
        {
            layout.Controls.Add(new Label {
                Text = label, AutoSize = true, Anchor = AnchorStyles.Left
            }, 0, row);
            layout.Controls.Add(ctrl, 1, row);
        }
    }
}
