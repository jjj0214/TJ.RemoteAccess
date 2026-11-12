// SPDX-License-Identifier: GPL-2.0-or-later
// User-triggered certificate bundle generation on the local interactive Server.
using System.Drawing;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabClientPairingDialog
{
    internal static bool Create(IWin32Window owner, LabServerClientRoster roster,
                                LabServerSettings settings, X509Certificate2 certificate)
    {
        using var dialog = new Form {
            Text = "Create Viewer certificates (local Server)",
            Width = 570, Height = 315, MinimumSize = new Size(530, 290),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, ShowInTaskbar = false
        };
        var table = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(15),
            ColumnCount = 2, RowCount = 6
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 4; i++) table.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var prefix = new TextBox { Text = "Viewer", Dock = DockStyle.Fill };
        var count = new NumericUpDown { Minimum = 1, Maximum = 3, Value = 2, Width = 80 };
        var firstNumber = new NumericUpDown { Minimum = 1, Maximum = 98, Value = 2, Width = 80 };
        var control = new CheckBox {
            Text = "Allow keyboard and mouse control",
            Checked = false, AutoSize = true
        };
        AddRow("Viewer name / prefix", prefix, 0);
        AddRow("Number of Viewers", count, 1);
        AddRow("Starting number", firstNumber, 2);
        AddRow("Control permission", control, 3);
        var description = new Label {
            Text = "One unique TLS client certificate and encrypted PFX will be created per Viewer.\n" +
                   "Existing Viewer certificates and allowed clients will not be changed.",
            Dock = DockStyle.Fill
        };
        table.Controls.Add(description, 0, 4);
        table.SetColumnSpan(description, 2);
        var buttons = new FlowLayoutPanel {
            FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill
        };
        var create = new Button { Text = "Generate", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(create); buttons.Controls.Add(cancel);
        table.Controls.Add(buttons, 0, 5);
        table.SetColumnSpan(buttons, 2);
        dialog.AcceptButton = create;
        dialog.CancelButton = cancel;
        dialog.Controls.Add(table);

        void AddRow(string label, Control input, int row)
        {
            table.Controls.Add(new Label {
                Text = label, Anchor = AnchorStyles.Left, AutoSize = true
            }, 0, row);
            table.Controls.Add(input, 1, row);
        }

        if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
        int number = (int)count.Value;
        string name = prefix.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 70 || name.Any(char.IsControl)) {
            MessageBox.Show(owner, "Viewer name must be 1-70 printable characters.",
                "Invalid name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        var generated = new List<LabClientPairingResult>();
        try
        {
            for (int i = 0; i < number; i++)
            {
                string nextName = $"{name}-{(int)firstNumber.Value + i}";
                generated.Add(LabClientPairing.Create(nextName, settings, certificate,
                                                      roster, canControl: control.Checked));
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or
                                   UnauthorizedAccessException or InvalidOperationException or
                                   System.Security.Cryptography.CryptographicException)
        {
            MessageBox.Show(owner,
                $"Generated {generated.Count} of {number} certificates.\n" +
                "Existing generated entries were NOT rolled back.\n\n" + ex.Message,
                "Viewer generation stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        if (generated.Count == 0) return false;
        var secretLines = generated.Select(p =>
            $"Viewer: {p.Client.Name}\r\n" +
            $"Folder: {p.Folder}\r\n" +
            $"Client ID: {p.Client.Id}\r\n" +
            $"PFX password: {p.PfxPassword}\r\n" +
            "---------------------------------------------");
        string result = string.Join("\r\n", secretLines);
        using var credentials = new Form {
            Text = "Viewer pairing bundles — passwords shown ONCE",
            StartPosition = FormStartPosition.CenterParent,
            Width = 780, Height = 470, MinimumSize = new Size(600, 350)
        };
        var display = new TextBox {
            Text = result, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
            WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 10)
        };
        var banner = new Label {
            Dock = DockStyle.Top, Height = 65, Padding = new Padding(10),
            Text = "Keep each PFX and password confidential. Copy the pairing folder to " +
                   "the intended Viewer PC and import via Viewer > Add server. " +
                   "Passwords are NOT saved and cannot be shown again."
        };
        var close = new Button { Text = "I have saved the pairing details", Dock = DockStyle.Bottom, Height = 43 };
        close.Click += (_, _) => credentials.Close();
        credentials.Controls.Add(display);
        credentials.Controls.Add(banner);
        credentials.Controls.Add(close);
        credentials.ShowDialog(owner);
        display.Clear();
        return true;
    }
}
