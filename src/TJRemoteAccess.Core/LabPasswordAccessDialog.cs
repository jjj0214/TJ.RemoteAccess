// SPDX-License-Identifier: GPL-2.0-or-later
// On-device UI: configure optional TLS-protected password authentication.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabPasswordAccessDialog
{
    internal static void Show(IWin32Window owner, LabPasswordAccess access,
                              LabServerSettings settings,
                              System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
    {
        using var form = new Form {
            Text = "Password access (TLS required, separate port)",
            Width = 680, Height = 390, MinimumSize = new Size(590, 350),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var settingsNow = access.Current();
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(13),
            ColumnCount = 2, RowCount = 7
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 175));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        var password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        var again = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        var controller = new CheckBox {
            Text = "Allow remote keyboard / mouse (default: View only)",
            Checked = settingsNow.CanControl, Dock = DockStyle.Fill
        };
        var pin = LabCertificates.Hash(cert);
        var pinBox = new TextBox { ReadOnly = true, Text = pin, Dock = DockStyle.Fill };
        Row("New password", password, 0);
        Row("Confirm password", again, 1);
        Row("Password access", new Label {
            Text = settingsNow.Enabled ? "Enabled" : "Disabled",
            AutoSize = true, Anchor = AnchorStyles.Left
        }, 2);
        Row("Password privileges", controller, 3);
        Row("Server TLS pin", pinBox, 4);
        var info = new Label {
            Dock = DockStyle.Fill, Text =
                $"Password TLS port: {settings.Port + 1} (certificate port: {settings.Port}).\n" +
                "Use Viewer > Add password server and copy this SHA-256 pin using a trusted channel.\n" +
                "No password is stored in the Viewer; no raw/unencrypted password listener exists."
        };
        layout.Controls.Add(info, 0, 5);
        layout.SetColumnSpan(info, 2);
        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        void ActionButton(string label, Action action)
        {
            var button = new Button { Text = label, AutoSize = true };
            button.Click += (_, _) => action();
            buttons.Controls.Add(button);
        }
        ActionButton("Close", () => form.Close());
        ActionButton("Copy Server pin", () => Clipboard.SetText(pin));
        ActionButton("Disable password", () => {
            if (MessageBox.Show(form,
                    "Disable password authentication and revoke all current password sessions?",
                    "Disable password access", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                access.Disable();
                MessageBox.Show(form, "Password access disabled. Restart Server to close its listener.",
                    "Password disabled");
                form.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                MessageBox.Show(form, ex.Message, "Unable to disable password");
            }
        });
        ActionButton("Set / change password", () => {
            try
            {
                if (password.Text != again.Text)
                    throw new InvalidDataException("Passwords do not match.");
                access.Configure(password.Text, controller.Checked);
                MessageBox.Show(form,
                    "Password verifier saved; plaintext password was NOT stored.\n" +
                    "Stop and Start the Lab Server to activate the password listener.",
                    "Password access configured");
                form.Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or InvalidOperationException)
            {
                MessageBox.Show(form, ex.Message, "Invalid password settings",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { password.Clear(); again.Clear(); }
        });
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);
        form.Controls.Add(layout);
        form.ShowDialog(owner);
        password.Clear();
        again.Clear();

        void Row(string label, Control ctrl, int row)
        {
            layout.Controls.Add(new Label { Text = label, AutoSize = true,
                Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(ctrl, 1, row);
        }
    }
}
