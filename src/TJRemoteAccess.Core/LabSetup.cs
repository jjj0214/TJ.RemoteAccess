// SPDX-License-Identifier: GPL-2.0-or-later
// Current-user, development-only certificate initialization. No admin required.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TJ.RemoteAccess.Lab;

internal static class LabSetup
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0].Equals("init", StringComparison.OrdinalIgnoreCase))
                return Init();
            if (args.Length is >= 2 and <= 4 &&
                args[0].Equals("import-client", StringComparison.OrdinalIgnoreCase))
                return ImportClient(args[1], args.Length >= 3 ? args[2] : null,
                                    args.Length >= 4 ? args[3] : null);
            if (args.Length == 1 && args[0].Equals("list-profiles", StringComparison.OrdinalIgnoreCase))
                return ListProfiles();
            if (args.Length is 2 or 3 && args[0] == "pair-client")
            {
                if (args.Length == 3 && args[2] != "--view-only")
                    throw new ArgumentException("Expected optional --view-only.");
                return LabSetupClientCommands.PairClient(args[1], canControl: args.Length == 2);
            }
            if (args.Length == 1 && args[0] == "list-clients")
                return LabSetupClientCommands.ListClients();
            if (args.Length == 2 && args[0] == "disable-client")
                return LabSetupClientCommands.SetClientEnabled(args[1], false);
            if (args.Length == 2 && args[0] == "enable-client")
                return LabSetupClientCommands.SetClientEnabled(args[1], true);
            if (args.Length == 3 && args[0] == "client-control")
                return LabSetupClientCommands.SetClientControl(args[1], args[2] == "allow" ? true :
                    args[2] == "view-only" ? false : throw new ArgumentException("Expected allow or view-only."));
            Console.WriteLine("TJ.RemoteAccess Lab Setup (current-user only)\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe init\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe import-client <client-transfer.pfx> [name] [host]\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe list-profiles\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe pair-client <name> [--view-only]  (SERVER)\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe list-clients  (SERVER)\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe disable-client|enable-client <full-id>\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe client-control <full-id> allow|view-only\n\n" +
                "Use init on the remote SERVER PC, then transfer the pairing folder " +
                "securely to the VIEWER PC. No Windows service or firewall rule is created.");
            return 2;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("SETUP FAILED: " + e.Message);
            return 1;
        }
    }

    private static int Init()
    {
        if (File.Exists(LabCertificates.ServerFile))
            throw new InvalidOperationException("Server is already paired. " +
                "Refusing to replace an existing server certificate.");
        var pairing = Path.Combine(LabCertificates.ConfigDirectory, "Pairing");
        Directory.CreateDirectory(pairing);
        using var server = LabCertificates.GenerateCertificate(server: true);
        using var client = LabCertificates.GenerateCertificate(server: false);
        var serverSettings = new LabServerSettings {
            ListenAddress = "127.0.0.1",
            Port = 15444,
            ServerCertificateThumbprint = server.Thumbprint,
            AllowedClientSha256 = LabCertificates.Hash(client)
        };
        var viewerSettings = new LabViewerSettings {
            Host = "127.0.0.1",
            Port = 15444,
            ClientCertificateThumbprint = client.Thumbprint,
            ServerSha256 = LabCertificates.Hash(server)
        };
        LabCertificates.WriteSettings(LabCertificates.ServerFile, serverSettings);
        // Preserve any previous Viewer identity. The profile store imports
        // the legacy viewer.json non-destructively before adding this server.
        var localProfile = new LabViewerProfileStore().RegisterProvisioned(
            viewerSettings, "Local lab server");
        if (!File.Exists(LabCertificates.ViewerFile))
            LabCertificates.WriteSettings(LabCertificates.ViewerFile, viewerSettings);
        var pairingConfig = Path.Combine(pairing, "viewer.json");
        LabCertificates.WriteSettings(pairingConfig, viewerSettings);

        string passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var exportPath = Path.Combine(pairing, "client-transfer.pfx");
        // Client private key is password-encrypted and MUST be transferred
        // over a trusted channel. The password is displayed once, not saved.
        File.WriteAllBytes(exportPath, client.Export(X509ContentType.Pfx, passphrase));
        File.WriteAllBytes(Path.Combine(pairing, "server-public.cer"),
                           server.Export(X509ContentType.Cert));

        Console.WriteLine("PAIR INITIALIZED for TEST USE ONLY.");
        Console.WriteLine("Server settings: " + LabCertificates.ServerFile);
        Console.WriteLine("Local Viewer profile: " + localProfile.Name);
        Console.WriteLine("Viewer profiles: " + new LabViewerProfileStore().CatalogPath);
        Console.WriteLine("Legacy viewer.json preserved: " + LabCertificates.ViewerFile);
        Console.WriteLine("Pairing folder (SENSITIVE): " + pairing);
        Console.WriteLine("Client PFX export password (COPY NOW, NOT SAVED): " + passphrase);
        Console.WriteLine("Original TightVNC remains untouched.");
        Console.WriteLine("Server initially binds to 127.0.0.1 ONLY.");
        Console.WriteLine("For another PC, securely transfer viewer.json and " +
                          "client-transfer.pfx, then run import-client on the viewer.");
        Console.WriteLine("After importing, erase the exported PFX and its password " +
                          "from temporary transfer locations.");
        return 0;
    }

    private static int ImportClient(string file, string? name, string? host)
    {
        Console.Write("Enter client PFX password (hidden): ");
        string password = ReadSecret();
        var profile = new LabViewerProfileStore().ImportPairing(
            file, password, name, host);
        Console.WriteLine("Pairing registered (existing saved servers retained):");
        Console.WriteLine("  " + profile);
        Console.WriteLine("  Server SHA256: " + profile.ServerSha256);
        Console.WriteLine("  Profiles: " + new LabViewerProfileStore().CatalogPath);
        Console.WriteLine("You can now select this server in the Viewer.");
        Console.WriteLine("Erase exported PFX copies from temporary transfer locations.");
        return 0;
    }

    private static int ListProfiles()
    {
        var catalog = new LabViewerProfileStore().Load();
        Console.WriteLine($"Saved TJ.RemoteAccess Lab servers: {catalog.Profiles.Count}");
        foreach (var profile in catalog.Profiles)
        {
            Console.WriteLine((catalog.SelectedProfileId == profile.Id ? "* " : "  ") +
                              profile);
            Console.WriteLine("   Server pin SHA256: " + profile.ServerSha256);
        }
        return 0;
    }

    private static string ReadSecret()
    {
        var input = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (input.Length > 0) input.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar) && input.Length < 1024)
                input.Append(key.KeyChar);
        }
        Console.WriteLine();
        return input.ToString();
    }
}
