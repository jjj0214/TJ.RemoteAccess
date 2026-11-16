# SPDX-License-Identifier: GPL-2.0-or-later
"""Static delivery invariants for standalone TLS quick-password support.

The real Schannel + anonymous-client-certificate + fingerprint loopback
tests live in preview_quick_password_tests.cs. These contracts are not
a substitute for two-PC GUI acceptance.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "TJRemoteAccess.Core"
DOCS = ROOT / "docs"
TOOLS = ROOT / "tools"

class QuickPasswordDelivery(unittest.TestCase):
    def test_quick_password_is_preview_only_and_legacy_certificate_mode_remains(self):
        quick = (SRC / "LabViewerQuickPasswordDialog.cs").read_text(encoding="utf-8-sig")
        viewer = (SRC / "LabViewer.cs").read_text(encoding="utf-8-sig")
        preview_project = (SRC / "TJ.RemoteAccess.Viewer.csproj").read_text(encoding="utf-8-sig")
        lab_project = (SRC / "TJ.RemoteAccess.Lab.Viewer.csproj").read_text(encoding="utf-8-sig")
        self.assertIn("Quick connect (IP + password)", viewer)
        self.assertIn("LabViewerQuickPasswordDialog.Prompt", viewer)
        self.assertIn("LabViewerQuickPasswordDialog.ConfirmFirstUse", viewer)
        self.assertIn("LabQuickPasswordTrust.ProbeServerIdentityAsync", viewer)
        self.assertLess(viewer.index("ProbeServerIdentityAsync("),
                        viewer.index("LabMessage.PasswordLogin, secret"))
        self.assertIn("LabViewerPasswordDialogs.PromptPassword(this)", viewer)
        self.assertIn("LabCertificates.LoadPrivateCertificate(profile.ClientCertificateThumbprint)", viewer)
        self.assertIn('<Compile Include="LabViewerQuickPasswordDialog.cs" />', preview_project)
        self.assertNotIn('<Compile Include="LabViewerQuickPasswordDialog.cs" />', lab_project)
        self.assertIn("No Viewer certificate needed", quick)
        self.assertIn("Trust and save this server identity?", quick)

    def test_two_tls_connections_and_no_password_in_preflight(self):
        trust = (SRC / "LabQuickPasswordTrust.cs").read_text(encoding="utf-8-sig")
        viewer = (SRC / "LabViewer.cs").read_text(encoding="utf-8-sig")
        for token in ("SslClientAuthenticationOptions", "Tls12 | SslProtocols.Tls13",
                      "ClientCertificates = null", "ExamineServer",
                      "AuthenticateAsClientAsync", "NoCheck"):
            self.assertIn(token, trust)
        self.assertNotIn("LabMessage.PasswordLogin", trust)
        self.assertNotIn("LabWire.WriteAsync", trust)
        self.assertIn("profile.ServerSha256 = presentedSha", viewer)
        self.assertIn("ExamineServer(", viewer)
        self.assertIn("profile.ServerSha256);", viewer)
        self.assertIn("MaySendPassword(", viewer)
        self.assertIn("RememberQuickPasswordEndpoint", viewer)

    def test_password_profiles_are_per_endpoint_and_not_overridden_by_changed_sha(self):
        profiles = (SRC / "LabViewerProfiles.cs").read_text(encoding="utf-8-sig")
        for token in ("FindPasswordEndpoint", "RememberQuickPasswordEndpoint",
                      "p.Port == port", "p.Port == candidate.Port",
                      "Conflicting trusted identities", "Server identity changed",
                      "ClientCertificateThumbprint = \"\""):
            self.assertIn(token, profiles)

    def test_server_password_listener_is_tls_only_and_rate_limited(self):
        server = (SRC / "LabServer.cs").read_text(encoding="utf-8-sig")
        access = (SRC / "LabPasswordAccess.cs").read_text(encoding="utf-8-sig")
        dialog = (SRC / "LabPasswordAccessDialog.cs").read_text(encoding="utf-8-sig")
        for token in ("ClientCertificateRequired = !passwordMode",
                      "SslServerAuthenticationOptions", "Tls12 | SslProtocols.Tls13",
                      "_passwordLimiter.MayAttempt", "_passwordLimiter.Failed",
                      "LabMessage.PasswordLogin", "_sessions.Count >= 3",
                      "NeedsListenerRestart(previous, current)"):
            self.assertIn(token, server)
        self.assertIn("CryptographicOperations.FixedTimeEquals", access)
        self.assertIn("Rfc2898DeriveBytes.Pbkdf2", access)
        self.assertIn("MinPasswordCharacters = 6", access)
        self.assertIn("Copy Server pin", dialog)
        self.assertNotIn("TcpListener(_address, 5900)", server)

    def test_preview_version_and_user_guide(self):
        for file in ("TJ.RemoteAccess.Server.csproj", "TJ.RemoteAccess.Viewer.csproj",
                     "TJ.RemoteAccess.Setup.csproj", "TJ.RemoteAccess.M3.Agent.csproj",
                     "TJ.RemoteAccess.M3.ControlService.csproj"):
            self.assertIn("0.5.3-preview", (SRC / file).read_text(encoding="utf-8-sig"))
        for file in ("Build-TJRemoteAccess-Preview-OfflinePackage.ps1",
                     "Build-TJRemoteAccess-ServicePreviewPackage.ps1"):
            self.assertIn("0.5.3-preview", (TOOLS / file).read_text(encoding="utf-8-sig"))
        doc = (DOCS / "QUICK_PASSWORD_ACCESS_0_5_2_20261116_KO.md").read_text(
            encoding="utf-8-sig")
        for phrase in ("15544", "15545", "Q01", "Q13", "TLS", "SHA-256"):
            self.assertIn(phrase, doc)

if __name__ == "__main__":
    unittest.main()
