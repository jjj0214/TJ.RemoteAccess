# SPDX-License-Identifier: GPL-2.0-or-later
"""Regression contracts for isolated certificate/password TLS and server GUI pairing."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"


class LabPasswordAndGuiDeliveryTests(unittest.TestCase):
    def test_all_password_and_pairing_dialog_sources_are_compiled(self):
        targets = {
            "Server": {"LabClientPairingDialog.cs", "LabClientPairing.cs",
                       "LabPasswordAccess.cs", "LabPasswordAccessDialog.cs"},
            "Viewer": {"LabViewerPasswordDialogs.cs"}
        }
        for component, required in targets.items():
            project = CORE / f"TJ.RemoteAccess.Lab.{component}.csproj"
            files = {node.get("Include") for node in ET.parse(project).getroot().iter("Compile")}
            self.assertTrue(required <= files, f"{component} missing {required - files}")
        files = {node.get("Include") for node in
                 ET.parse(ROOT / "tests" / "TJ.RemoteAccess.Lab.Tests.csproj")
                 .getroot().iter("Compile")}
        self.assertIn("lab_password_mode_tests.cs", files)
        self.assertIn(r"..\src\TJRemoteAccess.Core\LabPasswordAccess.cs", files)

    def test_certificate_tls_remains_separate_from_opt_in_password_tls(self):
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8")
        for required in (
            "ClientCertificateRequired = !passwordMode",
            "_clientRoster.IsCertificateAllowed(cert)",
            "AcceptLoop(certListener, frames, false, token)",
            "AcceptLoop(passListener, frames, true, token)",
            "_passwordAccess.Current().Enabled",
            "_passwordLimiter.MayAttempt(peerIp)",
            "_passwordLimiter.Failed(peerIp)",
            "_passwordAccess.TryAuthenticate",
            "_passwordAccess.IsSessionAuthorized",
            "_sessions.Count >= 3",
            "SslProtocols.Tls12 | SslProtocols.Tls13",
            "LabMessage.PasswordLogin"
        ):
            self.assertIn(required, server)
        self.assertNotIn("AuthenticateAsServerAsync(tcp", server)

    def test_password_never_written_to_viewer_profile_or_served_on_raw_socket(self):
        viewer = (CORE / "LabViewer.cs").read_text(encoding="utf-8")
        profile = (CORE / "LabViewerProfiles.cs").read_text(encoding="utf-8")
        for fragment in ("LabViewerPasswordDialogs.PromptPassword",
                         "LabWire.PasswordLogin(loginPassword!)",
                         "LabCertificates.ValidatePinned(cert, profile.ServerSha256",
                         "CryptographicOperations.ZeroMemory(secret)"):
            self.assertIn(fragment, viewer)
        self.assertIn("AuthMode", profile)
        self.assertIn("RegisterPasswordProfile", profile)
        self.assertIn('ClientCertificateThumbprint = ""', profile)
        self.assertNotIn("Password = password", profile)
        prompt = (CORE / "LabViewerPasswordDialogs.cs").read_text(encoding="utf-8")
        self.assertIn("UseSystemPasswordChar = true", prompt)
        self.assertIn("server-public.cer", prompt)
        self.assertIn("ServerSha256", prompt) if "ServerSha256" in prompt else self.assertIn(
            "LabCertificates.Hash(cert)", prompt)

    def test_server_stores_only_salted_password_verifier_and_rate_limits(self):
        source = (CORE / "LabPasswordAccess.cs").read_text(encoding="utf-8")
        for fragment in ("Rfc2898DeriveBytes.Pbkdf2",
                         "Iterations = 600_000",
                         "CryptographicOperations.FixedTimeEquals",
                         "server-password.json",
                         "Enabled { get; set; }",
                         "LockFile",
                         "IsSessionAuthorized",
                         "_global.LockedUntil",
                         "state.Failures >= 5"):
            self.assertIn(fragment, source)
        self.assertNotIn("public string Password { get; set; }", source)
        self.assertNotIn("WriteAllText(SettingsFile, password)", source)

    def test_server_can_generate_viewer_two_three_from_dialog(self):
        dialog = (CORE / "LabClientPairingDialog.cs").read_text(encoding="utf-8")
        for fragment in ("Generate", "Viewer", "Value = 2",
                         "Number of Viewers", "Starting number", "LabClientPairing.Create",
                         "PFX password:", "control.Checked"):
            self.assertIn(fragment, dialog)
        server = (CORE / "LabServerClientAccessForm.cs").read_text(encoding="utf-8")
        self.assertIn("Generate Viewer(s)", server)
        self.assertIn("LabClientPairingDialog.Create", server)
        self.assertIn("LabServerClientAccessForm(_clientRoster, _settings, _serverCert)",
                      (CORE / "LabServer.cs").read_text(encoding="utf-8"))

    def test_diagnostic_does_not_expose_hashes_secrets_or_configure_remote_access(self):
        script = (ROOT / "tools" / "Test-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        for fragment in ("PasswordTLSStatus", "PasswordCanControl",
                         "Tcp15445Listeners", "server-password.json"):
            self.assertIn(fragment, script)
        self.assertNotIn("    PasswordVerifier =", script)
        for forbidden in ("New-NetFirewallRule", "New-Service",
                          "Import-PfxCertificate", "Set-ItemProperty"):
            self.assertNotIn(forbidden, script)


if __name__ == "__main__":
    unittest.main()
