#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Prepare a *test-only* TightVNC 2.8.90 source fork from verified GPL bytes.

No MSI generation, service installation, process launch, network access or
modification of the original upstream tree. The fork is NOT a deployable
remote-access product; namespace edits require native Windows coexistence QA.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import tempfile
from pathlib import Path

from m2_fail_closed import EXPECTED_IDS, apply_interlocks
from m2_hook_namespace import EXPECTED_HOOK_IDS, isolate_hook_names
from vendor_upstream import EXPECTED_SHA256, SourceImportError, import_source
from verify_upstream_tree import verify_tree

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = ROOT / "third_party" / "tightvnc-2.8.90-gpl"
DEFAULT_FORK = ROOT / "out" / "m2-fork" / "tightvnc-2.8.90-tjra"
LITERAL = re.compile(r'(?P<prefix>u8|[LuU])?"(?P<body>(?:\\.|[^"\\])*)"')
RUNTIME_ROOTS = {
    "tvnserver", "tvnserver-app", "tvnviewer", "tvncontrol-app",
    "config-lib", "server-config-lib", "client-config-lib",
    "desktop", "desktop-ipc", "viewer-core", "wsconfig-lib",
    "rfb-sconn", "win-system", "win-event-log", "log-server",
    "log-writer", "gui", "thread", "http-server-lib", "hookldr", "screen-hooks",
}
NAME_MAP = (
    ("tvnserver", "TJ.RemoteAccess.Server"),
    ("tvnviewer", "TJ.RemoteAccess.Viewer"),
    ("tvncontrol", "TJ.RemoteAccess.Control"),
    ("TightVNC", "TJ.RemoteAccess"),
)
# These are Win32 object names, not cosmetic product branding. Keep all
# client/server/hook users consistent in the derived tree; never rename
# original GPL source files or replace upstream copyright notices.
ISOLATION_NAME_MAP = (
    ("TvnWindowsApplicationClass", "TJRA.WindowsApplicationClass"),
    ("TvnApplicationClass", "TJRA.ViewerApplicationClass"),
    ("TvnWindowClass", "TJRA.ViewerWindowClass"),
    ("TvnHookLoaderWindowClass", "TJRA.HookLoaderWindowClass"),
    ("HookTargetWinClassName", "TJRA.HookTargetWindowClass"),
    ("Tvnserver.Clipboard.MessageWindow", "TJRA.ClipboardMessageWindow"),
    ("TVN_log_pipe_public_name", "TJRA_log_pipe_public_name"),
    ("TVN.HOOK.LOADER.CLOSE.CODE", "TJRA.HOOK.LOADER.CLOSE.CODE"),
    ("TVN.HOOK.MESSAGE.CODE", "TJRA.HOOK.MESSAGE.CODE"),
)
ALL_NAME_MAP = NAME_MAP + ISOLATION_NAME_MAP

TARGET_NAMES = {
    "tvnserver": "TJ.RemoteAccess.Server",
    "tvnviewer": "TJ.RemoteAccess.Viewer",
    "tvncontrol-app": "TJ.RemoteAccess.Control",
    "hookldr": "TJ.RemoteAccess.HookLoader",
}
TARGET_IMPORT = re.compile(
    r'(<Import\b[^>]*Microsoft\.Cpp\.targets[^>]*/>)', re.IGNORECASE
)
PATHLIKE_EXTENSIONS = (
    ".h", ".hpp", ".cpp", ".c", ".rc", ".ico", ".bmp", ".png",
    ".lib", ".dll", ".obj", ".manifest", ".vcxproj", ".sln",
)


def binary_sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def decode_plain(path: Path) -> tuple[str, str, bytes] | None:
    blob = path.read_bytes()
    if b"\x00" in blob:
        return None
    if blob.startswith(b"\xef\xbb\xbf"):
        return blob[3:].decode("utf-8"), "utf-8", b"\xef\xbb\xbf"
    try:
        return blob.decode("utf-8"), "utf-8", b""
    except UnicodeDecodeError:
        return blob.decode("cp1252"), "cp1252", b""


def edit_runtime_literals(source: str) -> tuple[str, dict[str, int]]:
    counts = {old: 0 for old, _ in ALL_NAME_MAP}
    out_lines = []
    for line in source.splitlines(keepends=True):
        # Preserve source includes, preprocessor link names and copyright
        # statements verbatim; filenames are not renamed by this experiment.
        if line.lstrip().startswith(("#include ", "#import ", "#pragma ")):
            out_lines.append(line)
            continue

        def replace_literal(match: re.Match) -> str:
            value = match.group("body")
            # Resource file names and link/include strings must still resolve
            # to the original GPL source tree, whose files are NOT renamed.
            if value.lower().endswith(PATHLIKE_EXTENSIONS):
                return match.group(0)
            # Keep upstream web links and trademark attribution intact.
            # A vendor domain must never be rewritten to a fabricated TJ URL.
            if "http://" in value or "https://" in value or "www.TightVNC.com" in value:
                return match.group(0)
            updated = value
            for old, new in ALL_NAME_MAP:
                number = updated.count(old)
                if number:
                    updated = updated.replace(old, new)
                    counts[old] += number
            return (match.group("prefix") or "") + '"' + updated + '"'

        out_lines.append(LITERAL.sub(replace_literal, line))
    return "".join(out_lines), counts


