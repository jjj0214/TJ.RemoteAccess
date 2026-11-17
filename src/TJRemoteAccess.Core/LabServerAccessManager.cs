// SPDX-License-Identifier: GPL-2.0-or-later
// Product UI: certificate roster and optional password/TLS policy in one window.
using System.Drawing;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;

namespace TJ.RemoteAccess.Lab;

internal static class LabServerAccessManager
{
    internal static bool Show(Form owner, LabServerClientRoster roster,
                              LabPasswordAccess access, LabServerSettings settings,
                              X509Certificate2 serverCertificate)
    {
        var before = access.Current();
        using var window = new Form {
            Text = "TJ.RemoteAccess - Access and security",
            StartPosition = FormStartPosition.CenterParent,
            MinimumSize = new Size(800, 520), Size = new Size(980, 650)
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var clientsPage = new TabPage("Authorized viewers");
        var passwordPage = new TabPage("Password + TLS");
        using var clients = new LabServerClientAccessForm(roster, settings, serverCertificate) {
            TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill
        };
        using var password = LabPasswordAccessDialog.CreateForm(access, settings, serverCertificate);
        password.TopLevel = false;
        password.FormBorderStyle = FormBorderStyle.None;
        password.Dock = DockStyle.Fill;
        clientsPage.Controls.Add(clients);
        passwordPage.Controls.Add(password);
        tabs.TabPages.Add(clientsPage);
        tabs.TabPages.Add(passwordPage);
        var footer = new FlowLayoutPanel {
            Dock = DockStyle.Bottom, Height = 45,
            FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(5)
        };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => window.Close();
        footer.Controls.Add(close);
        window.Controls.Add(tabs);
        window.Controls.Add(footer);
        // A password change closes its editor and the parent manager to
        // apply the updated listener policy atomically; roster changes are live.
        password.FormClosed += (_, _) => {
            if (!window.IsDisposed) window.Close();
        };
        clients.Show();
        password.Show();
        window.ShowDialog(owner);
        return LabPasswordAccess.NeedsListenerRestart(before, access.Current());
    }
}
