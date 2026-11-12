# SPDX-License-Identifier: GPL-2.0-or-later
"""Offline fail-closed checks for the source-fork transformation rules.

These tests use synthetic *strings*, not third-party source code. Running
them never installs, launches or configures any VNC executable.
"""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from prepare_m2_fork import (  # noqa: E402
    edit_runtime_literals, insert_target_name, retarget_vs2022,
)
from vendor_upstream import SourceImportError  # noqa: E402


class M2ForkTransformationRules(unittest.TestCase):
    def test_preserve_include_directives_and_source_identifiers(self):
        source = '#include "TightVNC.h"\nint tvnserver = 1;\n'
        result, counts = edit_runtime_literals(source)
        self.assertEqual(result, source)
        self.assertEqual(counts["tvnserver"], 0)

    def test_service_registry_and_viewer_literals(self):
        source = (
            'const char* a = "tvnserver";\n'
            'const wchar_t* b = L"tvnviewer.exe";\n'
            'const char* c = "Software\\\\TightVNC\\\\Server";\n'
            'const char* d = "TightVNC_Service_Control";\n'
        )
        result, counts = edit_runtime_literals(source)
        self.assertIn('"TJ.RemoteAccess.Server"', result)
        self.assertIn('L"TJ.RemoteAccess.Viewer.exe"', result)
        self.assertIn('"Software\\\\TJ.RemoteAccess\\\\Server"', result)
        self.assertIn('"TJ.RemoteAccess_Service_Control"', result)
        self.assertEqual(counts["tvnserver"], 1)
        self.assertEqual(counts["tvnviewer"], 1)
        self.assertEqual(counts["TightVNC"], 2)

    def test_resource_file_names_remain_unchanged(self):
        source = 'ICON "TightVNC.ico"\nconst char* note = "TightVNC Server";\n'
        result, _ = edit_runtime_literals(source)
        self.assertIn('"TightVNC.ico"', result)
        self.assertIn('"TJ.RemoteAccess Server"', result)

    def test_original_project_file_names_unmodified(self):
        project = (
            '<Project DefaultTargets="Build">\n'
            '  <Import Project="$(VCTargetsPath)\\Microsoft.Cpp.targets" />\n'
            '</Project>\n'
        )
        output = insert_target_name(project, "TJ.RemoteAccess.Server")
        self.assertEqual(output.count("<TargetName>TJ.RemoteAccess.Server</TargetName>"), 1)
        self.assertIn("Microsoft.Cpp.targets", output)

    def test_no_target_import_fails_closed(self):
        with self.assertRaises(SourceImportError):
            insert_target_name("<Project></Project>", "TJ.RemoteAccess.Server")

    def test_retarget_changes_only_derived_vs_settings(self):
        source = (
            "<PlatformToolset>v141_xp</PlatformToolset>"
            "<WindowsTargetPlatformVersion>8.1</WindowsTargetPlatformVersion>"
        )
        result, changed = retarget_vs2022(source)
        self.assertEqual(changed, 2)
        self.assertIn("<PlatformToolset>v143</PlatformToolset>", result)
        self.assertIn("<WindowsTargetPlatformVersion>10.0</WindowsTargetPlatformVersion>", result)

    def test_unrelated_identity_is_not_replaced_in_bare_code(self):
        source = "TightVNCServiceName(); // tvnserver\n"
        result, _ = edit_runtime_literals(source)
        self.assertEqual(result, source)

    def test_empty_input_is_stable(self):
        result, counts = edit_runtime_literals("")
        self.assertEqual(result, "")
        self.assertTrue(all(n == 0 for n in counts.values()))

    def test_isolate_cross_process_window_and_ipc_identifiers(self):
        source = (
            'const char* a = "TvnWindowsApplicationClass";\n'
            'const char* b = "TvnApplicationClass";\n'
            'const char* c = "TvnWindowClass";\n'
            'const char* d = "TvnHookLoaderWindowClass";\n'
            'const char* e = "Tvnserver.Clipboard.MessageWindow";\n'
            'const char* f = "TVN_log_pipe_public_name";\n'
            'const char* g = "TVN.HOOK.MESSAGE.CODE";\n'
        )
        rewritten, counts = edit_runtime_literals(source)
        for new_name in (
            "TJRA.WindowsApplicationClass", "TJRA.ViewerApplicationClass",
            "TJRA.ViewerWindowClass", "TJRA.HookLoaderWindowClass",
            "TJRA.ClipboardMessageWindow", "TJRA_log_pipe_public_name",
            "TJRA.HOOK.MESSAGE.CODE",
        ):
            self.assertIn(new_name, rewritten)
        self.assertEqual(counts["TvnWindowsApplicationClass"], 1)
        self.assertEqual(counts["TVN_log_pipe_public_name"], 1)
        self.assertNotIn("Tvnserver.Clipboard.MessageWindow", rewritten)

    def test_preserve_original_vendor_website_attribution(self):
        source = (
            'const char* site = "www.TightVNC.com";\n'
            'const char* link = "<A HREF=\\\"http://www.tightvnc.com/\\\">www.TightVNC.com</A>";\n'
        )
        rewritten, counts = edit_runtime_literals(source)
        self.assertEqual(rewritten, source)
        self.assertEqual(counts["TightVNC"], 0)


if __name__ == "__main__":
    unittest.main()
