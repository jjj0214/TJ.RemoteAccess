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
            if (args.Length == 2 && args[0].Equals("import-client", StringComparison.OrdinalIgnoreCase))
                return ImportClient(args[1]);
            Console.WriteLine("TJ.RemoteAccess Lab Setup (current-user only)\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe init\n" +
                "  TJ.RemoteAccess.Lab.Setup.exe import-client <client-transfer.pfx>\n\n" +
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
        if (File.Exists(LabCertificates.ServerFile) || File.Exists(LabCertificates.ViewerFile))
            throw new InvalidOperationException("Lab configuration already exists. " +
                "Refusing to overwrite certificates or configured trust pins.");
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
        Console.WriteLine("Viewer settings: " + LabCertificates.ViewerFile);
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

    private static int ImportClient(string file)
    {
        var inputFile = Path.GetFullPath(file);
        if (!File.Exists(inputFile))
            throw new FileNotFoundException("Pairing PFX not found.", inputFile);
        var viewerConfig = Path.Combine(Path.GetDirectoryName(inputFile)!, "viewer.json");
        if (!File.Exists(viewerConfig))
            throw new FileNotFoundException("Matching viewer.json must accompany the PFX.");
        if (File.Exists(LabCertificates.ViewerFile))
            throw new InvalidOperationException(
                "Viewer configuration already exists. Refusing to change pins.");
        var settings = LabCertificates.LoadSettings<LabViewerSettings>(viewerConfig);
        Console.Write("Enter client PFX password (hidden): ");
        string password = ReadSecret();
#pragma warning disable SYSLIB0057
        using (var check = new X509Certificate2(inputFile, password,
                   X509KeyStorageFlags.EphemeralKeySet))
#pragma warning restore SYSLIB0057
        {
            if (!string.Equals(check.Thumbprint, settings.ClientCertificateThumbprint,
                               StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("PFX does not match viewer.json.");
        }
        LabCertificates.ImportPrivateCertificate(inputFile, password);
        LabCertificates.WriteSettings(LabCertificates.ViewerFile, settings);
        Console.WriteLine("\nClient certificate installed in CurrentUser\\My.");
        Console.WriteLine("Viewer config: " + LabCertificates.ViewerFile);
        Console.WriteLine("Change Host from 127.0.0.1 to server LAN IP ONLY " +
                          "after enabling explicit LAN listening on server.");
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
