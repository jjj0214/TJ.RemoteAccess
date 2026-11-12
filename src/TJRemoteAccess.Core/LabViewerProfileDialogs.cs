// SPDX-License-Identifier: GPL-2.0-or-later
// Forms for managing pinned lab server profiles. Does not reveal private keys.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabViewerProfileDialogs
{
    internal static LabViewerProfile? Import(IWin32Window owner,
                                              LabViewerProfileStore store)
    {
        using var pick = new OpenFileDialog
        {
            Title = "Select the client-transfer.pfx exported by the server",
            Filter = "Paired client certificate (*.pfx)|*.pfx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (pick.ShowDialog(owner) != DialogResult.OK) return null;
        string accompanying = Path.Combine(Path.GetDirectoryName(pick.FileName)!, "viewer.json");
        if (!File.Exists(accompanying))
        {
            MessageBox.Show(owner, "viewer.json must be in the same folder as the PFX.\n" +
                "Copy both files from the server Pairing folder.",
                "Pairing bundle incomplete", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
        using var form = BuildForm("Add paired TJRA server", "Import", out var grid);
        var name = AddRow(grid, 0, "Profile name", "");
        var host = AddRow(grid, 1, "Server host/IP (optional)", "");
        var password = AddRow(grid, 2, "PFX password", "");
        password.UseSystemPasswordChar = true;
        var tip = new Label {
            AutoSize = false, Dock = DockStyle.Fill, Height = 48,
            Text = "Server SHA-256 pin and client certificate will be verified.\n" +
                   "Existing profiles and certificates are preserved."
        };
        grid.Controls.Add(tip, 0, 3);
        grid.SetColumnSpan(tip, 2);
        if (form.ShowDialog(owner) != DialogResult.OK) return null;
        try
        {
            if (password.TextLength == 0)
                throw new InvalidDataException("PFX password cannot be empty.");
            return store.ImportPairing(pick.FileName, password.Text,
                name.Text.Trim(), host.Text.Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or InvalidDataException or
                                  System.Security.Cryptography.CryptographicException
                                  or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(owner, e.Message, "Pairing rejected",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally { password.Clear(); }
    }

    internal static LabViewerProfile? Edit(IWin32Window owner,
                                            LabViewerProfileStore store,
                                            LabViewerProfile profile)
    {
        using var form = BuildForm("Edit paired server endpoint", "Save", out var grid);
        var name = AddRow(grid, 0, "Profile name", profile.Name);
        var host = AddRow(grid, 1, "Server host / IP", profile.Host);
        var port = AddRow(grid, 2, "TCP port", profile.Port.ToString());
        var tip = new Label {
            Dock = DockStyle.Fill,
            Text = "Only display name and host/port can change.\n" +
                   "The pinned server certificate and client private key are not replaced."
        };
        grid.Controls.Add(tip, 0, 3);
        grid.SetColumnSpan(tip, 2);
        if (form.ShowDialog(owner) != DialogResult.OK) return null;
        try
        {
            if (!int.TryParse(port.Text, out int number))
                throw new InvalidDataException("Port must be a number.");
            return store.Edit(profile.Id, name.Text, host.Text, number);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                  or InvalidDataException or InvalidOperationException
                                  or KeyNotFoundException)
        {
            MessageBox.Show(owner, e.Message, "Unable to update profile",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }
    }

    internal static void ShowIdentity(IWin32Window owner, LabViewerProfile profile)
    {
        MessageBox.Show(owner,
            $"Profile: {profile.Name}\nHost: {profile.Host}:{profile.Port}\n\n" +
            $"Server certificate (SHA-256):\n{profile.ServerSha256}\n\n" +
            $"Local client certificate thumbprint:\n{profile.ClientCertificateThumbprint}\n\n" +
            "Trust pins cannot be edited here. Import a pairing bundle " +
            "to add another trusted server.",
            "Pinned server identity", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static Form BuildForm(string title, string action,
                                   out TableLayoutPanel grid)
    {
        var form = new Form
        {
            Text = title, Width = 520, Height = 285,
            MinimumSize = new Size(480, 275), StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            MinimizeBox = false, ShowInTaskbar = false
        };
        grid = new TableLayoutPanel {
            Dock = DockStyle.Fill, Padding = new Padding(14),
            ColumnCount = 2, RowCount = 5
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; ++i)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 39));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var accept = new Button {
            Text = action, DialogResult = DialogResult.OK, AutoSize = true
        };
        var cancel = new Button {
            Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true
        };
        buttons.Controls.Add(accept);
        buttons.Controls.Add(cancel);
        grid.Controls.Add(buttons, 0, 4);
        grid.SetColumnSpan(buttons, 2);
        form.AcceptButton = accept;
        form.CancelButton = cancel;
        form.Controls.Add(grid);
        return form;
    }

    private static TextBox AddRow(TableLayoutPanel grid, int row,
                                   string title, string value)
    {
        var label = new Label {
            Text = title, AutoSize = true, Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var input = new TextBox {
            Text = value, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 2, 4)
        };
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(input, 1, row);
        return input;
    }
}
