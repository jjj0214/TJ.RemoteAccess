# SPDX-License-Identifier: GPL-2.0-or-later
"""Read-only artifact and developer-PowerShell workflow contract tests.

This does not run PowerShell, native EXEs, VNC sessions, services or DLL hooks.
All actual Windows builds are separately run through JJJ Dev project_build.
"""
from __future__ import annotations

import hashlib
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
V3 = ROOT / "out" / "m2-fork" / "tightvnc-2.8.90-tjra-guarded-v4" / "upstream"

EXECUTABLES = (
    "tvnserver/x64/Release/TJ.RemoteAccess.Server.exe",
    "tvnviewer/x64/Release/TJ.RemoteAccess.Viewer.exe",
    "hookldr/x64/Release/TJ.RemoteAccess.HookLoader.exe",
    "screen-hooks/x64/Release/TJ.RemoteAccess.ScreenHooks64.dll",
)
STATIC_LIBRARY = "tvncontrol-app/x64/Release/TJ.RemoteAccess.Control.lib"


class M2WindowsArtifactContract(unittest.TestCase):
    def test_power_shell_build_script_targets_safe_v3_fork(self):
        script = (ROOT / "tools" / "Build-M2Fork.ps1").read_text(encoding="utf-8-sig")
        self.assertIn("tightvnc-2.8.90-tjra-guarded-v4", script)
        self.assertIn("requiredInterlocks", script)
        self.assertIn("requiredHooks", script)
        self.assertIn("Refusing stale fork reuse", script)
        self.assertIn("Refusing reuse", script)
        self.assertIn("production_deployment_allowed", script)
        self.assertIn("M2-security-audit.json", script)

    def test_real_generated_artifacts_have_distinct_binary_names(self):
        if not V3.exists():
            self.skipTest("Generated v3 fork has not been prepared")
        existing = [V3 / name for name in EXECUTABLES]
        if not any(file.is_file() for file in existing):
            self.skipTest("Build binaries not yet produced; compile first")

        hashes = set()
        for relative in EXECUTABLES:
            path = V3 / relative
            self.assertTrue(path.is_file(), relative)
            self.assertGreater(path.stat().st_size, 64 * 1024, relative)
            with path.open("rb") as stream:
                self.assertEqual(stream.read(2), b"MZ", relative)
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            self.assertNotIn(digest, hashes, "Duplicate binary content")
            hashes.add(digest)
        library = V3 / STATIC_LIBRARY
        self.assertTrue(library.is_file(), STATIC_LIBRARY)
        with library.open("rb") as stream:
            self.assertEqual(stream.read(8), b"!<arch>\n")

    def test_legacy_named_hook_binaries_are_not_new_output_targets(self):
        if not V3.exists():
            self.skipTest("Derived v3 fork absent")
        loader_project = (V3 / "hookldr" / "hookldr.vcxproj").read_text(
            encoding="utf-8", errors="replace")
        hook_project = (V3 / "screen-hooks" / "screenhooks.vcxproj").read_text(
            encoding="utf-8", errors="replace")
        self.assertIn("<TargetName>TJ.RemoteAccess.HookLoader</TargetName>", loader_project)
        self.assertIn("TJ.RemoteAccess.ScreenHooks64</TargetName>", hook_project)
        self.assertIn("TJ.RemoteAccess.ScreenHooks32</TargetName>", hook_project)


if __name__ == "__main__":
    unittest.main()