def insert_target_name(source: str, executable: str) -> str:
    # Rewriting XML trees would reformat upstream MSBuild projects.
    # The override is inserted immediately before the C++ targets import.
    # This is a TEST BUILD output override, not a service-install patch.
    if "<Project" not in source or "Microsoft.Cpp.targets" not in source:
        raise SourceImportError("Unexpected Visual Studio project structure")
    block = (
        '<PropertyGroup Label="TJ.RemoteAccess fork test output">\n'
        f'    <TargetName>{executable}</TargetName>\n'
        '  </PropertyGroup>\n  '
    )
    modified, count = TARGET_IMPORT.subn(lambda m: block + m.group(0), source)
    if count != 1:
        raise SourceImportError(f"Expected one VC++ targets import, found {count}")
    # The output name may still be overridden by an explicit Link.OutputFile.
    # Update only the Link.OutputFile executable name in the derived copy.
    old_names = {
        "TJ.RemoteAccess.Server": "tvnserver",
        "TJ.RemoteAccess.Viewer": "tvnviewer",
        "TJ.RemoteAccess.Control": "tvncontrol",
        "TJ.RemoteAccess.HookLoader": "hookldr",
    }
    old = old_names.get(executable)
    if old is None:
        raise SourceImportError("Unexpected derived executable target")
    output = re.compile(
        r'(<OutputFile>[^<]*?)' + re.escape(old) + r'(\.exe</OutputFile>)',
        re.IGNORECASE)
    modified = output.sub(lambda match: match.group(1) + executable + match.group(2), modified)
    return modified


def retarget_vs2022(source: str) -> tuple[str, int]:
    # Retarget only a derived COPY, never pristine TightVNC source. VS2022's
    # v143 toolset and the installed Windows 10/11 SDK are a separate gate.
    toolset = re.compile(r'(<PlatformToolset>)(v\d+(?:_xp)?)(</PlatformToolset>)')
    sdk = re.compile(r'(<WindowsTargetPlatformVersion>)(8\.1|10\.0\.[\d.]+)(</WindowsTargetPlatformVersion>)')
    output, tool_count = toolset.subn(r'\g<1>v143\g<3>', source)
    output, sdk_count = sdk.subn(r'\g<1>10.0\g<3>', output)
    return output, tool_count + sdk_count


