// SPDX-License-Identifier: GPL-2.0-or-later
// Physically present operator UI. Authorization changes take effect without a
// Server restart; connected viewers revalidate at least once per screen frame.
using System.Drawing;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal sealed class LabServerClientAccessForm : Form
{
    private readonly LabServerClientRoster _roster;
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

    internal LabServerClientAccessForm(LabServerClientRoster roster)
    {
        _roster = roster;
        Text = "TJ.RemoteAccess Lab — Client access (current user)";
        MinimumSize = new Size(700, 400);
        Size = new Size(860, 520);
        StartPosition = FormStartPosition.CenterParent;
        foreach (var (name, width) in new[] {
            ("Viewer", 220), ("Status", 95), ("Control", 100),
            ("SHA-256 prefix", 160), ("Client ID", 245)
        })
            _entries.Columns.Add(name, width);

        var bar = new FlowLayoutPanel {
            Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(5)
        };
        AddButton(bar, "Refresh", Reload);
        AddButton(bar, "Disable", () => Change(enabled: false));
        AddButton(bar, "Enable", () => Change(enabled: true));
        AddButton(bar, "Allow control", () => ChangeControl(true));
        AddButton(bar, "View only", () => ChangeControl(false));
        Controls.Add(_entries);
        Controls.Add(_help);
        Controls.Add(bar);
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
