# SPDX-License-Identifier: GPL-2.0-or-later
"""Compact native installer regressions.

Contract checks intentionally do NOT pretend to install native software.
Real Windows .NET 10 AppHost/installer install-update-uninstall smoke runs
only in the compact-installer.yml GitHub-hosted Windows job.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "tools" / "Build-TJRemoteAccess-CompactInstaller.ps1"
ISS = ROOT / "tools" / "TJ.RemoteAccess.iss"
CI = ROOT / ".github" / "workflows" / "compact-installer.yml"


class CompactNativeInstallerContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.script = SCRIPT.read_text(encoding="utf-8-sig")
        cls.iss = ISS.read_text(encoding="utf-8-sig")
        cls.ci = CI.read_text(encoding="utf-8-sig")

    def test_one_native_executable_no_self_contained_app_duplication(self):
        for token in (
            "Inno Setup creates", "OutputBaseFilename=TJ.RemoteAccess-",
            "Compression=lzma2", "SolidCompression=yes",
            "PrivilegesRequired=lowest", "DefaultDirName={localappdata}",
            "AppId={{", "UninstallDisplayName=", "[Components]",
            "Uninstall TJ.RemoteAccess", "[Files]",
        ):
            self.assertIn(token, self.iss)
        self.assertIn("TJ.RemoteAccess.iss", self.script)
        self.assertIn("-p:TargetFramework=net10.0-windows", self.script)
        self.assertIn("'--self-contained','false'", self.script)
        self.assertIn("-p:PublishSingleFile=false", self.script)
        self.assertIn("-p:AppHostDotNetSearch=AppRelative", self.script)
        self.assertIn("-p:AppHostRelativeDotNet=../Runtime", self.script)
        self.assertNotIn("-p:PublishTrimmed=true", self.script)
        self.assertNotIn("-p:PublishReadyToRun=true", self.script)
        self.assertNotIn("PublishSingleFile=true", self.script)
        self.assertNotIn("dotnet publish", self.iss)

    def test_desktop_runtime_is_embedded_once_and_never_global_installed(self):
        self.assertEqual(
            self.iss.count('Source: "{#RepoRoot}\\out\\compact-publish\\Runtime\\*"'), 1)
        for component in ("Server", "Viewer", "Setup"):
            self.assertIn("out\\compact-publish\\" + component + "\\*", self.iss)
        self.assertIn("Name: \"runtime\"", self.iss)
        self.assertIn("Flags: fixed", self.iss)
        for token in (
            "WindowsDesktop", "Microsoft.NETCore.App",
            "hostfxr.dll", "Microsoft.WindowsDesktop.App.runtimeconfig.json",
            "$runtimeFiles", "Microsoft .NET Windows Desktop Runtime",
        ):
            self.assertIn(token, self.script)
        for forbidden in ("choco install", "winget install", "Install-Module",
                          "New-NetFirewallRule", "Stop-Service", "Remove-Item $env:LOCALAPPDATA"):
            self.assertNotIn(forbidden, self.script)

    def test_core_and_desktop_runtime_archives_make_one_private_runtime(self):
        # The Desktop ZIP alone has no dotnet.exe: the official Core runtime
        # must be verified independently and merged before the apphost smoke.
        for token in (
            "dotnet-runtime-win-x64.zip",
            "windowsdesktop-runtime-win-x64.zip",
            "$coreAssets = @($release[0].runtime.files",
            "$coreActualSha512 = Sha512-File $coreRuntimeZip",
            "Microsoft Core Runtime ZIP SHA-512 mismatch",
            "ExtractToDirectory($coreRuntimeZip,$runtime)",
            "Copy-Item -LiteralPath $desktopFramework -Destination $desktopShared",
            "runtimeCoreSha512 = $coreActualSha512",
        ):
            self.assertIn(token, self.script)

    def test_net10_restore_has_isolated_assets_per_app(self):
        # The original three projects share one source folder and obj path.
        # .NET 9 test build must NEVER race/overwrite .NET 10 publish assets.
        for token in (
            "out\\compact-obj", "$app", "MSBuildProjectExtensionsPath",
            "BaseIntermediateOutputPath", "dotnet' (",
            "'restore',$project", "'publish',$project",
            "--no-restore", "--force", "'win-x64'",
        ):
            self.assertIn(token, self.script)
        self.assertIn("$msbuild", self.script)

    def test_microsoft_runtime_is_fully_pinned_and_verified(self):
        for token in (
            "RuntimeVersion = '10.0.15'",
            "https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json",
            "windowsdesktop-runtime-win-x64.zip",
            "'win-x64'",
            "Get-TJRemoteAccessFileSha256.ps1",
            "Sha512-File",
            "SHA512",
            "$actualSha512",
            "OrdinalIgnoreCase",
            "runtimeSha512",
            "sourceRevision",
            "runtime-provenance.json",
        ):
            self.assertIn(token, self.script)
        self.assertIn("Microsoft .NET Windows Desktop Runtime", self.script)

        self.assertNotIn("Invoke-Expression", self.script)
        self.assertNotIn("curl | bash", self.script)

    def test_private_runtime_verified_before_source_build(self):
        # Test accesses a LOCAL private runtime, not an ordinary system .NET install.
        for token in (
            "$localSetup", "'service-host','status'",
            "DOTNET_ROOT_X64", "missing-global-dotnet",
            "Run-Checked 'Setup service-host status (read only)'",
            "$config.runtimeOptions.tfm",
        ):
            self.assertIn(token, self.script)
        self.assertIn("source.zip", self.iss)
        self.assertIn("GPL-Source", self.iss)
        self.assertIn("THIRD_PARTY_NOTICES.md", self.iss)
        self.assertIn("git -C $root archive", self.script)


    def test_no_tls_credential_or_tightvnc_mutations(self):
        for token in ("PrivilegesRequired=lowest", "AppPublisherURL=",
                      "CloseApplications=yes", "RestartApplications=no"):
            self.assertIn(token, self.iss)
        for forbidden in ("DeleteFile", "DelTree", "Remove-Item -LiteralPath $env:LOCALAPPDATA",
                          "TightVNC.exe", "sc.exe", "netsh advfirewall", "certutil -delstore"):
            self.assertNotIn(forbidden, self.iss)
            self.assertNotIn(forbidden, self.script)
        self.assertIn("GPL-Source", self.iss)

    def test_actions_perform_real_installer_e2e_and_upload_artifact(self):
        for token in (
            "on:", "windows-latest", "10.0.x", "Inno Setup",
            "Build-TJRemoteAccess-CompactInstaller.ps1",
            "0.5.4-preview", "10.0.15",
            "silent install, upgrade, runtime resolution, uninstall smoke",
            "/VERYSILENT", "/COMPONENTS=viewer,setup,runtime",
            "/COMPONENTS=server,viewer,setup,runtime",
            "DOTNET_ROOT_X64", "service-host",
            "GPL-Source", "unins000.exe",
            "preserve-user-configuration", "actions/upload-artifact@v4",
            "if-no-files-found: error", "compact-size-report.json",
            "runtime-provenance.json",
        ):
            self.assertIn(token.lower(), self.ci.lower())
        self.assertNotIn("dotnet build .\\TJ.RemoteAccess.sln -c Release --nologo --no-restore", self.ci)

    def test_dotnet_10_packaging_uses_consistent_native_argv_on_ci(self):
        # A preceding Windows-hosted run failed with MSB1008, indicating that
        # MSBuild received multiple project arguments. Enforce PS7 native
        # argument passing and keep the exact argv visible for diagnosis.
        self.assertIn("PSNativeCommandArgumentPassing = 'Standard'", self.script)
        self.assertIn('("[argv] " + $executable', self.script)
        self.assertIn("[System.Diagnostics.ProcessStartInfo]::new($executable)", self.script)
        self.assertIn("$startInfo.ArgumentList.Add([string]$argument)", self.script)
        self.assertIn('"-p:BaseIntermediateOutputPath=$obj"', self.script)
        self.assertIn('"-p:MSBuildProjectExtensionsPath=$obj"', self.script)
        self.assertIn("Start-Process -FilePath 'pwsh.exe'", self.ci)
        self.assertIn("shell: pwsh", self.ci)
        self.assertNotIn("Start-Process -FilePath 'powershell.exe'", self.ci)

    def test_final_gate_uploads_source_hash_and_full_install_logs(self):
        self.assertIn("feature/chatgpt-compact-native-verify", self.ci)
        self.assertIn("      - main", self.ci)
        self.assertIn("compact-metrics:", self.ci)
        self.assertIn("installer_sha16", self.ci)
        self.assertIn("id: package_metrics", self.ci)
        self.assertIn("out/compact-dist/source.zip", self.ci)
        self.assertIn("TJRA-Compact-Install-Logs/", self.ci)
        self.assertIn("Audit real compact installer component and license paths", self.ci)
        self.assertIn("Uninstaller left shared runtime behind", self.ci)
        self.assertIn("Required file missing:", self.ci)
        self.assertIn("$rootCauses[-1]", self.ci)
        self.assertIn("if-no-files-found: error", self.ci)
        self.assertIn("compact-size-report.json", self.ci)
        self.assertIn("runtime-provenance.json", self.ci)

    def test_installed_server_first_run_is_lan_ready_and_does_not_rotate_credentials(self):
        setup = (ROOT / 'src/TJRemoteAccess.Core/LabSetup.cs').read_text(encoding='utf-8-sig')
        launch = (ROOT / 'src/TJRemoteAccess.Core/LabServerLaunch.cs').read_text(encoding='utf-8-sig')
        server = (ROOT / 'src/TJRemoteAccess.Core/LabServer.cs').read_text(encoding='utf-8-sig')
        for token in ('init-server', 'InitServerOnly()', 'File.Exists(LabCertificates.ServerFile)',
                      'ListenAddress = "0.0.0.0"', 'AllowedClientSha256 = ""'):
            self.assertIn(token, setup)
        self.assertIn('lan = true;', launch)
        self.assertIn('LabCertificates.ValidatePinned', server)
        self.assertIn('[Run]', self.iss)
        self.assertIn('Parameters: "init-server"; Flags: runhidden waituntilterminated; Components: server', self.iss)
        self.assertIn('Parameters: "--allow-lan"', self.iss)
        self.assertIn('Idempotent Server initializer rotated or modified existing credentials', self.ci)
        self.assertIn('Server TLS certificate lacks private key', self.ci)
        self.assertNotIn('New-NetFirewallRule', self.iss)

    def test_ps_parser_regression_includes_new_builder(self):
        script = (ROOT / "tests/preview_powershell_parse_tests.cs").read_text(
            encoding="utf-8-sig")
        self.assertIn("Build-TJRemoteAccess-CompactInstaller.ps1", script)


if __name__ == "__main__":
    unittest.main()
