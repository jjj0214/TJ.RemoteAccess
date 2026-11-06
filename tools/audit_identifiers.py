#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Read-only evidence inventory for TightVNC identity/IPC collisions.

No automated global search-and-replace: renaming symbols blindly can break
service, helper, window message, version or installer contracts.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

PATTERNS = {
    "service_and_exe": ["tvnserver", "tvnviewer", "TvnServer"],
    "registry": ["Software\\TightVNC", "TightVNC\\Server", "TightVNC\\Viewer"],
    "control_pipes": ["TightVNC_Service_Control", "TightVNC_Application_Control"],
    "global_ipc": ["Global\\", "CreateFileMapping", "CreateNamedPipe", "CreateMutex"],
    "security_sensitive_acl": ["setAllAccess", "SetSecurityDescriptorDacl", "NULL DACL"],
    "installer_identity": ["UpgradeCode", "ProductCode", "ComponentGuid"],
    "viewer_window_class": ["TvnWindowClass", "RegisterClass", "RegisterWindowMessage"],
}
SUFFIXES = {".cpp", ".h", ".hpp", ".vcxproj", ".vcproj", ".wxs", ".iss", ".rc", ".c"}


def audit(root: Path) -> dict:
    if not root.is_dir():
        raise ValueError(f"Source directory missing: {root}")
    findings = []
    for path in sorted(root.rglob("*")):
        if not path.is_file() or path.suffix.lower() not in SUFFIXES:
            continue
        # Don't interpret binary blobs, compressed resource, or large generated files.
        if path.stat().st_size > 3_000_000:
            continue
        try:
            lines = path.read_text(encoding="utf-8-sig", errors="replace").splitlines()
        except OSError:
            continue
        for number, line in enumerate(lines, 1):
            hits = [category for category, terms in PATTERNS.items()
                    if any(term in line for term in terms)]
            if hits:
                findings.append({"path": path.relative_to(root).as_posix(),
                                 "line": number, "categories": hits})
    return {"source_root": str(root.resolve()), "finding_count": len(findings),
            "findings": findings}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--out", type=Path, help="Optional output JSON path")
    args = parser.parse_args()
    report = audit(args.source)
    payload = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    if args.out:
        if not args.out.parent.is_dir():
            raise ValueError("Output folder does not exist")
        args.out.write_text(payload, encoding="utf-8")
        print(f"Audit findings: {report['finding_count']} -> {args.out}")
    else:
        print(payload)


if __name__ == "__main__":
    main()
