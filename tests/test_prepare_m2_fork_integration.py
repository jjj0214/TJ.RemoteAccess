# SPDX-License-Identifier: GPL-2.0-or-later
"""Synthetic end-to-end fork preparation tests with mocked ZIP verification.

The real CLI ALWAYS verifies SHA-256 and every extracted source file. Mocking
is used only here to exercise output safety without bundling third-party code.
"""
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from m2_fail_closed import EXPECTED_IDS  # noqa: E402
from prepare_m2_fork import prepare  # noqa: E402
from vendor_upstream import SourceImportError  # noqa: E402

PROJECT = (
    '<Project DefaultTargets="Build">\n'
    '<PlatformToolset>v141_xp</PlatformToolset>\n'
    '<WindowsTargetPlatformVersion>8.1</WindowsTargetPlatformVersion>\n'
    '<Link><OutputFile>$(OutDir)tvnserver.exe</OutputFile></Link>\n'
    '<Import Project="$(VCTargetsPath)\\Microsoft.Cpp.targets" />\n'
    '</Project>\n'
)


class ForkIntegrationTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.source = self.root / "source"
        self.fork = self.root / "derived"
        self.archive = self.root / "source.zip"
        self.archive.write_bytes(b"synthetic, NOT TightVNC")
        for name in ("tvnserver", "tvnviewer", "tvncontrol-app", "hookldr"):
            project = self.source / name / (name + ".vcxproj")
            project.parent.mkdir(parents=True)
            project.write_text(PROJECT)
        app = self.source / "tvnserver-app" / "NamingDefs.cpp"
        app.parent.mkdir(parents=True)
        app.write_text('const char* n = "tvnserver";\n'
                       'const char* r = "Software\\\\TightVNC\\\\Server";\n')
        with app.open("a", encoding="utf-8") as stream:
            stream.write('const char* window = "TvnWindowsApplicationClass";\n')
            stream.write('HookDefinitions::HOOK_LOADER_NAME[] = _T("hookldr.exe");\n')
        installer = self.source / "desktop" / "HookInstaller.cpp"
        installer.parent.mkdir(parents=True, exist_ok=True)
        installer.write_text(
            'HookInstaller::LIBRARY_NAME[] = _T("screenhooks32.dll");\n'
            'HookInstaller::LIBRARY_NAME[] = _T("screenhooks64.dll");\n')
        dll_project = self.source / "screen-hooks" / "screenhooks.vcxproj"
        dll_project.parent.mkdir(parents=True, exist_ok=True)
        dll_project.write_text(PROJECT.replace("</Project>", "".join(
            "<TargetName Condition='cfg'>$(ProjectName)" + platform + "</TargetName>\n"
            for platform in (["32"] * 4 + ["64"] * 4)) + "</Project>"))
        rfb = self.source / "rfb" / "RfbKeySym.h"
        rfb.parent.mkdir(parents=True)
        rfb.write_text('#include "RfbKeySymListener.h"\nclass RfbKeySym {};\n')
        # Synthetic-only method bodies are placeholders; no Windows listener is built.
        fixtures = {
            "tvnserver-app/TvnServer.cpp": """void TvnServer::restartHttpServer()
{
  old_http();
}

void TvnServer::restartControlServer()
{
  old_pipe();
}

void TvnServer::restartMainRfbServer()
{
  old_rfb();
}

void TvnServer::stopHttpServer()
{
}
""",
            "tvnserver-app/ExtraRfbServers.cpp": """bool ExtraRfbServers::reload(bool asService, RfbClientManager *mgr)
{
  return old_reload();
}

void ExtraRfbServers::shutDown()
{
}

bool ExtraRfbServers::startUp(bool asService, RfbClientManager *mgr)
{
  return old_start();
}

void ExtraRfbServers::getConfiguration(Conf *out)
{
}
""",
            "tvnserver-app/RfbClientManager.cpp": """Desktop *RfbClientManager::onClientAuth(RfbClient *client)
{
  return old_auth();
}

bool RfbClientManager::onCheckForBan(RfbClient *client)
{
  return false;
}
""",
            "rfb-sconn/RfbClient.cpp": """    // FileTransfers initialization
    if (config->isFileTransfersEnabled() &&
        rfbInitializer.getTightEnabledFlag()) {
      fileTransfer = new FileTransferRequestHandler(&codeRegtor, &output, m_desktop, m_log, !m_viewOnly);
      m_log->debug(_T("File transfer has been created"));
    } else {
      m_log->info(_T("File transfer is not allowed"));
    }

void RfbClient::onKeyboardEvent(UINT32 keySym, bool down)
{
  m_desktop->setKeyboardEvent(keySym, down);
}

void RfbClient::onMouseEvent(UINT16 x, UINT16 y, UINT8 buttonMask)
{
  m_desktop->setMouseEvent(x, y, buttonMask);
}

Rect RfbClient::getViewPortRect(const Dimension *fbDimension)
{
}
""",
        }
        for relative, contents in fixtures.items():
            file = self.source / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(contents, encoding="utf-8")
        (self.source / "tightvnc2017.sln").write_text("synthetic solution")

    def fake_verify(self, *_args):
        return {"archive_sha256": "synthetic-only", "files_verified": 5}

    def test_fork_output_pristine_source_and_no_overwrite(self):
        with mock.patch("prepare_m2_fork.verify_tree", side_effect=self.fake_verify):
            result = prepare(self.archive, self.source, self.fork, True)
        self.assertEqual(result["status"], "TEST_FORK_NOT_SECURITY_APPROVED")
        self.assertTrue(self.fork.is_dir())
        out = self.fork / "upstream" / "tvnserver" / "tvnserver.vcxproj"
        build_xml = out.read_text()
        self.assertIn("<PlatformToolset>v143</PlatformToolset>", build_xml)
        self.assertIn("<TargetName>TJ.RemoteAccess.Server</TargetName>", build_xml)
        self.assertIn("$(OutDir)TJ.RemoteAccess.Server.exe", build_xml)
        original = (self.source / "tvnserver" / "tvnserver.vcxproj").read_text()
        self.assertIn("<PlatformToolset>v141_xp</PlatformToolset>", original)
        self.assertNotIn("TJ.RemoteAccess", original)
        manifest = json.loads((self.fork / "TJRA_FORK_MANIFEST.json").read_text())
        self.assertGreater(manifest["replacement_totals"]["tvnserver"], 0)
        self.assertGreater(manifest["replacement_totals"]["TightVNC"], 0)
        self.assertEqual(set(manifest["mandatory_interlocks"]), EXPECTED_IDS)
        self.assertFalse(manifest["legacy_listeners_enabled"])
        self.assertFalse(manifest["production_deployment_allowed"])
        server = (self.fork / "upstream" / "tvnserver-app" / "TvnServer.cpp").read_text()
        upstream_server = (self.source / "tvnserver-app" / "TvnServer.cpp").read_text()
        self.assertIn("old_rfb();", upstream_server)
        self.assertNotIn("old_rfb();", server)
        self.assertNotIn("old_http();", server)
        self.assertNotIn("old_pipe();", server)
        self.assertIn("legacy RFB listener disabled", server)
        self.assertGreater(manifest["replacement_totals"]["TvnWindowsApplicationClass"], 0)
        original_names = (self.source / "tvnserver-app" / "NamingDefs.cpp").read_text()
        derived_names = (self.fork / "upstream" / "tvnserver-app" / "NamingDefs.cpp").read_text()
        self.assertIn('"TvnWindowsApplicationClass"', original_names)
        self.assertIn('"TJRA.WindowsApplicationClass"', derived_names)
        original_rfb = (self.source / "rfb" / "RfbKeySym.h").read_text()
        derived_rfb = (self.fork / "upstream" / "rfb" / "RfbKeySym.h").read_text()
        self.assertNotIn("#include <string>", original_rfb)
        self.assertIn("#include <string>", derived_rfb)
        self.assertEqual(derived_rfb.count("#include <string>"), 1)
        self.assertTrue(any(f["path"] == "rfb/RfbKeySym.h"
                            for f in manifest["modified_files"]))
        with mock.patch("prepare_m2_fork.verify_tree", side_effect=self.fake_verify):
            with self.assertRaises(SourceImportError):
                prepare(self.archive, self.source, self.fork, True)

    def test_original_project_never_retargeted(self):
        with mock.patch("prepare_m2_fork.verify_tree", side_effect=self.fake_verify):
            prepare(self.archive, self.source, self.fork, False)
        src = (self.source / "tvnviewer" / "tvnviewer.vcxproj").read_text()
        derived = (self.fork / "upstream" / "tvnviewer" / "tvnviewer.vcxproj").read_text()
        self.assertIn("<PlatformToolset>v141_xp</PlatformToolset>", src)
        self.assertIn("<PlatformToolset>v141_xp</PlatformToolset>", derived)
        self.assertIn("<TargetName>TJ.RemoteAccess.Viewer</TargetName>", derived)


if __name__ == "__main__":
    unittest.main()
