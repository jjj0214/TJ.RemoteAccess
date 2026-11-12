// SPDX-License-Identifier: GPL-2.0-or-later
// Local interactive administrator actions for the Lab Server's client roster.
// Does not open firewall ports, install services or remove Windows certificates.
namespace TJ.RemoteAccess.Lab;

internal static class LabSetupClientCommands
{
    internal static int PairClient(string name, bool canControl)
    {
        var settings = LabCertificates.LoadSettings<LabServerSettings>(LabCertificates.ServerFile);
        using var serverCertificate =
            LabCertificates.LoadPrivateCertificate(settings.ServerCertificateThumbprint);
        var roster = new LabServerClientRoster(legacyPin: settings.AllowedClientSha256);
        var result = LabClientPairing.Create(name, settings, serverCertificate,
                                            roster, canControl: canControl);
        Console.WriteLine("UNIQUE VIEWER PAIRING GENERATED (LAB ONLY)");
        Console.WriteLine("Viewer identity: " + result.Client.Name);
        Console.WriteLine("Client ID: " + result.Client.Id);
        Console.WriteLine("Control permitted: " + result.Client.CanControl);
        Console.WriteLine("Pairing folder (sensitive): " + result.Folder);
        Console.WriteLine("Client PFX password (DISPLAYED ONCE): " + result.PfxPassword);
        Console.WriteLine("Copy the three files in this folder to the intended Viewer PC.");
        Console.WriteLine("Import the PFX from the Viewer Add server dialog.");
        Console.WriteLine("When import is verified, delete temporary PFX copies.");
        Console.WriteLine("Use disable-client <id> to revoke this unique Viewer.");
        return 0;
    }

    internal static int ListClients()
    {
        var roster = LoadRoster();
        var catalog = roster.Load();
        Console.WriteLine($"TJRA Lab allowed Viewer identities: {catalog.Clients.Count}");
        foreach (var entry in catalog.Clients.OrderBy(x => x.Name))
        {
            Console.WriteLine($"{entry.Name} | ID={entry.Id} | " +
                $"{(entry.Enabled ? "Enabled" : "Disabled")} | " +
                $"{(entry.CanControl ? "Controller permitted" : "View only")}");
            Console.WriteLine("  Certificate SHA-256: " + entry.CertificateSha256);
        }
        return 0;
    }

    internal static int SetClientEnabled(string id, bool enabled)
    {
        var entry = LoadRoster().SetEnabled(id, enabled);
        Console.WriteLine($"{entry.Name}: {(entry.Enabled ? "ENABLED" : "DISABLED")}");
        Console.WriteLine("Policy re-evaluates active sessions on subsequent frame/packet.");
        return 0;
    }

    internal static int SetClientControl(string id, bool canControl)
    {
        var entry = LoadRoster().SetCanControl(id, canControl);
        Console.WriteLine($"{entry.Name}: {(entry.CanControl ? "Control permitted" : "View only")}");
        Console.WriteLine("A currently connected Controller is disconnected if downgraded.");
        return 0;
    }

    private static LabServerClientRoster LoadRoster()
    {
        var settings = LabCertificates.LoadSettings<LabServerSettings>(LabCertificates.ServerFile);
        return new LabServerClientRoster(legacyPin: settings.AllowedClientSha256);
    }
}
