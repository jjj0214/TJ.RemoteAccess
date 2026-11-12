#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Read-only audit of a generated M2 fork's known coexistence/security gaps.

This is deliberately a blocker report, never a production-security approval.
It examines fixed source locations only; it does not start processes, install a
service, modify a registry, contact a network or execute native binaries.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_TREE = ROOT / "out" / "m2-fork" / "tightvnc-2.8.90-tjra-guarded-v4" / "upstream"

# Exact implementation signs that have not yet passed independent RFB/IPC
# hardening. Fixing a string alone never makes a product deployable.
BLOCKER_PATTERNS = (
    ("RAW_RFB_TCP_LISTENER", "tvnserver-app/RfbServer.cpp",
     "TcpServer::start()", "Raw TCP listener must be gated behind trusted transport"),
    ("LEGACY_RFB_ADMISSION", "tvnserver-app/RfbClientManager.cpp",
     "onClientAuth(RfbClient *client)",
     "VNC password authentication is not a trusted TJRA principal"),
    ("DIRECT_KEYBOARD_INJECTION", "rfb-sconn/RfbClient.cpp",
     "m_desktop->setKeyboardEvent(",
     "The final input sink must call EngineSessionGate under the same lock"),
    ("DIRECT_MOUSE_INJECTION", "rfb-sconn/RfbClient.cpp",
     "m_desktop->setMouseEvent(",
     "The final input sink must call EngineSessionGate under the same lock"),
    ("LEGACY_FILE_TRANSFER", "rfb-sconn/RfbClient.cpp",
     "new FileTransferRequestHandler(",
     "Disable the unreviewed upstream file-transfer extension"),
    ("SHARED_SCREEN_HOOK_DLL", "desktop/HookInstaller.cpp",
     '"screenhooks64.dll"',
     "The legacy hook DLL requires independent binary naming and packaging"),
    ("SHARED_HOOK_LOADER_EXE", "tvnserver-app/NamingDefs.cpp",
     '"hookldr.exe"',
     "The legacy hook loader requires independent binary naming and packaging"),
    ("SHARED_MIRROR_DRIVER_WINDOW", "tvnserver-app/NamingDefs.cpp",
     '"MIRROR_DRIVER_MESSAGE_WINDOW_CLASS_NAME"',
     "Driver/window compatibility and access control need review"),
)

# Exact Win32 object literals that should be distinct in regenerated source.
LEGACY_WIN32_NAMES = (
    "TvnWindowsApplicationClass", "TvnApplicationClass",
    "TvnWindowClass", "TvnHookLoaderWindowClass", "HookTargetWinClassName",
    "Tvnserver.Clipboard.MessageWindow", "TVN_log_pipe_public_name",
    "TVN.HOOK.LOADER.CLOSE.CODE", "TVN.HOOK.MESSAGE.CODE",
)
NAME_FILES = (
    "tvnserver-app/NamingDefs.cpp",
    "tvnviewer/NamingDefs.cpp",
)


INTERLOCK_MARKERS = {
    "tvnserver-app/TvnServer.cpp": (
        "TJRA M2: legacy RFB listener disabled",
        "TJRA M2: legacy HTTP listener disabled",
        "TJRA M2: legacy untrusted control pipe disabled",
    ),
    "tvnserver-app/ExtraRfbServers.cpp": ("TJRA M2: extra RFB listeners disabled",),
    "tvnserver-app/RfbClientManager.cpp": (
        "TJRA M2: session rejected (trusted gateway missing)",
    ),
    "rfb-sconn/RfbClient.cpp": (
        "TJRA M2: keyboard injection denied until trusted final injector exists",
        "TJRA M2: pointer injection denied until trusted final injector exists",
        "TJRA M2: RFB file transfer disabled",
    ),
}

def audit_tree(tree: Path) -> dict:
    tree = Path(tree)
    findings: list[dict] = []
    if tree.is_symlink() or not tree.is_dir():
        return {
            "status": "BLOCKED_FOR_DEPLOYMENT",
            "deployAllowed": False,
            "findings": [{"code": "SOURCE_TREE_MISSING_OR_UNSAFE", "path": ".",
                          "line": 0, "reason": "Missing directory or symlink"}],
            "auditedFiles": 0,
        }
    names = {path for _, path, _, _ in BLOCKER_PATTERNS} | set(NAME_FILES) | set(INTERLOCK_MARKERS)
    texts: dict[str, str] = {}
    for rel in sorted(names):
        path = tree / rel
        if path.is_symlink() or not path.is_file() or path.stat().st_size > 2 * 1024 * 1024:
            findings.append({"code": "SOURCE_FILE_MISSING_OR_UNSAFE", "path": rel,
                             "line": 0, "reason": "Missing, oversized, or symlink"})
            continue
        texts[rel] = path.read_bytes().decode("utf-8", errors="replace")

    for code, rel, marker, reason in BLOCKER_PATTERNS:
        content = texts.get(rel)
        if content is None:
            continue
        index = content.find(marker)
        if index >= 0:
            findings.append({"code": code, "path": rel,
                             "line": content.count("\n", 0, index) + 1,
                             "reason": reason})
    for rel in NAME_FILES:
        content = texts.get(rel)
        if content is None:
            continue
        for value in LEGACY_WIN32_NAMES:
            marker = '"' + value + '"'
            index = content.find(marker)
            if index >= 0:
                findings.append({"code": "SHARED_WIN32_NAME", "path": rel,
                                 "line": content.count("\n", 0, index) + 1,
                                 "reason": "Upstream Win32 namespace: " + value})

    missing_interlocks = [
        f"{rel}: {marker}"
        for rel, markers in INTERLOCK_MARKERS.items()
        for marker in markers if marker not in texts.get(rel, "")
    ]
    # Even with zero static findings, dynamic authentication, input authorization,
    # Windows service coexistence, security tests, and GPL review are mandatory.
    return {
        "status": "BLOCKED_FOR_DEPLOYMENT",
        "deployAllowed": False,
        "findings": findings,
        "auditedFiles": len(texts),
        "generationInterlocks": ("SOURCE_ONLY_PRESENT" if not missing_interlocks
                                 else "NOT_APPLIED_TO_THIS_FORK"),
        "missingInterlocks": missing_interlocks,
        "limitations": (
            "Static source signature audit only. It cannot prove that all "
            "injection, IPC or authentication paths are safe."
        ),
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tree", type=Path, default=DEFAULT_TREE)
    args = parser.parse_args()
    print(json.dumps(audit_tree(args.tree), indent=2))


if __name__ == "__main__":
    main()
