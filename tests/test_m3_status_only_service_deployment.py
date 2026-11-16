# SPDX-License-Identifier: GPL-2.0-or-later
"""Static safety contracts for M3 status-only packaging, NOT runtime acceptance."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
TOOLS = ROOT / "tools"
SRC = ROOT / "src" / "TJRemoteAccess.Core"

class M3StatusOnlyDeploymentContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.build = (TOOLS / "Build-TJRemoteAccess-M3-LabPackage.ps1").read_text(encoding="utf-8-sig")
        cls.install = (TOOLS / "Install-TJRemoteAccess-M3-Lab.ps1").read_text(encoding="utf-8-sig")
        cls.uninstall = (TOOLS / "Uninstall-TJRemoteAccess-M3-Lab.ps1").read_text(encoding="utf-8-sig")
        cls.probe = (TOOLS / "Test-TJRemoteAccess-M3-Lab.ps1").read_text(encoding="utf-8-sig")

    def test_package_is_committed_gpl_source_corresponding_and_sha256(self):
        self.assertIn("status --porcelain", self.build)
        self.assertIn("git -C $root archive --format=zip", self.build)
        self.assertIn("Get-FileHash", self.build)
        self.assertIn("manifest.json", self.build)
        self.assertIn("SourceRevision", self.build)
        self.assertIn("RemoteAccessEnabled = $false", self.build)
        self.assertIn("win-x64", self.build)
        self.assertIn("-p:PublishSingleFile=true", self.build)
        self.assertIn("TJ.RemoteAccess.M3.ControlService.exe", self.build)
        self.assertIn("TJ.RemoteAccess.M3.Agent.exe", self.build)

    def test_installer_requires_explicit_admin_and_trusted_manifest(self):
        for phrase in (
            "ApproveStatusOnlyLab", "WindowsPrincipal",
            "ExpectedPackageSha256", "Get-FileHash",
            "manifest.SchemaVersion -ne 1", "manifest.RemoteAccessEnabled",
            "RemoteAccessEnabled -ne $false", "SHA256", "source.zip",
            "LocalSystem", "start= demand", "binPath=", "Move-Item",
            "backup", "Upgrade",
        ):
            self.assertIn(phrase, self.install)
        self.assertIn("M3 STATUS-ONLY LAB INSTALL PASS", self.install)
        self.assertNotIn("start= auto", self.install)
        self.assertNotIn("Set-NetFirewallRule", self.install)
        self.assertNotIn("New-NetFirewallRule", self.install)
        self.assertNotIn("Uninstall-TightVNC", self.install)
        self.assertNotIn("[Convert]::ToHexString", self.install)

    def test_uninstaller_is_namespaced_and_checks_ownership(self):
        self.assertIn("ApproveStatusOnlyLabRemoval", self.uninstall)
        self.assertIn("TJRA.ControlService", self.uninstall)
        self.assertIn("expectedExe", self.uninstall)
        self.assertIn("manifest.Artifact", self.uninstall)
        self.assertIn("sc.exe delete", self.uninstall)
        self.assertNotIn("Uninstall-TightVNC", self.uninstall)
        self.assertNotIn("New-NetFirewallRule", self.uninstall)

    def test_read_only_probe_does_not_launch_or_mutate(self):
        self.assertIn("TJ.RemoteAccess.M3.StatusOnly (READ ONLY)", self.probe)
        self.assertIn("Get-CimInstance", self.probe)
        self.assertIn("Get-Process", self.probe)
        self.assertIn("ManifestStatus", self.probe)
        for forbidden in ("Start-Service", "Stop-Service", "sc.exe", "Remove-Item",
                          "Set-ItemProperty", "New-NetFirewallRule",
                          "Import-PfxCertificate"):
            self.assertNotIn(forbidden, self.probe)

    def test_solution_builds_service_agent_without_shared_obj_collisions(self):
        props = (SRC / "Directory.Build.props").read_text(encoding="utf-8-sig")
        sln = (ROOT / "TJ.RemoteAccess.Lab.sln").read_text(encoding="utf-8-sig")
        self.assertIn("BaseIntermediateOutputPath", props)
        self.assertIn("$(MSBuildProjectName)", props)
        self.assertIn("MSBuildProjectExtensionsPath", props)
        self.assertIn("TJ.RemoteAccess.M3.ControlService.csproj", sln)
        self.assertIn("TJ.RemoteAccess.M3.Agent.csproj", sln)

    def test_service_does_not_enable_remote_transport_or_input(self):
        control = (SRC / "M3ControlService.cs").read_text(encoding="utf-8-sig")
        agent = (SRC / "M3SessionAgent.cs").read_text(encoding="utf-8-sig")
        launcher = (SRC / "M3WindowsSessionLauncher.cs").read_text(encoding="utf-8-sig")
        self.assertIn("M3SessionLifecycle", control)
        self.assertIn("ValidateServiceIdentity", control)
        self.assertIn("OnSessionChange", control)
        self.assertIn("GetVerifiedKernelPeer", launcher)
        self.assertIn("WTSQueryUserToken", launcher)
        self.assertIn("M3AgentProof.AuthenticateParentAsync", launcher)
        self.assertIn("M3AgentProof.AuthenticateChildAsync", agent)
        for data in (control, agent):
            self.assertNotIn("TcpListener(", data)
            self.assertNotIn("SendInput(", data)
            self.assertNotIn("CopyFromScreen(", data)

if __name__ == "__main__":
    unittest.main()
