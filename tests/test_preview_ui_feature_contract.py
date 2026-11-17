# SPDX-License-Identifier: GPL-2.0-or-later
"""Source-contract gates only; they do not claim Windows GUI acceptance."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "TJRemoteAccess.Core"
ISS = ROOT / "tools" / "TJ.RemoteAccess.iss"


class ProductAccessAndViewerFeatureContracts(unittest.TestCase):
    def test_installed_server_lan_does_not_relax_authenticated_access(self):
        launch = (SRC / "LabServerLaunch.cs").read_text(encoding="utf-8-sig")
        server = (SRC / "LabServer.cs").read_text(encoding="utf-8-sig")
        defaults = (SRC / "LabCertificates.cs").read_text(encoding="utf-8-sig")
        installer = ISS.read_text(encoding="utf-8-sig")
        self.assertIn("IsInstalledPreviewServer", launch)
        self.assertIn("explicitlyAllowLan |= LabServerLaunchOptions.IsInstalledPreviewServer", server)
        self.assertIn('DefaultListenAddress = "0.0.0.0"', defaults)
        self.assertIn('DefaultListenAddress = "127.0.0.1"', defaults)
        self.assertIn('Parameters: "--allow-lan"', installer)
        self.assertIn('Tasks: autostartserver; Components: server', installer)
        self.assertIn("Flags: unchecked; Components: server", installer)
        for forbidden in ("New-NetFirewallRule", "sc.exe create", "netsh advfirewall"):
            self.assertNotIn(forbidden, installer)

    def test_authorization_controls_are_combined_without_removing_tls(self):
        server = (SRC / "LabServer.cs").read_text(encoding="utf-8-sig")
        manager = (SRC / "LabServerAccessManager.cs").read_text(encoding="utf-8-sig")
        tray = (SRC / "LabServerTray.cs").read_text(encoding="utf-8-sig")
        self.assertIn("Access && security", tray)
        self.assertIn("LabServerAccessManager.Show", server)
        self.assertIn('new TabPage("Authorized viewers")', manager)
        self.assertIn('new TabPage("Password + TLS")', manager)
        self.assertIn("NeedsListenerRestart", manager)
        self.assertIn("IsSessionAuthorized", server)
        self.assertIn("GetAuthorized", server)

    def test_controller_only_tls_file_transfer_is_explicitly_approved(self):
        file_transfer = (SRC / "LabFileTransferPeer.cs").read_text(encoding="utf-8-sig")
        viewer = (SRC / "LabViewer.cs").read_text(encoding="utf-8-sig")
        commands = (SRC / "LabViewerToolCommands.cs").read_text(encoding="utf-8-sig")
        for token in (
            "MaxFileBytes = 512L * 1024 * 1024", "BlockSize = 32768",
            "CryptographicOperations.FixedTimeEquals",
            "Invalid file block", "DemandPermission()", "SaveFileDialog",
            "OpenFileDialog", "File.Move(", "ReceiveBlockAsync"):
            self.assertIn(token, file_transfer)
        self.assertIn("LabFileTransferDialogs.DestinationAsync", viewer)
        self.assertIn("LabFileTransferDialogs.ServerFileAsync",
                      (SRC / "LabServer.cs").read_text(encoding="utf-8-sig"))
        self.assertIn("SendFileFromViewerAsync", commands)
        self.assertIn("Volatile.Read(ref _viewOnlySnapshot)", viewer)
        self.assertNotIn("Process.Start(", file_transfer)

    def test_service_security_boundary_is_not_conflated_with_per_user_installer(self):
        service = (SRC / "M3ControlService.cs").read_text(encoding="utf-8-sig")
        lifecycle = (SRC / "M3SessionLifecycle.cs").read_text(encoding="utf-8-sig")
        supervisor = (SRC / "M3SupervisedDesktopHost.cs").read_text(encoding="utf-8-sig")
        self.assertIn("mayRetry: () =>", service)
        self.assertIn("M3ServiceHostPolicyStore.IsEnabled()", service)
        self.assertIn("approved == M3WindowsSessionLauncher.ConsoleSessionId", service)
        self.assertIn("retries < 2", lifecycle)
        self.assertIn("IsInstalledProductServerPathEligible", supervisor)
        installer = ISS.read_text(encoding="utf-8-sig")
        self.assertIn("PrivilegesRequired=lowest", installer)
        self.assertNotIn("TJ.RemoteAccess.M3.ControlService.exe", installer)
        self.assertNotIn("CreateService", installer)


if __name__ == "__main__":
    unittest.main()
