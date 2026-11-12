#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Verify every extracted upstream GPL source file against a pinned ZIP.

This never creates, modifies or removes source files. The CLI does not permit
overriding the official TightVNC 2.8.90 SHA-256.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile

from vendor_upstream import (
    EXPECTED_SHA256,
    SourceImportError,
    checksum,
    examine_archive,
)

CHUNK = 1024 * 1024


def digest_stream(stream) -> str:
    sha = hashlib.sha256()
    while True:
        block = stream.read(CHUNK)
        if not block:
            break
        sha.update(block)
    return sha.hexdigest()


def verify_tree(
    archive_path: Path,
    source_dir: Path,
    expected_sha256: str = EXPECTED_SHA256,
) -> dict:
    if not archive_path.is_file():
        raise SourceImportError("Upstream ZIP not found")
    observed = checksum(archive_path)
    if observed.lower() != expected_sha256.lower():
        raise SourceImportError("Upstream ZIP SHA-256 mismatch")
    if not source_dir.is_dir() or source_dir.is_symlink():
        raise SourceImportError("Extracted source root missing or is a symlink")

    expected_files: set[str] = set()
    expected_dirs: set[str] = set()
    with ZipFile(archive_path) as archive:
        members = examine_archive(archive)
        for info, path in members:
            if len(path.parts) < 2:
                continue  # Strip ZIP root, never put it under extracted root.
            relative = Path(*path.parts[1:])
            key = relative.as_posix()
            for parent in relative.parents:
                if parent == Path("."):
                    break
                expected_dirs.add(parent.as_posix())
            dest = source_dir / relative
            if info.is_dir():
                expected_dirs.add(key)
                if not dest.is_dir() or dest.is_symlink():
                    raise SourceImportError(f"Missing/unsafe directory: {key}")
                continue
            if key in expected_files:
                raise SourceImportError(f"Duplicate source file: {key}")
            expected_files.add(key)
            if not dest.is_file() or dest.is_symlink():
                raise SourceImportError(f"Missing/unsafe source file: {key}")
            if dest.stat().st_size != info.file_size:
                raise SourceImportError(f"Size mismatch: {key}")
            with archive.open(info) as original, dest.open("rb") as extracted:
                if digest_stream(original) != digest_stream(extracted):
                    raise SourceImportError(f"Content mismatch: {key}")

    for actual in source_dir.rglob("*"):
        key = actual.relative_to(source_dir).as_posix()
        if actual.is_symlink():
            raise SourceImportError(f"Unexpected symlink: {key}")
        if actual.is_file():
            if key not in expected_files:
                raise SourceImportError(f"Unexpected extra file: {key}")
        elif actual.is_dir():
            if key not in expected_dirs:
                raise SourceImportError(f"Unexpected extra directory: {key}")
        else:
            raise SourceImportError(f"Unexpected filesystem object: {key}")

    return {
        "archive_sha256": observed,
        "files_verified": len(expected_files),
        "source_dir": str(source_dir.resolve()),
        "exact_match": True,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(verify_tree(args.zip, args.source), indent=2))


if __name__ == "__main__":
    main()
