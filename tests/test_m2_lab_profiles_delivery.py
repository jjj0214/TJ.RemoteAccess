# SPDX-License-Identifier: GPL-2.0-or-later
"""Read-only regression contracts for the multi-server lab delivery."""
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"
TOOLS = ROOT / "tools"


class LabMultiServerDeliveryTests(unittest.TestCase):
    def test_all_profile_sources_are_compiled(self):
        for component in ("Viewer", "Setup"):
            file = CORE / f"TJ.RemoteAccess.Lab.{component}.csproj"
            included = [node.attrib.get("Include", "")
                        for node in ET.parse(file).getroot().findall(".//Compile")]
            self.assertIn("LabViewerProfiles.cs", included)
        viewer = [node.attrib.get("Include", "")
                  for node in ET.parse(CORE / "TJ.RemoteAccess.Lab.Viewer.csproj")
                  .getroot().findall(".//Compile")]
        self.assertIn("LabViewerProfileDialogs.cs", viewer)
        tests = [node.attrib.get("Include", "")
                 for node in ET.parse(ROOT / "tests" / "TJ.RemoteAccess.Lab.Tests.csproj")
                 .getroot().findall(".//Compile")]
        self.assertIn("lab_profiles_tests.cs", tests)

    def test_existing_legacy_profile_not_overwritten(self):
        source = (CORE / "LabSetup.cs").read_text(encoding="utf-8")
        self.assertIn("RegisterProvisioned", source)
        self.assertIn("if (!File.Exists(LabCertificates.ViewerFile))", source)
        self.assertNotIn("Viewer configuration already exists", source)
        self.assertIn("list-profiles", source)
        self.assertIn("ImportPairing", source)
        store = (CORE / "LabViewerProfiles.cs").read_text(encoding="utf-8")
        self.assertIn("viewer-profiles.json", store)
        self.assertIn("viewer.json", store)
        self.assertIn("SelectedProfileId", store)
        self.assertIn("FileShare.None", store)
        self.assertIn("server-public.cer", store)
        self.assertIn("FixedTimeEquals", (CORE / "LabCertificates.cs").read_text(encoding="utf-8"))

    def test_gui_supports_add_select_edit_remove_without_cert_pin_edit(self):
        source = (CORE / "LabViewer.cs").read_text(encoding="utf-8")
        for token in ("_profilePicker", "AddServerProfile", "EditServerProfile",
                      "RemoveServerProfile", "LabViewerProfileDialogs.ShowIdentity",
                      "_profileStore.Edit", "profile.ServerSha256"):
            self.assertIn(token, source)
        dialogs = (CORE / "LabViewerProfileDialogs.cs").read_text(encoding="utf-8")
        self.assertIn("UseSystemPasswordChar", dialogs)
        self.assertIn("The pinned server identity cannot be edited here", dialogs)

    def test_installer_stages_and_rolls_back_without_touching_tightvnc(self):
        script = (TOOLS / "Install-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        self.assertIn(".stage-", script)
        self.assertIn(".backup-", script)
        self.assertIn("Post-update verification failed", script)
        self.assertIn("HadPrevious", script)
        self.assertIn("Move-Item", script)
        self.assertIn("Test-Path", script)
        self.assertIn("Existing profiles/certificates preserved", script)
        for prohibited in ("New-NetFirewallRule", "Set-NetFirewallRule",
                           "New-Service", "sc.exe", "schtasks.exe"):
            self.assertNotIn(prohibited, script)

    def test_readonly_preflight_and_two_pc_acceptance_guide(self):
        script = (TOOLS / "Test-TJRemoteAccess-Lab.ps1").read_text(
            encoding="utf-8-sig")
        for token in ("viewer-profiles.json", "SavedServerCount",
                      "SelectedServerName", "ProfileStatus"):
            self.assertIn(token, script)
        guide = (ROOT / "docs" / "M2_LAB_FINAL_ACCEPTANCE_20261112_KO.md").read_text(
            encoding="utf-8")
        for token in ("Add server", "import-client", "list-profiles",
                      "Viewer configuration already exists", "git pull --ff-only",
                      "Uninstall-TJRemoteAccess-Lab.ps1"):
            self.assertIn(token, guide)


if __name__ == "__main__":
    unittest.main()
