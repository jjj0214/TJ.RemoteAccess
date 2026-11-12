# SPDX-License-Identifier: GPL-2.0-or-later
"""Persistent, real-source fork acceptance test on the JJJ Dev test workstation.

Unlike synthetic unit tests, this verifies the exact upstream SHA-256,
generates a *new* deterministic fork only if absent, and verifies the source
interlocks and manifest. Never runs a server/service, edits the pristine tree,
or overwrites the old fork.

The existing Build-M2Fork.ps1 is the local source of the GPL ZIP location.
No company source code or ZIP is read into the public Git source tree.
"""
from __future__ import annotations

import json
import re
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from prepare_m2_fork import DEFAULT_SOURCE, prepare  # noqa: E402
from vendor_upstream import EXPECTED_SHA256, checksum  # noqa: E402
from m2_fail_closed import EXPECTED_IDS  # noqa: E402
from audit_m2_fork_security import audit_tree  # noqa: E402
from m2_hook_namespace import EXPECTED_HOOK_IDS  # noqa: E402

FORK = ROOT / "out" / "m2-fork" / "tightvnc-2.8.90-tjra-guarded-v4"
DEFAULT_ZIP_PARAM = re.compile(
    r"(?m)^\s*\[string\]\s*\$TightVncSourceZip\s*=\s*'([^']+)'"
)


class RealSourceForkAcceptance(unittest.TestCase):
    def test_prepare_and_audit_isolated_guarded_fork(self):
        # This is intentionally restricted to JJJ Dev: only generate if both
        # exact pinned archive and verified extracted source are present.
        script = (ROOT / "tools" / "Build-M2Fork.ps1").read_text(encoding="utf-8-sig")
        match = DEFAULT_ZIP_PARAM.search(script)
        self.assertIsNotNone(match, "No pinned upstream ZIP configured in build workflow")
        zip_path = Path(match.group(1))
        self.assertTrue(zip_path.is_file(), f"Missing public GPL ZIP: {zip_path}")
        self.assertEqual(checksum(zip_path).lower(), EXPECTED_SHA256.lower())
        self.assertTrue(DEFAULT_SOURCE.is_dir(), "Pristine GPL source must already exist")
        self.assertFalse(FORK.is_symlink(), "Refusing reparse point output directory")

        if FORK.exists():
            self.assertTrue((FORK / "TJRA_FORK_MANIFEST.json").is_file(),
                            "Refusing incomplete old fork")
        else:
            result = prepare(zip_path, DEFAULT_SOURCE, FORK, retarget=True)
            self.assertEqual(result["status"], "TEST_FORK_NOT_SECURITY_APPROVED")

        manifest = json.loads((FORK / "TJRA_FORK_MANIFEST.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["status"], "TEST_FORK_NOT_SECURITY_APPROVED")
        self.assertEqual(manifest["source_archive_sha256"].lower(), EXPECTED_SHA256.lower())
        self.assertEqual(set(manifest["mandatory_interlocks"]), EXPECTED_IDS)
        self.assertEqual(set(manifest["hook_artifact_isolation"]), EXPECTED_HOOK_IDS)
        self.assertFalse(manifest["legacy_listeners_enabled"])
        self.assertFalse(manifest["production_deployment_allowed"])
        self.assertTrue(manifest["retargeted_v143"])
        self.assertIn("main-rfb-listener", manifest["mandatory_interlocks"])

        server = (FORK / "upstream" / "tvnserver-app" / "TvnServer.cpp").read_text(
            encoding="utf-8", errors="replace")
        for marker in (
            "TJRA M2: legacy RFB listener disabled",
            "TJRA M2: legacy HTTP listener disabled",
            "TJRA M2: legacy untrusted control pipe disabled",
        ):
            self.assertIn(marker, server)
        self.assertNotIn("m_rfbServer = new RfbServer(", server)
        self.assertNotIn("m_httpServer = new HttpServer(", server)
        self.assertNotIn("m_controlServer = new ControlServer(", server)

        audit = audit_tree(FORK / "upstream")
        self.assertEqual(audit["generationInterlocks"], "SOURCE_ONLY_PRESENT")
        self.assertEqual(audit["missingInterlocks"], [])
        self.assertFalse(audit["deployAllowed"])
        naming = (FORK / "upstream" / "tvnserver-app" / "NamingDefs.cpp").read_text(
            encoding="utf-8", errors="replace")
        installer = (FORK / "upstream" / "desktop" / "HookInstaller.cpp").read_text(
            encoding="utf-8", errors="replace")
        self.assertIn('"TJ.RemoteAccess.HookLoader.exe"', naming)
        self.assertIn('"TJ.RemoteAccess.ScreenHooks64.dll"', installer)
        self.assertIn('"TJ.RemoteAccess.ScreenHooks32.dll"', installer)
        loader = (FORK / "upstream" / "hookldr" / "hookldr.vcxproj").read_text()
        self.assertIn("<TargetName>TJ.RemoteAccess.HookLoader</TargetName>", loader)
        dll_project = (FORK / "upstream" / "screen-hooks" / "screenhooks.vcxproj").read_text()
        self.assertIn("TJ.RemoteAccess.ScreenHooks64</TargetName>", dll_project)
        for project in (
            "tvnserver/tvnserver.vcxproj",
            "tvnviewer/tvnviewer.vcxproj",
            "tvncontrol-app/tvncontrol-app.vcxproj",
            "hookldr/hookldr.vcxproj",
            "screen-hooks/screenhooks.vcxproj",
        ):
            self.assertTrue((FORK / "upstream" / project).is_file(), project)


if __name__ == "__main__":
    unittest.main()
