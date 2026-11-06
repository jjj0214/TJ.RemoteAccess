# SPDX-License-Identifier: GPL-2.0-or-later
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
import vendor_upstream
from vendor_upstream import (SourceImportError, examine_archive, import_source)
from audit_identifiers import audit


class TestVendorTools(unittest.TestCase):
    def build_zip(self, name: str):
        temporary = tempfile.TemporaryDirectory()
        path = Path(temporary.name) / "source.zip"
        with ZipFile(path, "w") as archive:
            archive.writestr(name, "test")
        return temporary, path

    def test_traversal_rejected(self):
        ctx, file = self.build_zip("tightvnc-2.8.90-gpl/../danger.cpp")
        with ctx, ZipFile(file) as archive:
            with self.assertRaises(SourceImportError): examine_archive(archive)

    def test_wrong_root_rejected(self):
        ctx, file = self.build_zip("wrong-root/a.cpp")
        with ctx, ZipFile(file) as archive:
            with self.assertRaises(SourceImportError): examine_archive(archive)

    def test_case_collision_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "src.zip"
            with ZipFile(path, "w") as archive:
                archive.writestr("tightvnc-2.8.90-gpl/Tvn/a.cpp", "a")
                archive.writestr("tightvnc-2.8.90-gpl/tvn/A.cpp", "b")
            with ZipFile(path) as archive:
                with self.assertRaises(SourceImportError): examine_archive(archive)

    def test_good_zip_layout(self):
        ctx, file = self.build_zip("tightvnc-2.8.90-gpl/tvnserver/main.cpp")
        with ctx, ZipFile(file) as archive:
            self.assertEqual(len(examine_archive(archive)), 1)

    def test_import_exact_mock_hash(self):
        ctx, file = self.build_zip("tightvnc-2.8.90-gpl/src/a.cpp")
        with ctx:
            target = file.parent / "verified"
            original = vendor_upstream.EXPECTED_SHA256
            vendor_upstream.EXPECTED_SHA256 = vendor_upstream.checksum(file)
            try:
                result = import_source(file, target)
                self.assertEqual(result["imported_files"], 1)
                self.assertEqual((target / "src" / "a.cpp").read_text(), "test")
                with self.assertRaises(SourceImportError): import_source(file, target)
            finally:
                vendor_upstream.EXPECTED_SHA256 = original

    def test_hash_mismatch_no_write(self):
        ctx, file = self.build_zip("tightvnc-2.8.90-gpl/a.cpp")
        with ctx:
            target = file.parent / "dest"
            with self.assertRaises(SourceImportError): import_source(file, target)
            self.assertFalse(target.exists())

    def test_audit_read_only(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            target = root / "NamingDefs.cpp"
            text = 'TightVNC_Service_Control\nUpgradeCode\n'
            target.write_text(text)
            found = audit(root)
            self.assertEqual(found["finding_count"], 2)
            self.assertEqual(target.read_text(), text)


if __name__ == "__main__":
    unittest.main()
