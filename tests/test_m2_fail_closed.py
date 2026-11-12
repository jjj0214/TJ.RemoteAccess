# SPDX-License-Identifier: GPL-2.0-or-later
"""Synthetic-only checks for mandatory deny-by-default M2 fork interlocks."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from m2_fail_closed import EXPECTED_IDS, INTERLOCKS, apply_interlocks  # noqa: E402
from prepare_m2_fork import DEFAULT_SOURCE, decode_plain  # noqa: E402
from vendor_upstream import SourceImportError  # noqa: E402

SOURCE = """void TvnServer::restartHttpServer()
{
  open_http();
}

void TvnServer::restartControlServer()
{
  open_unsafe_pipe();
}

void TvnServer::restartMainRfbServer()
{
  open_rfb();
}

void TvnServer::stopHttpServer()
{
}
"""


class M2FailClosedTests(unittest.TestCase):
    def test_generated_server_removes_all_legacy_listener_creation(self):
        changed, applied = apply_interlocks("tvnserver-app/TvnServer.cpp", SOURCE)
        self.assertEqual(set(applied), {
            "main-rfb-listener", "legacy-http-listener",
            "legacy-control-pipe",
        })
        self.assertNotIn("open_rfb();", changed)
        self.assertNotIn("open_http();", changed)
        self.assertNotIn("open_unsafe_pipe();", changed)
        self.assertIn("stopMainRfbServer();", changed)
        self.assertIn("stopHttpServer();", changed)
        self.assertIn("stopControlServer();", changed)
        self.assertIn("open_rfb();", SOURCE)

    def test_removed_interlock_target_fails_closed(self):
        broken = SOURCE.replace("void TvnServer::stopHttpServer()",
                                "void TvnServer::renamedMethod()")
        with self.assertRaises(SourceImportError):
            apply_interlocks("tvnserver-app/TvnServer.cpp", broken)

    def test_duplicate_method_fails_closed(self):
        duplicated = SOURCE.replace(
            "void TvnServer::stopHttpServer()",
            "void TvnServer::restartMainRfbServer()\nvoid TvnServer::stopHttpServer()",
        )
        with self.assertRaises(SourceImportError):
            apply_interlocks("tvnserver-app/TvnServer.cpp", duplicated)

    def test_crlf_line_endings_are_preserved(self):
        original = SOURCE.replace("\n", "\r\n")
        changed, applied = apply_interlocks("tvnserver-app/TvnServer.cpp", original)
        self.assertEqual(len(applied), 3)
        self.assertEqual(changed.count("\n"), changed.count("\r\n"))
        self.assertIn("legacy RFB listener disabled", changed)

    def test_unrelated_source_remains_unchanged(self):
        changed, applied = apply_interlocks("rfb/MsgDefs.cpp", SOURCE)
        self.assertEqual(changed, SOURCE)
        self.assertEqual(applied, [])

    def test_interlock_manifest_requires_all_nine_gates(self):
        self.assertEqual(EXPECTED_IDS, {
            "main-rfb-listener", "legacy-http-listener",
            "legacy-control-pipe", "extra-rfb-reload",
            "extra-rfb-startup", "legacy-session-admission",
            "legacy-keyboard-input", "legacy-pointer-input", "legacy-rfb-file-transfer",
        })


    def test_real_upstream_methods_accept_pinned_security_transform(self):
        # Read only; no source regeneration, native process, or service launch.
        if not DEFAULT_SOURCE.is_dir():
            self.skipTest("Original GPL source not present on this machine")
        for relative, entries in INTERLOCKS.items():
            path = DEFAULT_SOURCE / relative
            self.assertTrue(path.is_file(), relative)
            decoded = decode_plain(path)
            self.assertIsNotNone(decoded, relative)
            changed, applied = apply_interlocks(relative, decoded[0])
            expected_count = len(entries) + (1 if relative == "rfb-sconn/RfbClient.cpp" else 0)
            self.assertEqual(len(applied), expected_count)
            self.assertNotEqual(changed, decoded[0])

if __name__ == "__main__":
    unittest.main()
