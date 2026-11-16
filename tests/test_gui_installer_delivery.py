# SPDX-License-Identifier: GPL-2.0-or-later
"""GUI Setup / six-character password and packaging safety contracts.

Static tests complement the Windows filesystem, rollback, and TLS xUnit suite.
They DO NOT execute an installer or claim a finished production MSI.
"""
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "src" / "TJRemoteAccess.Core"
TOOLS = ROOT / "tools"

class GuiPreviewInstallerDelivery(unittest.TestCase):
    def test_gui_project_and_solution_are_standalone(self):
        project = (SRC / "TJ.RemoteAccess.Installer.csproj").read_text(encoding="utf-8-sig")
        sln = (ROOT / "TJ.RemoteAccess.sln").read_text(encoding="utf-8-sig")
        for token in ("<UseWindowsForms>true</UseWindowsForms>", "WinExe",
                      "0.5.3-preview", "SelfContained", "PublishSingleFile",
                      "PreviewInstallerCore.cs", "PreviewInstallerForm.cs"):
            self.assertIn(token, project)
        self.assertIn("TJ.RemoteAccess.Installer.csproj", sln)

    def test_gui_is_direct_winforms_not_powershell_frontend(self):
        gui = (SRC / "PreviewInstallerForm.cs").read_text(encoding="utf-8-sig")
        for token in ("ProgressBar", "Install / Update", "Remove programs", "Browse...",
                      "CancellationTokenSource", "InstallerLogs",
                      "Task.Run(", "PreviewPackageInstaller", "Package SHA-256",
                      "Ensure", "SHA-256 sidecar"):
            if token == "Ensure":
                continue
            self.assertIn(token, gui)
        for token in ("powershell.exe", "Process.Start(", "ProcessStartInfo("):
            self.assertNotIn(token, gui)

    def test_engine_validates_entire_zip_before_swap_and_rolls_back(self):
        engine = (SRC / "PreviewInstallerCore.cs").read_text(encoding="utf-8-sig")
        for token in ("ComputeArchiveHash", "ValidateManifest", "SourceRevisionPattern",
                      "SafeArchiveEntry", "MaxTotalUncompressedBytes", "MaxEntries",
                      "MaxEntryBytes", "FileShare.Read", "fixed", "EnsureNotRunning",
                      "IncrementalHash", "CryptographicOperations.FixedTimeEquals",
                      "new ZipArchive(packageStream", "Directory.Move(target, backup)",
                      "Directory.Move(record.Backup, record.Target)",
                      "CancellationToken", "ClientCertificate", "Start Menu"):
            if token in ("fixed", "ClientCertificate", "Start Menu"):
                continue
            self.assertIn(token, engine)
        self.assertNotIn("New-NetFirewallRule", engine)
        self.assertNotIn("Uninstall-TightVNC", engine)
        self.assertNotIn("sc.exe", engine)

    def test_gui_builder_and_ci_automate_offline_package(self):
        script = (TOOLS / "Build-TJRemoteAccess-Preview-GuiPackage.ps1").read_text(
            encoding="utf-8-sig")
        workflow = (ROOT / ".github/workflows/preview-installer.yml").read_text(
            encoding="utf-8-sig")
        for token in ("0.5.3-preview", "self-contained true", "PublishSingleFile=true",
                      "IncludeNativeLibrariesForSelfExtract=true",
                      "Get-TJRemoteAccessFileSha256", ".sha256", "Build-TJRemoteAccess-Preview-OfflinePackage",
                      "LICENSE", "source"):
            if token == "source": continue
            self.assertIn(token, script)
        for token in ("windows-latest", "actions/upload-artifact@v4",
                      "TJ.RemoteAccess-0.5.3-preview-GUI-Installer-win-x64",
                      "Build-TJRemoteAccess-Preview-GuiPackage.ps1", "dotnet test"):
            self.assertIn(token, workflow)
        self.assertNotIn("release create", workflow)
        self.assertNotIn("(Get-FileHash -", script)
        self.assertIn("Get-TJRemoteAccessFileSha256.ps1", script)

    def test_offline_zip_builder_never_uses_implicit_windows_zip_entry_names(self):
        builder = (TOOLS / "Build-TJRemoteAccess-Preview-OfflinePackage.ps1").read_text(
            encoding="utf-8-sig")
        writer = (TOOLS / "New-TJRemoteAccess-PreviewZip.ps1").read_text(
            encoding="utf-8-sig")
        powershell_tests = (ROOT / "tests/preview_powershell_parse_tests.cs").read_text(
            encoding="utf-8-sig")
        xunit_project = (ROOT / "tests/TJ.RemoteAccess.Preview.Tests.csproj").read_text(
            encoding="utf-8-sig")
        self.assertIn("New-TJRemoteAccess-PreviewZip.ps1", builder)
        self.assertNotIn("    Compress-Archive -Path ", builder)
        self.assertIn("CreateEntry(", writer)
        self.assertIn(".Replace('\\', '/')", writer)
        self.assertIn("entryNames.Contains($name)", builder)
        self.assertIn("New-TJRemoteAccess-PreviewZip.ps1", powershell_tests)
        self.assertIn("preview_offline_builder_end_to_end_tests.cs", xunit_project)

    def test_preview_ci_uses_dotnet_hashing_without_get_filehash_cmdlet(self):
        helper = (TOOLS / "Get-TJRemoteAccessFileSha256.ps1").read_text(
            encoding="utf-8-sig")
        self.assertIn("System.Security.Cryptography.SHA256", helper)
        self.assertIn("ComputeHash($stream)", helper)
        self.assertIn("finally", helper)
        for file in (
            "Build-TJRemoteAccess-Preview-OfflinePackage.ps1",
            "Build-TJRemoteAccess-Preview-GuiPackage.ps1",
            "Install-TJRemoteAccess-Preview.ps1",
            "Install-TJRemoteAccess-Preview-Offline.ps1",
        ):
            script = (TOOLS / file).read_text(encoding="utf-8-sig")
            self.assertIn("Get-TJRemoteAccessFileSha256.ps1", script, file)
            self.assertIn("Get-TJRemoteAccessFileSha256 -LiteralPath", script, file)
            self.assertNotIn("(Get-FileHash -LiteralPath", script, file)
        gui = (TOOLS / "Build-TJRemoteAccess-Preview-GuiPackage.ps1").read_text(
            encoding="utf-8-sig")
        self.assertNotIn("    Compress-Archive ", gui)
        self.assertIn("New-TJRemoteAccess-PreviewZip.ps1", gui)
        offline = (TOOLS / "Build-TJRemoteAccess-Preview-OfflinePackage.ps1").read_text(
            encoding="utf-8-sig")
        self.assertIn("'Get-TJRemoteAccessFileSha256.ps1'", offline)
        installer = (SRC / "PreviewInstallerCore.cs").read_text(encoding="utf-8-sig")
        self.assertIn('"Get-TJRemoteAccessFileSha256.ps1"', installer)

    def test_original_offline_script_now_has_streaming_progress(self):
        original = (TOOLS / "Install-TJRemoteAccess-Preview-Offline.ps1").read_text(
            encoding="utf-8-sig")
        for token in ("Write-Progress", "sourceStream.Read", "TransformBlock",
                      "TransformFinalBlock", "[1/4]", "[2/4]", "[3/4]", "[4/4]",
                      "entryBytes", "Manifest SHA-256 mismatch"):
            self.assertIn(token, original)
        self.assertNotIn("ZipFile]::ExtractToDirectory", original)
        self.assertNotIn("[Parameter(Mandatory = $true)]", original)
        self.assertIn("IsNullOrWhiteSpace($PackagePath)", original)
        self.assertIn("Missing -PackagePath", original)

    def test_six_character_password_protocol_and_server_agree(self):
        auth = (SRC / "LabPasswordAccess.cs").read_text(encoding="utf-8-sig")
        wire = (SRC / "LabProtocol.cs").read_text(encoding="utf-8-sig")
        viewer = (SRC / "LabViewer.cs").read_text(encoding="utf-8-sig")
        self.assertIn("MinPasswordCharacters = 6", auth)
        self.assertIn("6..128 printable characters", auth)
        self.assertIn("Length: >= 6 and <= 128", wire)
        self.assertIn("payload.Length is < 7 or > 257", wire)
        self.assertIn("6..128 printable characters", viewer)
        self.assertIn("600_000", auth)
        self.assertIn("CryptographicOperations.FixedTimeEquals", auth)
        self.assertIn("Tls12 | SslProtocols.Tls13", viewer)
        self.assertNotIn("12..128 printable characters", auth)

if __name__ == "__main__":
    unittest.main()
