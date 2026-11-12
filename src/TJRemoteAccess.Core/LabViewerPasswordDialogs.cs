// SPDX-License-Identifier: GPL-2.0-or-later
// Password-mode Viewer pairing. The server public identity is always pinned;
// no password is persisted or included in viewer-profiles.json.
using System.Drawing;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabViewerPasswordDialogs
{
    internal static LabViewerProfile? Add(IWin32Window owner, LabViewerProfileStore store)
    {
        using var dialog = new Form {
            Text = "Add password server — pinned TLS (LAB ONLY)",
            Width = 710, Height = 350, MinimumSize = new Size(650, 330),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var table = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(12),
            ColumnCount = 2, RowCount = 6
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 41));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        var name = new TextBox { Text = "Password server", Dock = DockStyle.Fill };
        var host = new TextBox { Text = "192.168.0.100", Dock = DockStyle.Fill };
        var port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = 15445,
                                        Dock = DockStyle.Left, Width = 105 };
        var pin = new TextBox { Dock = DockStyle.Fill, CharacterCasing = CharacterCasing.Upper };
        Row("Profile name", name, 0);
        Row("Server IP / host", host, 1);
        Row("TLS password port", port, 2);

        var pinControls = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        pinControls.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pinControls.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        var load = new Button { Text = "Load .cer", Dock = DockStyle.Fill };
        pinControls.Controls.Add(pin, 0, 0);
        pinControls.Controls.Add(load, 1, 0);
        table.Controls.Add(new Label {
            Text = "Server SHA-256 pin", Anchor = AnchorStyles.Left, AutoSize = true
        }, 0, 3);
        table.Controls.Add(pinControls, 1, 3);
        load.Click += (_, _) => {
            using var picker = new OpenFileDialog {
                Title = "Select the authentic server-public.cer from the Server PC",
                Filter = "Server public certificate (*.cer)|*.cer|Certificate (*.crt)|*.crt",
                CheckFileExists = true
            };
            if (picker.ShowDialog(dialog) != DialogResult.OK) return;
            try {
                using var cert = X509CertificateLoader.LoadCertificate(
                    File.ReadAllBytes(picker.FileName));
                string hash = LabCertificates.Hash(cert);
                if (!LabCertificates.ValidatePinned(cert, hash, LabCertificates.ServerEku))
                    throw new CryptographicException("Not a current TJRA Lab server TLS certificate.");
                pin.Text = hash;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException
                                      or UnauthorizedAccessException)
            {
                MessageBox.Show(dialog, ex.Message, "Server identity rejected");
            }
        };
        var warning = new Label {
            Dock = DockStyle.Fill,
            Text = "TLS encryption is ALWAYS required. Verify the 64-character server SHA-256\n" +
                   "on the Server PC through a trusted channel. No TOFU or unpinned TLS is allowed.\n" +
                   "The password is entered at CONNECT time, not stored in this profile."
        };
        table.Controls.Add(warning, 0, 4);
        table.SetColumnSpan(warning, 2);
        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var accept = new Button { Text = "Add password server", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        table.Controls.Add(buttons, 0, 5);
        table.SetColumnSpan(buttons, 2);
        dialog.AcceptButton = accept;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(table);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        try
        {
            return store.RegisterPasswordProfile(name.Text, host.Text, (int)port.Value, pin.Text);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                  or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(owner, ex.Message, "Password server pairing rejected",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        void Row(string label, Control value, int row)
        {
            table.Controls.Add(new Label {
                Text = label, Anchor = AnchorStyles.Left, AutoSize = true
            }, 0, row);
            table.Controls.Add(value, 1, row);
        }
    }

    internal static string? PromptPassword(IWin32Window owner)
    {
        using var form = new Form {
            Text = "Password authentication — encrypted pinned TLS",
            Width = 490, Height = 205,
            MinimumSize = new Size(445, 190),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false
        };
        var field = new TextBox {
            UseSystemPasswordChar = true, Width = 426,
            Location = new Point(16, 57)
        };
        var prompt = new Label {
            Text = "Enter the Server password (not saved; verified inside pinned TLS).",
            Location = new Point(16, 17), AutoSize = true
        };
        var connect = new Button {
            Text = "Connect", DialogResult = DialogResult.OK,
            Location = new Point(260, 109), Width = 90
        };
        var cancel = new Button {
            Text = "Cancel", DialogResult = DialogResult.Cancel,
            Location = new Point(355, 109), Width = 90
        };
        form.Controls.Add(prompt);
        form.Controls.Add(field);
        form.Controls.Add(connect);
        form.Controls.Add(cancel);
        form.AcceptButton = connect;
        form.CancelButton = cancel;
        var answer = form.ShowDialog(owner) == DialogResult.OK ? field.Text : null;
        field.Clear();
        return answer;
    }
}
