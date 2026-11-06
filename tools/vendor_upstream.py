#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Import ONLY the SHA-pinned TightVNC 2.8.90 GPL source archive.

No downloads, no secrets and no modification of another product checkout.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import stat
import tempfile
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

EXPECTED_SHA256 = "f3f76393a5865e1f1919c78c3eb43b9c937cf738927c2bfc920ecbd3913b061c"
EXPECTED_ROOT = "tightvnc-2.8.90-gpl"
MAX_TOTAL_UNCOMPRESSED = 300 * 1024 * 1024
MAX_FILES = 20000
CHUNK = 1024 * 1024


class SourceImportError(ValueError):
    pass


def checksum(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(CHUNK), b""):
            digest.update(block)
    return digest.hexdigest()


def examine_archive(archive: ZipFile) -> list[tuple]:
    entries = archive.infolist()
    if not entries or len(entries) > MAX_FILES:
        raise SourceImportError("Invalid archive entry count")
    total = 0
    seen: set[str] = set()
    checked = []
    for item in entries:
        raw = item.filename
        if "\\" in raw or "\x00" in raw or raw.startswith("/"):
            raise SourceImportError("Unsafe archive filename")
        path = PurePosixPath(raw)
        if any(part in ("", ".", "..") for part in path.parts):
            raise SourceImportError("Traversal or empty path component")
        if not path.parts or path.parts[0] != EXPECTED_ROOT:
            raise SourceImportError("Unexpected ZIP root directory")
        if any(":" in part for part in path.parts):
            raise SourceImportError("Potential drive path")
        # Reject case-insensitive path collisions on Windows.
        normalized = "/".join(path.parts).lower().rstrip("/")
        if normalized in seen:
            raise SourceImportError("Duplicate case-insensitive ZIP entry")
        seen.add(normalized)
        if (item.external_attr >> 16) & 0o170000 == stat.S_IFLNK:
            raise SourceImportError("Symbolic links are not allowed")
        if item.file_size < 0:
            raise SourceImportError("Invalid member length")
        total += item.file_size
        if total > MAX_TOTAL_UNCOMPRESSED:
            raise SourceImportError("Archive exceeds uncompressed-size limit")
        checked.append((item, path))
    return checked


def import_source(zip_path: Path, destination: Path) -> dict:
    if not zip_path.is_file():
        raise SourceImportError(f"ZIP does not exist: {zip_path}")
    observed_hash = checksum(zip_path)
    if observed_hash != EXPECTED_SHA256:
        raise SourceImportError(f"TightVNC source SHA256 mismatch: {observed_hash}")
    if destination.exists():
        raise SourceImportError(f"Refusing to replace existing target: {destination}")
    if not destination.parent.is_dir():
        raise SourceImportError(f"Parent directory does not exist: {destination.parent}")
    # Staging is guaranteed on the same volume as target for an atomic final rename.
    with tempfile.TemporaryDirectory(prefix=".tjra-verified-", dir=destination.parent) as temp:
        stage = Path(temp)
        file_count = 0
        with ZipFile(zip_path, "r") as archive:
            checked = examine_archive(archive)
            for info, path in checked:
                # Strip the top-level archive root; install into the target folder.
                relative = Path(*path.parts[1:]) if len(path.parts) > 1 else Path(".")
                target = stage / relative
                if info.is_dir():
                    target.mkdir(parents=True, exist_ok=True)
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.open(info, "r") as source, target.open("xb") as sink:
                    shutil.copyfileobj(source, sink, CHUNK)
                file_count += 1
        # Never follow symlinks in or overwrite the final destination.
        if destination.exists():
            raise SourceImportError("Target appeared during staging")
        os.rename(stage, destination)
    return {
        "version": "2.8.90", "upstream": "TightVNC",
        "archive_sha256": observed_hash, "imported_files": file_count,
        "target": str(destination), "upstream_source_url":
            "https://www.tightvnc.com/download/2.8.90/tightvnc-2.8.90-src-gpl.zip",
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip", type=Path, required=True, help="Official GPL ZIP on local disk")
    parser.add_argument("--dest", type=Path, default=Path(__file__).resolve().parents[1]
                        / "third_party" / EXPECTED_ROOT)
    args = parser.parse_args()
    print(json.dumps(import_source(args.zip.resolve(), args.dest.resolve()), indent=2))


if __name__ == "__main__":
    main()
