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
import os
import re
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from prepare_m2_fork import DEFAULT_SOURCE, prepare  # noqa: E402
from vendor_upstream import EXPECTED_SHA256, checksum, import_source  # noqa: E402
from verify_upstream_tree import verify_tree  # noqa: E402
from m2_fail_closed import EXPECTED_IDS  # noqa: E402
from audit_m2_fork_security import audit_tree  # noqa: E402
from m2_hook_namespace import EXPECTED_HOOK_IDS  # noqa: E402

FORK = ROOT / "out" / "m2-fork" / "tightvnc-2.8.90-tjra-guarded-v4"
DEFAULT_ZIP_PARAM = re.compile(
    r"(?m)^\s*\[string\]\s*\$TightVncSourceZip\s*=\s*'([^']+)'"
)


def missing_workstation_source_skip_reason(zip_path, pristine_source, fork, require_real):
    """Skip ONLY when every machine-provisioned GPL input is absent.

    If ANY expected directory/archive exists (even partially), a failed
    SHA-256/source/interlock check must remain a genuine failure. Dedicated
    Windows source acceptance sets TJRA_REQUIRE_REAL_SOURCE=1 to fail on
    missing inputs instead of skipping. Portable GitHub runners lack those
    locally provisioned, non-redistributed source files.
    """
    if require_real or zip_path.is_file() or pristine_source.is_dir() or fork.exists():
        return None
    return ("Real TightVNC source acceptance requires the pinned local GPL "
            "archive and verified extracted tree on the dedicated JJJ Dev "
            "workstation. Set TJRA_REQUIRE_REAL_SOURCE=1 to enforce this gate.")


class RealSourcePrerequisitePolicyTests(unittest.TestCase):
    def test_only_completely_unprovisioned_portable_environment_skips(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            archive = root / "not-provisioned.zip"
            source = root / "verified-source"
            fork = root / "derived"
            self.assertIsNotNone(
                missing_workstation_source_skip_reason(archive, source, fork, False))
            self.assertIsNone(
                missing_workstation_source_skip_reason(archive, source, fork, True))
            source.mkdir()
            self.assertIsNone(
                missing_workstation_source_skip_reason(archive, source, fork, False))
            source.rmdir()
            fork.mkdir()
            self.assertIsNone(
                missing_workstation_source_skip_reason(archive, source, fork, False))
            fork.rmdir()
            archive.write_bytes(b"incomplete")
            self.assertIsNone(
                missing_workstation_source_skip_reason(archive, source, fork, False))


class RealSourceForkAcceptance(unittest.TestCase):
    def test_prepare_and_audit_isolated_guarded_fork(self):
        # A SHA-pinned local GPL archive is mandatory. Each linked worktree may
        # import its own pristine copy, but never trust unverified source bytes.
        script = (ROOT / "tools" / "Build-M2Fork.ps1").read_text(encoding="utf-8-sig")
        match = DEFAULT_ZIP_PARAM.search(script)
        self.assertIsNotNone(match, "No pinned upstream ZIP configured in build workflow")
        zip_path = Path(match.group(1))
        reason = missing_workstation_source_skip_reason(
            zip_path, DEFAULT_SOURCE, FORK,
            os.environ.get("TJRA_REQUIRE_REAL_SOURCE") == "1")
        if reason:
            self.skipTest(reason)
        self.assertTrue(zip_path.is_file(), f"Missing public GPL ZIP: {zip_path}")
        self.assertEqual(checksum(zip_path).lower(), EXPECTED_SHA256.lower())
        if not DEFAULT_SOURCE.exists():
            # Separate Git worktrees do not share ignored third_party assets.
            # Import only a SHA-256 verified local archive; never auto-download.
            import_source(zip_path, DEFAULT_SOURCE)
        proof = verify_tree(zip_path, DEFAULT_SOURCE)
        self.assertTrue(proof["exact_match"], "Pristine GPL source differs from the pinned ZIP")
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
