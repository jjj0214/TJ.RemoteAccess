# SPDX-License-Identifier: GPL-2.0-or-later
"""Offline LAB package contract checks. These DO NOT execute PowerShell."""
from pathlib import Path
import unittest

TOOLS = Path(__file__).resolve().parents[1] / "tools"


class OfflinePackageContracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.pack = (TOOLS / "Build-TJRemoteAccess-Lab-OfflinePackage.ps1").read_text(
            encoding="utf-8-sig")
        cls.install = (TOOLS / "Install-TJRemoteAccess-Lab-Offline.ps1").read_text(
            encoding="utf-8-sig")

    def test_build_contains_self_contained_published_binaries_and_exact_source(self):
        for fragment in ("-Mode Both -BuildOnly", "Get-FileHash", "SHA256",
                         "SourceRevision", "manifest.json", "git -C $root archive",
                         "source.zip", "Compression.ZipFile",
                         "git -C $root status --porcelain"):
            self.assertIn(fragment, self.pack)

    def test_build_refuses_overwriting_existing_release_artifacts(self):
        self.assertIn("An artifact with this version already exists", self.pack)
        self.assertIn("Source tree is dirty", self.pack)
        self.assertIn("Unsigned = $true", self.pack)
        self.assertIn(".sha256", self.pack)
        self.assertIn("if ($LASTEXITCODE -ne 0)", self.pack)

    def test_install_requires_publisher_hash_before_extract_or_update(self):
        self.assertIn("[Parameter(Mandatory = $true)]", self.install)
        self.assertIn("ExpectedPackageSha256", self.install)
        self.assertIn("actualPackageHash", self.install)
        self.assertIn("Get-FileHash", self.install)
        self.assertLess(self.install.index("actualPackageHash"),
                        self.install.index("ExtractToDirectory"))
        self.assertLess(self.install.index("ExtractToDirectory"),
                        self.install.index("Move-Item -LiteralPath $target"))

    def test_install_rejects_unsafe_files_and_manifest_mismatches(self):
        for fragment in ("Duplicate or case-colliding ZIP entry",
                         "Unsafe ZIP entry name",
                         "Symlink entry rejected",
                         "Unmanifested package entry",
                         "Manifest hash verification failed",
                         "Required executable missing from package",
                         "Uncompressed Lab package is too large"):
            self.assertIn(fragment, self.install)
        self.assertIn("$zipEntries.ContainsKey", self.install)
        self.assertIn("ZipFile]::ExtractToDirectory", self.install)
        self.assertIn("source.zip", self.install)

    def test_installer_preserves_secrets_rolls_back_and_never_installs_service(self):
        for fragment in ("$installed", ".backup-", "HadPrevious",
                         "Move-Item -LiteralPath $entry.Backup",
                         "Get-Process", "LAB INSTALL PASS"):
            self.assertIn(fragment, self.install)
        for dangerous in ("New-NetFirewallRule", "New-Service", "sc.exe create",
                          "Import-PfxCertificate", "Start-Process -Verb RunAs",
                          "Set-ItemProperty"):
            self.assertNotIn(dangerous, self.install)
        self.assertIn("TLS profiles, certificates and passwords were NOT replaced", self.install)


if __name__ == "__main__":
    unittest.main()
