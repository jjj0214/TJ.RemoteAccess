// SPDX-License-Identifier: GPL-2.0-or-later
// User-facing IP + password shortcut. No Viewer certificate or manual
// certificate import is required; TLS is still mandatory.
// Password is collected in memory and only transmitted AFTER a separately
// confirmed first-use Server SHA-256 identity.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabViewerQuickPasswordDialog
{
    internal static LabQuickPasswordRequest? Prompt(IWin32Window owner, string? defaultHost)
    {
        using var dialog = new Form {
            Text = "Quick connect - IP + password (encrypted TLS)",
            Width = 530, Height = 285, MinimumSize = new Size(490, 265),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false
        };
        var table = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(14),
            ColumnCount = 2, RowCount = 5
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));

        var host = new TextBox {
            Text = string.IsNullOrWhiteSpace(defaultHost) ? "" : defaultHost,
            Dock = DockStyle.Fill
        };
        var port = new NumericUpDown {
            Minimum = 1024, Maximum = 65535, Value = LabCertificates.DefaultPort + 1,
            Width = 110
        };
        var password = new TextBox { UseSystemPasswordChar = true, Dock = DockStyle.Fill };
        AddRow("Server IP / host", host, 0);
        AddRow("Password TLS port", port, 1);
        AddRow("Password", password, 2);
        var info = new Label {
            Text = "No Viewer certificate needed. TLS encryption is mandatory.\n" +
                   "First connection requires confirming the Server fingerprint.\n" +
                   "The password is never saved in Viewer profiles.",
            AutoSize = true, Dock = DockStyle.Fill
        };
        table.Controls.Add(info, 0, 3);
        table.SetColumnSpan(info, 2);
        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var connect = new Button { Text = "Connect", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(connect);
        buttons.Controls.Add(cancel);
        table.Controls.Add(buttons, 0, 4);
        table.SetColumnSpan(buttons, 2);
        dialog.Controls.Add(table);
        dialog.AcceptButton = connect;
        dialog.CancelButton = cancel;
        host.Select();
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        string resultPassword = password.Text;
        password.Clear();
        return new LabQuickPasswordRequest(host.Text.Trim(), (int)port.Value, resultPassword);

        void AddRow(string title, Control control, int index)
        {
            table.Controls.Add(new Label {
                Text = title, AutoSize = true, Anchor = AnchorStyles.Left
            }, 0, index);
            table.Controls.Add(control, 1, index);
        }
    }

    internal static bool ConfirmFirstUse(IWin32Window owner, string host, int port,
                                          string fullSha256)
    {
        // The TLS handshake is encrypted but not yet trusted. Nothing
        // password-related has been sent at this point.
        string grouped = string.Join("  ", Enumerable.Range(0, 8)
            .Select(i => fullSha256.Substring(i * 8, 8)));
        var message = $"First connection to {host}:{port}.\n\n" +
            "Verify this SHA-256 fingerprint on the Server PC:\n\n" +
            grouped + "\n\n" +
            "On that PC: Server tray > Password access > Copy Server pin.\n\n" +
            "An impostor on this network could display a DIFFERENT fingerprint. " +
            "Do not proceed unless you can verify the match.\n" +
            "Only after you choose Yes will your password be transmitted over TLS.\n\n" +
            "Trust and save this server identity?";
        return MessageBox.Show(owner, message, "Verify Server identity before sending password",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
}