def prepare(archive: Path, source: Path, fork: Path, retarget: bool) -> dict:
    archive = archive.resolve()
    source = source.resolve()
    fork = fork.resolve()
    if not archive.is_file():
        raise SourceImportError(f"Pinned upstream ZIP missing: {archive}")
    if not source.exists():
        if source.parent != ROOT / "third_party":
            raise SourceImportError("Auto-import allowed only at default third_party source")
        import_source(archive, source)
    identity = verify_tree(archive, source)
    if fork.exists():
        raise SourceImportError(f"Refusing overwrite of existing fork: {fork}")
    if source == fork or source in fork.parents or fork in source.parents:
        raise SourceImportError("Fork and pristine source cannot contain each other")
    fork.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix=".tjra-m2-fork-", dir=fork.parent))
    modified_files = []
    totals = {old: 0 for old, _ in ALL_NAME_MAP}
    interlocks_applied = set()
    hook_names_applied = set()
    toolchain_edits = 0
    targets_written = {}
    try:
        # Copy only known verified original GPL source. No private project code.
        shutil.copytree(source, stage / "upstream", symlinks=False)
        build_source = stage / "upstream"

        for path in sorted(build_source.rglob("*")):
            if not path.is_file():
                continue
            rel = path.relative_to(build_source).as_posix()
            original_hash = binary_sha256(path)
            decoded = decode_plain(path) if path.suffix.lower() in {
                ".cpp", ".h", ".hpp", ".rc", ".mc", ".vcxproj"
            } else None
            if decoded is None:
                continue
            content, encoding, bom = decoded
            updated = content
            counts = {}
            # VS2022 no longer gets std::wstring from an unrelated STL header.
            # Apply this compatibility fix only to the verified derived tree.
            if rel == "rfb/RfbKeySym.h" and "#include <string>" not in updated:
                marker = '#include "RfbKeySymListener.h"'
                if updated.count(marker) != 1:
                    raise SourceImportError("Unexpected RfbKeySym.h structure")
                newline = "\r\n" if "\r\n" in updated else "\n"
                updated = updated.replace(marker, "#include <string>" + newline + marker)

            if path.suffix.lower() in {".cpp", ".h", ".hpp", ".rc", ".mc"}:
                if path.relative_to(build_source).parts[0] in RUNTIME_ROOTS:
                    updated, counts = edit_runtime_literals(updated)
                    for name, number in counts.items():
                        totals[name] += number

            # Fail closed in the derived copy until TJRA trusted gateway,
            # authenticated session binding, and input agent are available.
            updated, applied = apply_interlocks(rel, updated)
            interlocks_applied.update(applied)
            updated, isolated = isolate_hook_names(rel, updated)
            hook_names_applied.update(isolated)
            if path.suffix.lower() == ".vcxproj":
                name = path.stem
                if name in TARGET_NAMES:
                    updated = insert_target_name(updated, TARGET_NAMES[name])
                    targets_written[name] = rel
                if retarget:
                    updated, n = retarget_vs2022(updated)
                    toolchain_edits += n

            if updated == content:
                continue
            # Source characters and original newlines are preserved.
            path.write_bytes(bom + updated.encode(encoding))
            modified_files.append({
                "path": rel, "before": original_hash,
                "after": binary_sha256(path), "replacements": counts
            })

        if set(targets_written) != set(TARGET_NAMES):
            raise SourceImportError(
                f"Missing Server/Viewer/Control/HookLoader .vcxproj files: {targets_written}"
            )
        if totals["tvnserver"] == 0 or totals["TightVNC"] == 0:
            raise SourceImportError(
                "No service/registry identity string modifications detected; refuse inert fork"
            )
        if interlocks_applied != EXPECTED_IDS:
            raise SourceImportError(
                "Missing mandatory M2 deny-by-default interlocks: "
                + repr(sorted(EXPECTED_IDS - interlocks_applied)))
        if hook_names_applied != EXPECTED_HOOK_IDS:
            raise SourceImportError(
                "Missing mandatory hook artifact isolation: "
                + repr(sorted(EXPECTED_HOOK_IDS - hook_names_applied)))
        if not (build_source / "tightvnc2017.sln").is_file():
            raise SourceImportError("Original VS2017 solution absent")

        # Do not trust these substitutions to cover all Win32 object IDs.
        # Preserve explicit residual inspection list for human review.
        indicators = ("tvnserver", "tvnviewer", "tvncontrol", "TightVNC",
                      "Global\\", "CreateNamedPipe", "CreateFileMapping",
                      "setAllAccess", "TvnWindowClass", "TvnApplicationClass",
                      "TvnWindowsApplicationClass", "TvnHookLoaderWindowClass",
                      "HookTargetWinClassName", "TVN.HOOK", "TVN_log_pipe",
                      "Tvnserver.Clipboard", "MIRROR_DRIVER_MESSAGE_WINDOW",
                      "hookldr.exe", "screenhooks", "CreateMutex",
                      "SetSecurityDescriptorDacl")
        residual = []
        for path in sorted(build_source.rglob("*")):
            if not path.is_file() or path.suffix.lower() not in {
                ".cpp", ".h", ".hpp", ".rc", ".mc"
            }:
                continue
            if path.relative_to(build_source).parts[0] not in RUNTIME_ROOTS:
                continue
            decoded = decode_plain(path)
            if decoded is None:
                continue
            for line_number, line in enumerate(decoded[0].splitlines(), 1):
                found = [token for token in indicators if token in line]
                if found:
                    residual.append({
                        "path": path.relative_to(build_source).as_posix(),
                        "line": line_number, "indicators": found
                    })

        manifest = {
            "status": "TEST_FORK_NOT_SECURITY_APPROVED",
            "source_archive_sha256": identity["archive_sha256"],
            "source_files_verified": identity["files_verified"],
            "retargeted_v143": retarget,
            "project_targets": targets_written,
            "replacement_totals": totals,
            "toolchain_edits": toolchain_edits,
            "mandatory_interlocks": sorted(interlocks_applied),
            "legacy_listeners_enabled": False,
            "production_deployment_allowed": False,
            "interlock_revision": "M2-DENY-DEFAULT-2026-11-11",
            "hook_artifact_isolation": sorted(hook_names_applied),
            "modified_files": modified_files,
            "potential_residual_collisions": residual,
            "warning": (
                "The M2 test fork disables legacy RFB, HTTP and control listeners. "
                "A trusted gateway, privileged injector, installer and hardened IPC "
                "are NOT implemented. DO NOT deploy or execute as a live service."
            ),
        }
        # Manifest lives alongside build tree. Original tree remains immutable.
        (stage / "TJRA_FORK_MANIFEST.json").write_text(
            json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
        )
        os.rename(stage, fork)
        return {
            "fork": str(fork), "solution": str(fork / "upstream" / "tightvnc2017.sln"),
            "files_modified": len(modified_files), "toolchain_edits": toolchain_edits,
            "residual_audit_count": len(residual),
            "manifest": str(fork / "TJRA_FORK_MANIFEST.json"),
            "status": manifest["status"],
        }
    except Exception:
        if stage.exists():
            shutil.rmtree(stage)
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip", type=Path, required=True)
    parser.add_argument("--source", type=Path, default=DEFAULT_SOURCE)
    parser.add_argument("--out", type=Path, default=DEFAULT_FORK)
    parser.add_argument("--retarget-v143", action="store_true")
    args = parser.parse_args()
    print(json.dumps(prepare(args.zip, args.source, args.out,
                             args.retarget_v143), indent=2))


if __name__ == "__main__":
    main()
