# SPDX-License-Identifier: GPL-2.0-or-later
"""M3/M4/M5 follow-up contract tests: fail closed, no service/UAC claims."""
import pathlib
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
CORE = ROOT / "src" / "TJRemoteAccess.Core"
TOOLS = ROOT / "tools"


class M3M4HardeningTests(unittest.TestCase):
    def test_m4_external_viewer_uses_existing_pinned_profile_only(self):
        launch = (CORE / "LabViewerLaunch.cs").read_text(encoding="utf-8")
        ui = (CORE / "LabViewer.cs").read_text(encoding="utf-8")
        setup = (CORE / "LabSetup.cs").read_text(encoding="utf-8")
        for term in ('--profile-id', '--connect', 'Guid.TryParseExact',
                     'throw new InvalidOperationException', 'Resolve('):
            self.assertIn(term, launch)
        self.assertIn('launch.Resolve(store.Load())', ui)
        self.assertIn('Shown += async (_, _) => await ConnectAsync()', ui)
        self.assertIn('ListProfilesJson()', setup)
        self.assertIn('list-profiles-json', setup)
        self.assertNotIn('p.ClientCertificateThumbprint\n', setup.split('private static int ListProfilesJson()')[1].split('private static string ReadSecret')[0])
        self.assertNotIn('PfxPassword', setup.split('private static int ListProfilesJson()')[1].split('private static string ReadSecret')[0])
        self.assertNotIn('"--password"', launch)

    def test_m3_transport_restricts_named_pipes_and_pins_kernel_identity(self):
        pipe = (CORE / "M3WindowsPipe.cs").read_text(encoding="utf-8")
        for term in ('PIPE_REJECT_REMOTE_CLIENTS',
                     'CreateNamedPipeW', 'ConvertStringSecurityDescriptorToSecurityDescriptorW',
                     'GetNamedPipeClientProcessId', 'GetNamedPipeClientSessionId',
                     'launchedAgentPid', 'expectedSessionId',
                     'D:P(A;;GA;;;SY)(A;;GRGW;;;'):
            self.assertIn(term, pipe)
        for term in ("GetTokenLogonSid", "GetTokenInformation",
                     "GroupLogonId", "specificLogonSid",
                     "Same-account parallel logons are excluded"):
            self.assertIn(term, pipe)
        self.assertIn("exact interactive LOGON SID", pipe)
        self.assertNotIn(';;;WD', pipe)  # Everyone SID
        self.assertNotIn(';;;AU', pipe)  # Authenticated Users
        protocol = (CORE / "M3IpcProtocol.cs").read_text(encoding="utf-8")
        for term in ('MaxPacketPayload = 1024', 'HMACSHA256.HashData',
                     'FixedTimeEquals', 'AuthenticateParentAsync',
                     'AuthenticateChildAsync',
                     'expectedPid', 'expectedSessionId'):
            self.assertIn(term, protocol)
        self.assertNotIn('RemoteDesktopInput', protocol)
        self.assertNotIn('ExecuteCommand', protocol)

    def test_lock_and_logout_revoke_current_lab_sessions(self):
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8")
        for term in ('SystemEvents.SessionSwitch +=',
                     'SystemEvents.SessionSwitch -=',
                     'SessionSwitchReason.SessionLock',
                     'SessionSwitchReason.SessionLogoff',
                     'SessionSwitchReason.ConsoleDisconnect',
                     'SessionSwitchReason.RemoteDisconnect',
                     'StopServer();'):
            self.assertIn(term, server)
        self.assertNotIn('SessionSwitchReason.SessionUnlock or', server)
        self.assertIn('LabServerLaunchOptions.Parse(args)', server)

    def test_current_user_auto_start_is_explicit_reversible_and_not_system(self):
        startup = (TOOLS / "Set-TJRemoteAccess-Lab-Startup.ps1").read_text(encoding="utf-8-sig")
        for term in ("[ValidateSet('Status', 'Enable', 'Disable')]",
                     'New-ScheduledTaskPrincipal',
                     '-LogonType Interactive -RunLevel Limited',
                     'AtLogOn', 'Unregister-ScheduledTask',
                     'server.json', 'TryParse', 'IsLoopback',
                     "'--allow-lan --auto-start'"):
            self.assertIn(term, startup)
        for dangerous in ('-UserId SYSTEM', '-RunLevel Highest',
                          'New-NetFirewallRule', 'sc.exe create',
                          'Set-ItemProperty'):
            self.assertNotIn(dangerous, startup)

    def test_bounded_audit_is_nonblocking_and_never_logs_screen_or_keys(self):
        audit = (CORE / "LabAuditWriter.cs").read_text(encoding="utf-8")
        server = (CORE / "LabServer.cs").read_text(encoding="utf-8")
        for term in ('Channel.CreateBounded', 'TryWrite', 'maxBytes',
                     'Rotate()', 'LogFile', 'access.jsonl',
                     'FullMode = BoundedChannelFullMode.Wait'):
            self.assertIn(term, audit)
        self.assertIn('_audit.TryRecord("LabServer", value)', server)
        self.assertNotIn('JpegFrame', audit)
        self.assertNotIn('KeyDown', audit)
        self.assertNotIn('PasswordLogin', audit)

    def test_new_sources_are_in_existing_release_and_xunit_builds(self):
        def names(project):
            return {node.attrib.get("Include")
                    for node in ET.parse(project).getroot().iter("Compile")}
        server = names(CORE / "TJ.RemoteAccess.Lab.Server.csproj")
        viewer = names(CORE / "TJ.RemoteAccess.Lab.Viewer.csproj")
        tests = names(ROOT / "tests" / "TJ.RemoteAccess.Lab.Tests.csproj")
        self.assertTrue({"LabServerLaunch.cs", "LabAuditWriter.cs"} <= server)
        self.assertIn("LabViewerLaunch.cs", viewer)
        self.assertTrue({"lab_audit_tests.cs", "lab_viewer_launch_tests.cs",
                         "lab_server_launch_tests.cs", "m3_ipc_security_tests.cs"} <= tests)
        self.assertIn(r"..\src\TJRemoteAccess.Core\M3WindowsPipe.cs", tests)


if __name__ == "__main__":
    unittest.main()
