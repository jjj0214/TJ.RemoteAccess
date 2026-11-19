// SPDX-License-Identifier: GPL-2.0-or-later
// Physically present operator UI. Authorization changes take effect without a
// Server restart; connected viewers revalidate at least once per screen frame.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerClientAccessForm : Form
{
    private readonly LabServerClientRoster _roster;
    private readonly LabServerSettings _settings;
    private readonly System.Security.Cryptography.X509Certificates.X509Certificate2 _serverCertificate;
    private readonly ListView _entries = new() {
        Dock = DockStyle.Fill, FullRowSelect = true, HideSelection = false,
        MultiSelect = false, View = View.Details
    };
    private readonly Label _help = new() {
        Dock = DockStyle.Top, Height = 54,
        Padding = new Padding(10), Text =
            "Viewer identities are pinned by SHA-256. To pair a NEW PC, use " +
            "TJ.RemoteAccess.Lab.Setup.exe pair-client \"Viewer name\" on this Server. " +
            "Disabling a client also disconnects its existing sessions."
    };

    internal LabServerClientAccessForm(LabServerClientRoster roster, LabServerSettings settings,
        System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate,
        Action? openPasswordSettings = null)
    {
        _roster = roster;
        _settings = settings;
        _serverCertificate = serverCertificate;
        Text = "TJ.RemoteAccess — Client access and file permissions";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        MinimumSize = new Size(780, 470);
        Size = new Size(1000, 610);
        StartPosition = FormStartPosition.CenterParent;
        foreach (var (name, width) in new[] {
            ("Viewer", 220), ("Status", 95), ("Control", 100),
            ("SHA-256 prefix", 160), ("Client ID", 245)
        })
            _entries.Columns.Add(name, width);

        var bar = new FlowLayoutPanel {
            Dock = DockStyle.Bottom, Height = 90, Padding = new Padding(5),
            WrapContents = true, AutoScroll = true
        };
        AddButton(bar, "Refresh", Reload);
        AddButton(bar, "Generate Viewer(s)", GenerateViewers);
        AddButton(bar, "Remote file share...", ConfigureRemoteFiles);
        if (openPasswordSettings is not null) AddButton(bar, "Password / quick access...", openPasswordSettings);
        AddButton(bar, "Disable", () => Change(enabled: false));
        AddButton(bar, "Enable", () => Change(enabled: true));
        AddButton(bar, "Allow control", () => ChangeControl(true));
        AddButton(bar, "View only", () => ChangeControl(false));
        Controls.Add(_entries);
        Controls.Add(_help);
        Controls.Add(bar);
        Reload();
    }

    private void ConfigureRemoteFiles()
    {
        using var dialog = new Form {
            Text = "TJ.RemoteAccess - Local file share permissions",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(670, 470),
            Size = new Size(820, 500),
            AutoScaleMode = AutoScaleMode.Dpi,
            Font = new Font("Segoe UI", 10f),
            BackColor = Color.White
        };
        var layout = new TableLayoutPanel {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(16)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label {
            Text = "Server Owner only: sharing a folder permits authenticated Controllers to " +
                "list and operate on its contents, subject to each on-device approval. " +
                "Choose only a folder intentionally shared; a drive root is forbidden.",
            Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.FromArgb(116, 56, 31)
        }, 0, 0);
        string newRoot = _settings.RemoteFileRoot;
        var rootLabel = new Label {
            Text = string.IsNullOrWhiteSpace(newRoot) ?
                "Default: private TJ.RemoteAccess/Received" : newRoot,
            Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft
        };
        var rootBar = new FlowLayoutPanel {
            Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true
        };
        var browse = new Button { Text = "Choose folder...", AutoSize = true };
        var restoreDefault = new Button { Text = "Use private Received", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var pick = new FolderBrowserDialog {
                Description = "Choose the local folder to share with remote Controllers",
                SelectedPath = Directory.Exists(newRoot) ? newRoot :
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (pick.ShowDialog(dialog) != DialogResult.OK) return;
            newRoot = pick.SelectedPath;
            rootLabel.Text = newRoot;
        };
        restoreDefault.Click += (_, _) =>
        {
            newRoot = "";
            rootLabel.Text = "Default: private TJ.RemoteAccess/Received";
        };
        rootBar.Controls.Add(browse);
        rootBar.Controls.Add(restoreDefault);
        layout.Controls.Add(rootLabel, 0, 1);
        var options = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, AutoScroll = true
        };
        CheckBox Toggle(string name, bool enabled)
        {
            var check = new CheckBox {
                Text = name, Checked = enabled, AutoSize = true,
                Margin = new Padding(8, 10, 8, 10)
            };
            options.Controls.Add(check);
            return check;
        }
        var browseCheck = Toggle("Allow authenticated Controllers to browse the shared folder",
            _settings.AllowRemoteFileBrowse);
        var uploadCheck = Toggle("Allow upload into the shared folder (individual approval)",
            _settings.AllowRemoteFileUpload);
        var downloadCheck = Toggle("Allow downloads from Server (individual approval)",
            _settings.AllowRemoteFileDownload);
        var changeCheck = Toggle("Allow create / rename / delete / copy (individual approval)",
            _settings.AllowRemoteFileChanges);
        layout.Controls.Add(options, 0, 2);
        layout.Controls.Add(rootBar, 0, 3);
        var buttons = new FlowLayoutPanel {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft
        };
        var save = new Button { Text = "Save permissions", AutoSize = true,
            DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true,
            DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            LabRemoteFileAccessPolicy.ValidateRoot(newRoot);
            if (MessageBox.Show(this,
                "Change this Server's shared folder or permissions?\n\n" +
                "Existing sessions using an old share root will be denied new file actions. " +
                "Viewers must reconnect to use the new root.",
                "Confirm local Server file access",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            // Persist first; never leave runtime permissions broader than the
            // file that will be loaded again after a restart.
            var updated = new LabServerSettings {
                ListenAddress = _settings.ListenAddress,
                Port = _settings.Port,
                ServerCertificateThumbprint = _settings.ServerCertificateThumbprint,
                AllowedClientSha256 = _settings.AllowedClientSha256,
                RemoteFileRoot = newRoot,
                AllowRemoteFileBrowse = browseCheck.Checked,
                AllowRemoteFileUpload = uploadCheck.Checked,
                AllowRemoteFileDownload = downloadCheck.Checked,
                AllowRemoteFileChanges = changeCheck.Checked
            };
            LabCertificates.WriteSettings(LabCertificates.ServerFile, updated);
            _settings.RemoteFileRoot = updated.RemoteFileRoot;
            _settings.AllowRemoteFileBrowse = updated.AllowRemoteFileBrowse;
            _settings.AllowRemoteFileUpload = updated.AllowRemoteFileUpload;
            _settings.AllowRemoteFileDownload = updated.AllowRemoteFileDownload;
            _settings.AllowRemoteFileChanges = updated.AllowRemoteFileChanges;
            MessageBox.Show(this, "Saved. Reconnect Viewers to use the new file share policy.",
                "TJ.RemoteAccess", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or
            UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Remote file share change rejected",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void GenerateViewers()
    {
        if (LabClientPairingDialog.Create(this, _roster, _settings, _serverCertificate))
            Reload();
    }

    private static void AddButton(FlowLayoutPanel bar, string text, Action callback)
    {
        var button = new Button { Text = text, AutoSize = true };
        button.Click += (_, _) => callback();
        bar.Controls.Add(button);
    }

    private LabServerClient? Selected()
    {
        if (_entries.SelectedItems.Count != 1) return null;
        string id = (string)_entries.SelectedItems[0].Tag!;
        return _roster.Load().Clients.SingleOrDefault(x => x.Id == id);
    }

    private void Reload()
    {
        try
        {
            var catalog = _roster.Load();
            _entries.BeginUpdate();
            try
            {
                _entries.Items.Clear();
                foreach (var client in catalog.Clients.OrderBy(x => x.Name))
                {
                    var row = new ListViewItem(client.Name) { Tag = client.Id };
                    row.SubItems.Add(client.Enabled ? "Enabled" : "Disabled");
                    row.SubItems.Add(client.CanControl ? "Permitted" : "View only");
                    row.SubItems.Add(client.CertificateSha256[..16] + "...");
                    row.SubItems.Add(client.Id);
                    _entries.Items.Add(row);
                }
            }
            finally { _entries.EndUpdate(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                  or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, ex.Message, "Client authorization unavailable",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Change(bool enabled)
    {
        try
        {
            var selected = Selected();
            if (selected is null) return;
            if (selected.Enabled == enabled) return;
            if (MessageBox.Show(this,
                    (enabled ? "Enable" : "Disable") + $" '{selected.Name}'?\n" +
                    "Disabling blocks existing active connections after policy refresh.",
                    "Change Viewer access", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _roster.SetEnabled(selected.Id, enabled);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                  or UnauthorizedAccessException or InvalidOperationException
                                  or KeyNotFoundException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Access change rejected");
        }
    }

    private void ChangeControl(bool permitted)
    {
        try
        {
            var selected = Selected();
            if (selected is null || selected.CanControl == permitted) return;
            _roster.SetCanControl(selected.Id, permitted);
            Reload();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
                                  or UnauthorizedAccessException or InvalidOperationException
                                  or KeyNotFoundException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Access change rejected");
        }
    }
}
