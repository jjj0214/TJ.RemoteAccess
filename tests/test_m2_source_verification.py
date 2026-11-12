# SPDX-License-Identifier: GPL-2.0-or-later
import hashlib
import sys
import tempfile
import unittest
from pathlib import Path
from zipfile import ZipFile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from vendor_upstream import SourceImportError  # noqa: E402
from verify_upstream_tree import verify_tree  # noqa: E402


class ExtractedTreeValidation(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.archive = self.root / "source.zip"
        self.extracted = self.root / "extracted"
        (self.extracted / "sub").mkdir(parents=True)
        (self.extracted / "sub" / "file.cpp").write_bytes(b"GPL text\n")
        with ZipFile(self.archive, "w") as zip_file:
            zip_file.writestr("tightvnc-2.8.90-gpl/", "")
            zip_file.writestr("tightvnc-2.8.90-gpl/sub/", "")
            zip_file.writestr("tightvnc-2.8.90-gpl/sub/file.cpp", b"GPL text\n")
        self.sha = hashlib.sha256(self.archive.read_bytes()).hexdigest()

    def verify(self):
        return verify_tree(self.archive, self.extracted, expected_sha256=self.sha)

    def test_verified_tree(self):
        result = self.verify()
        self.assertTrue(result["exact_match"])
        self.assertEqual(result["files_verified"], 1)

    def test_tampered_file_rejected(self):
        (self.extracted / "sub" / "file.cpp").write_bytes(b"Bad text\n")
        with self.assertRaises(SourceImportError):
            self.verify()

    def test_extra_file_rejected(self):
        (self.extracted / "sub" / "injected.txt").write_text("extra")
        with self.assertRaises(SourceImportError):
            self.verify()

    def test_missing_file_rejected(self):
        (self.extracted / "sub" / "file.cpp").unlink()
        with self.assertRaises(SourceImportError):
            self.verify()

    def test_wrong_archive_hash_rejected(self):
        with self.assertRaises(SourceImportError):
            verify_tree(self.archive, self.extracted)


if __name__ == "__main__":
    unittest.main()
