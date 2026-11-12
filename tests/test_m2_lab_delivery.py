# SPDX-License-Identifier: GPL-2.0-or-later
"""Offline delivery/installation safety checks; no PowerShell/GUI execution."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"
TOOLS = ROOT / "tools"


class LabDeliverySafetyTests(unittest.TestCase):
    def test_three_independent_dotnet9_windows_projects(self):
        projects = {}
        for component in ("Server", "Viewer", "Setup"):
            file = CORE / f"TJ.RemoteAccess.Lab.{component}.csproj"
            self.assertTrue(file.is_file(), file)
            root = ET.parse(file).getroot()
            projects[component] = root
            target = root.find(".//TargetFramework")
            self.assertIsNotNone(target)
            self.assertEqual(target.text, "net9.0-windows")
            self.assertEqual(root.find(".//PlatformTarget").text, "x64")
            self.assertTrue(any("LabCertificates.cs" in item.attrib.get("Include", "")
                                for item in root.findall(".//Compile")))
        for component in ("Server", "Viewer"):
            self.assertEqual(projects[component].find(".//UseWindowsForms").text, "true")
        self.assertEqual(projects["Server"].find(".//AssemblyName").text,
                         "TJ.RemoteAccess.Lab.Server")
        self.assertEqual(projects["Viewer"].find(".//AssemblyName").text,
                         "TJ.RemoteAccess.Lab.Viewer")

    def test_install_is_current_user_only_and_does_not_open_firewall(self):
        source = (TOOLS / "Install-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        self.assertIn("dotnet publish", source)
        self.assertIn("PublishSingleFile=true", source)
        self.assertIn("SelfContained", source.replace("--self-contained", "SelfContained"))
        self.assertIn("Programs\\TJ.RemoteAccess.Lab", source)
        for forbidden in ("New-NetFirewallRule", "Set-NetFirewallRule", "sc.exe",
                          "New-Service", "schtasks.exe", "TightVNC\\tvnserver"):
            self.assertNotIn(forbidden, source)
        self.assertTrue((TOOLS / "Uninstall-TJRemoteAccess-Lab.ps1").is_file())
        self.assertTrue((TOOLS / "Test-TJRemoteAccess-Lab.ps1").is_file())

    def test_server_default_loopback_and_tls_pins(self):
        settings = (CORE / "LabCertificates.cs").read_text(encoding="utf-8")
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8")
        viewer = (CORE / "LabViewer.cs").read_text(encoding="utf-8")
        self.assertIn('"127.0.0.1"', settings)
        self.assertIn("15444", settings)
        self.assertIn("IPAddress.IsLoopback(ip)", server)
        self.assertIn("--allow-lan", server)
        self.assertIn("ClientCertificateRequired = true", server)
        self.assertIn("LabCertificates.ValidatePinned", server)
        self.assertIn("LabCertificates.ValidatePinned", viewer)
        self.assertIn("SslProtocols.Tls12 | SslProtocols.Tls13", server)
        self.assertIn("SslProtocols.Tls12 | SslProtocols.Tls13", viewer)
        self.assertIn("LabRole.ViewOnly", server)
        self.assertIn("_sessions.Count >= 3", server)
        self.assertIn("ReleaseAll(_pressedKeys", server)

    def test_setup_has_no_plain_private_key_in_settings(self):
        setup = (CORE / "LabSetup.cs").read_text(encoding="utf-8")
        cert = (CORE / "LabCertificates.cs").read_text(encoding="utf-8")
        self.assertIn("PersistKeySet", cert)
        self.assertIn("X509Store(StoreName.My, StoreLocation.CurrentUser)", cert)
        self.assertIn("server.json", cert)
        self.assertIn("viewer.json", cert)
        self.assertIn("File.Exists(LabCertificates.ServerFile)", setup)
        self.assertIn("client-transfer.pfx", setup)
        self.assertIn("RandomNumberGenerator.GetBytes(24)", setup)
        self.assertNotIn('"Password" :', cert)
        self.assertNotIn("WriteAllText(exportPath", setup)

    def test_documentation_explicit_lab_scope(self):
        doc = (ROOT / "docs" / "M2_LAB_INSTALL_TEST_KO.md").read_text(
            encoding="utf-8")
        for word in ("--allow-lan", "UAC", "guarded-v4",
                     "Import", "import-client", "127.0.0.1", "15444"):
            self.assertIn(word.lower(), doc.lower(), word)


if __name__ == "__main__":
    unittest.main()
