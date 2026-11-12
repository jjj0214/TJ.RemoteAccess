# SPDX-License-Identifier: GPL-2.0-or-later
"""Read-only source contract for per-client TLS authorization and issuance."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "TJRemoteAccess.Core"
TESTS = ROOT / "tests"


class LabClientRosterDeliveryTests(unittest.TestCase):
    def test_projects_include_new_code_and_windows_schannel_tests(self):
        expected = {
            "Server": {"LabServerClients.cs", "LabServerClientAccessForm.cs"},
            "Setup": {"LabServerClients.cs", "LabClientPairing.cs", "LabSetupClientCommands.cs"},
        }
        for component, required in expected.items():
            project = SRC / f"TJ.RemoteAccess.Lab.{component}.csproj"
            found = {x.get("Include") for x in ET.parse(project).getroot().iter("Compile")}
            self.assertTrue(required <= found, f"Missing {required - found}")
        found = {x.get("Include") for x in
                 ET.parse(TESTS / "TJ.RemoteAccess.Lab.Tests.csproj")
                 .getroot().iter("Compile")}
        self.assertTrue({"lab_client_roster_tests.cs",
                         "lab_client_tls_authorization_tests.cs"} <= found)

    def test_server_uses_dynamic_roster_at_three_security_decisions(self):
        server = (SRC / "LabServer.cs").read_text(encoding="utf-8")
        self.assertIn("_clientRoster.IsCertificateAllowed(cert)", server)
        self.assertIn("var admission = _clientRoster.GetAuthorized(clientPin)", server)
        self.assertIn("admission.CanControl", server)
        self.assertIn("if (rights is null || (role == LabRole.Controller", server)
        self.assertIn("_clientRoster.IsCertificateAllowed(clientCertificate)", server)
        self.assertNotIn(
            "ValidatePinned(cert, _settings.AllowedClientSha256", server)
        self.assertIn("SendFrames(tls, writeMutex, frames, remoteCert, clientPin", server)

    def test_roster_migrates_and_fails_closed_without_legacy_revival(self):
        roster = (SRC / "LabServerClients.cs").read_text(encoding="utf-8")
        for piece in ("server-clients.json", "server-clients.migrated",
                      "Legacy paired Viewer", "MaxClients = 128",
                      "TimeSpan.FromMilliseconds(500)",
                      "CertificateSha256", "IsCertificateAllowed",
                      "FileShare.None", "MigrationMarkerPath"):
            self.assertIn(piece, roster)
        self.assertIn("if (File.Exists(MigrationMarkerPath))", roster)
        self.assertIn("DateTime.UtcNow", roster)

    def test_unique_pairing_stages_private_key_only_as_password_pfx(self):
        pairing = (SRC / "LabClientPairing.cs").read_text(encoding="utf-8")
        for piece in ("CreateSelfSigned", "client-transfer.pfx",
                      "CryptographicOperations.ZeroMemory", "server-public.cer",
                      "viewer.json", "roster.Register", "PfxPassword"):
            self.assertIn(piece, pairing)
        self.assertNotIn("store.Add(", pairing)
        self.assertNotIn("GenerateCertificate(server: false)", pairing)
        commands = (SRC / "LabSetupClientCommands.cs").read_text(encoding="utf-8")
        for piece in ("PairClient", "ListClients", "SetClientEnabled",
                      "SetClientControl"):
            self.assertIn(piece, commands)

    def test_lab_installer_does_not_touch_windows_services(self):
        installer = (ROOT / "tools" / "Install-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        for forbidden in ("New-NetFirewallRule", "New-Service", "sc.exe",
                          "schtasks.exe", "Set-Service", "netsh advfirewall"):
            self.assertNotIn(forbidden, installer)


if __name__ == "__main__":
    unittest.main()
