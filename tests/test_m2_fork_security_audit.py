# SPDX-License-Identifier: GPL-2.0-or-later
"""Offline checks for the read-only M2 security/coexistence audit."""
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from audit_m2_fork_security import (  # noqa: E402
    BLOCKER_PATTERNS, DEFAULT_TREE, NAME_FILES, INTERLOCK_MARKERS, audit_tree,
)


class M2SecurityAuditTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        source_files = {path for _, path, _, _ in BLOCKER_PATTERNS}
        source_files.update(NAME_FILES)
        source_files.update(INTERLOCK_MARKERS)
        for relative in source_files:
            file = self.root / relative
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text("// mock source, no known security marker\n", encoding="utf-8")

    def test_does_not_approve_even_without_known_signatures(self):
        result = audit_tree(self.root)
        self.assertEqual(result["status"], "BLOCKED_FOR_DEPLOYMENT")
        self.assertFalse(result["deployAllowed"])
        self.assertEqual(result["findings"], [])
        self.assertEqual(result["auditedFiles"], 8)
        self.assertEqual(result["generationInterlocks"], "NOT_APPLIED_TO_THIS_FORK")

    def test_detects_raw_listener_keyboard_and_original_window_name(self):
        (self.root / "tvnserver-app/RfbServer.cpp").write_text(
            "void start() { TcpServer::start(); }\n", encoding="utf-8")
        (self.root / "rfb-sconn/RfbClient.cpp").write_text(
            "m_desktop->setKeyboardEvent(sym, down);\n", encoding="utf-8")
        (self.root / "tvnviewer/NamingDefs.cpp").write_text(
            'const char* name = "TvnApplicationClass";\n', encoding="utf-8")
        report = audit_tree(self.root)
        codes = {finding["code"] for finding in report["findings"]}
        self.assertIn("RAW_RFB_TCP_LISTENER", codes)
        self.assertIn("DIRECT_KEYBOARD_INJECTION", codes)
        self.assertIn("SHARED_WIN32_NAME", codes)
        self.assertFalse(report["deployAllowed"])
        for finding in report["findings"]:
            self.assertGreater(finding["line"], 0)

    def test_reports_interlocks_only_when_present_in_generated_source(self):
        missing = audit_tree(self.root)
        self.assertEqual(missing["generationInterlocks"], "NOT_APPLIED_TO_THIS_FORK")
        self.assertTrue(missing["missingInterlocks"])
        for relative, markers in INTERLOCK_MARKERS.items():
            path = self.root / relative
            path.write_text("\n".join(markers) + "\n", encoding="utf-8")
        present = audit_tree(self.root)
        self.assertEqual(present["generationInterlocks"], "SOURCE_ONLY_PRESENT")
        self.assertEqual(present["missingInterlocks"], [])
        self.assertFalse(present["deployAllowed"])

    def test_missing_file_is_blocker(self):
        (self.root / "rfb-sconn/RfbClient.cpp").unlink()
        findings = audit_tree(self.root)["findings"]
        self.assertTrue(any(f["code"] == "SOURCE_FILE_MISSING_OR_UNSAFE"
                            and f["path"] == "rfb-sconn/RfbClient.cpp"
                            for f in findings))

    def test_missing_tree_is_never_allowed(self):
        report = audit_tree(self.root / "missing")
        self.assertFalse(report["deployAllowed"])
        self.assertEqual(report["auditedFiles"], 0)

    def test_current_derived_tree_is_not_mistaken_for_production(self):
        if not DEFAULT_TREE.is_dir():
            self.skipTest("Generated TightVNC fork not present on this machine")
        report = audit_tree(DEFAULT_TREE)
        self.assertEqual(report["status"], "BLOCKED_FOR_DEPLOYMENT")
        self.assertFalse(report["deployAllowed"])
        self.assertGreater(report["auditedFiles"], 0)
        self.assertEqual(report["generationInterlocks"], "SOURCE_ONLY_PRESENT")
        codes = {f["code"] for f in report["findings"]}
        self.assertNotIn("DIRECT_KEYBOARD_INJECTION", codes)
        self.assertNotIn("DIRECT_MOUSE_INJECTION", codes)
        self.assertNotIn("LEGACY_FILE_TRANSFER", codes)
        self.assertEqual(codes, {
            "RAW_RFB_TCP_LISTENER",
            "LEGACY_RFB_ADMISSION",
            "SHARED_MIRROR_DRIVER_WINDOW",
        })


if __name__ == "__main__":
    unittest.main()
