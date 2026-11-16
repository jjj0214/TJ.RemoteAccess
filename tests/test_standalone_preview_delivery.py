# SPDX-License-Identifier: GPL-2.0-or-later
"""Standalone preview packaging/build policy contracts (not service acceptance)."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "TJRemoteAccess.Core"
TOOLS = ROOT / "tools"

class StandalonePreviewContracts(unittest.TestCase):
    def test_three_independent_product_executables_and_product_solution(self):
        solution = (ROOT / "TJ.RemoteAccess.sln").read_text(encoding="utf-8-sig")
        for part in ("Server", "Viewer", "Setup"):
            project = SRC / f"TJ.RemoteAccess.{part}.csproj"
            self.assertTrue(project.exists())
            root = ET.parse(project).getroot()
            self.assertEqual(root.findtext("./PropertyGroup/AssemblyName"),
                             f"TJ.RemoteAccess.{part}")
            self.assertIn("TJRA_STANDALONE_PREVIEW",
                root.findtext("./PropertyGroup/DefineConstants"))
            self.assertIn(f"TJ.RemoteAccess.{part}.csproj", solution)
        self.assertIn("TJ.RemoteAccess.M3.Agent.csproj", solution)
        self.assertIn("TJ.RemoteAccess.M3.ControlService.csproj", solution)

    def test_standalone_settings_isolate_lab_and_tightvnc(self):
        settings = (SRC / "LabCertificates.cs").read_text(encoding="utf-8-sig")
        self.assertIn('DirectoryName = "TJ.RemoteAccess.Lab"', settings)
        self.assertIn('DirectoryName = "TJ.RemoteAccess"', settings)
        self.assertIn("DefaultPort = 15444", settings)
        self.assertIn("DefaultPort = 15544", settings)
        self.assertIn("TJRA_STANDALONE_PREVIEW", settings)
        self.assertIn("StoreLocation.CurrentUser", settings)

    def test_owner_enrollment_requires_cert_and_user_sid(self):
        source = (SRC / "M3DesktopEnrollment.cs").read_text(encoding="utf-8-sig")
        for token in ("CurrentFile", "EnableForCurrentUser", "DisableForCurrentUser",
                      "WindowsIdentity.GetCurrent", "ServerCertificateSha256",
                      "LabCertificates.ValidatePinned", "AllowSupervisedStart"):
            self.assertIn(token, source)

    def test_service_supervision_is_not_a_secure_desktop_bypass(self):
        host = (SRC / "M3SupervisedDesktopHost.cs").read_text(encoding="utf-8-sig")
        agent = (SRC / "M3SessionAgent.cs").read_text(encoding="utf-8-sig")
        guard = (SRC / "M3InteractiveDesktopGuard.cs").read_text(encoding="utf-8-sig")
        service = (SRC / "M3ControlService.cs").read_text(encoding="utf-8-sig")
        policy = (SRC / "M3ServiceHostPolicy.cs").read_text(encoding="utf-8-sig")
        self.assertIn("M3DesktopEnrollmentStore.IsEnabledForCurrentUser", host)
        self.assertIn("M3InteractiveDesktopGuard.IsNormalInteractiveDesktop", host)
        self.assertIn("M3DesktopEnrollmentStore.IsEnabledForCurrentUser", agent)
        self.assertIn("KillOnJobClose", host)
        self.assertIn("CreateSuspended", host)
        self.assertIn("Mode: \"InteractiveOwnerConsentOnly\"", policy)
        self.assertIn("M3ServiceHostPolicyStore.IsEnabled()", service)
        self.assertIn('string.Equals(name, "Default"', guard)
        for script in (agent, host, service):
            self.assertNotIn("SetThreadDesktop(", script)
            self.assertNotIn("SendInput(", script)

    def test_service_preview_zip_has_source_hashes_and_five_binaries(self):
        builder = (TOOLS / "Build-TJRemoteAccess-ServicePreviewPackage.ps1").read_text(encoding="utf-8-sig")
        installer = (TOOLS / "Install-TJRemoteAccess-ServicePreview.ps1").read_text(encoding="utf-8-sig")
        for binary in ("TJ.RemoteAccess.Server.exe", "TJ.RemoteAccess.Viewer.exe",
                       "TJ.RemoteAccess.Setup.exe", "TJ.RemoteAccess.M3.Agent.exe",
                       "TJ.RemoteAccess.M3.ControlService.exe"):
            self.assertIn(binary, builder)
            self.assertIn(binary, installer)
        for value in ("git -C $root archive", "Get-FileHash", "source.zip",
                      "ServiceStartup = 'Automatic'", "RemoteAccessEnabled = $false",
                      "DesktopHostingAvailable = $true"):
            self.assertIn(value, builder)
        for value in ("ExpectedPackageSha256", "ApproveServicePreview",
                      "EnableSupervisedDesktop", "LocalSystem", "start= auto",
                      "TJ.RemoteAccess.Service.Preview", "Get-FileHash",
                      "ExpectedPackageSha256", "desktop-host-policy.json"):
            self.assertIn(value, installer)
        self.assertNotIn("New-NetFirewallRule", installer)
        self.assertNotIn("Uninstall-TightVNC", installer)

    def test_two_product_installers_never_change_tightvnc(self):
        user_installer = (TOOLS / "Install-TJRemoteAccess-Preview.ps1").read_text(encoding="utf-8-sig")
        offline_installer = (TOOLS / "Install-TJRemoteAccess-Preview-Offline.ps1").read_text(encoding="utf-8-sig")
        for text in (user_installer, offline_installer):
            self.assertIn("Programs\\TJ.RemoteAccess", text)
            self.assertNotIn("Programs\\TJ.RemoteAccess.Lab", text)
            self.assertNotIn("sc.exe", text)
            self.assertNotIn("New-NetFirewallRule", text)
            self.assertNotIn("Uninstall-TightVNC", text)

if __name__ == "__main__":
    unittest.main()
