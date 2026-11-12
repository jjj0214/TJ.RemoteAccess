#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-2.0-or-later
"""Pin and isolate dependent hook-loader/DLL names in the derived GPL fork.

String renames are not an ACL or security boundary. These changes only make
the fork's hook executable, x86/x64 DLL and their runtime references match.
No installation, DLL injection, or hook execution is performed here.
"""
from __future__ import annotations

import re

from vendor_upstream import SourceImportError

REPLACEMENTS = {
    "tvnserver-app/NamingDefs.cpp": (
        ('HookDefinitions::HOOK_LOADER_NAME[] = _T("hookldr.exe")',
         'HookDefinitions::HOOK_LOADER_NAME[] = _T("TJ.RemoteAccess.HookLoader.exe")',
         "hook-loader-runtime-reference"),
    ),
    "desktop/HookInstaller.cpp": (
        ('HookInstaller::LIBRARY_NAME[] = _T("screenhooks32.dll")',
         'HookInstaller::LIBRARY_NAME[] = _T("TJ.RemoteAccess.ScreenHooks32.dll")',
         "hook-dll32-runtime-reference"),
        ('HookInstaller::LIBRARY_NAME[] = _T("screenhooks64.dll")',
         'HookInstaller::LIBRARY_NAME[] = _T("TJ.RemoteAccess.ScreenHooks64.dll")',
         "hook-dll64-runtime-reference"),
    ),
}
PROJECT_TARGET = re.compile(
    r"(<TargetName\b[^>]*>)(\$\((?:ProjectName)\))([36][24])(</TargetName>)"
)
EXPECTED_HOOK_IDS = {
    "hook-loader-runtime-reference",
    "hook-dll32-runtime-reference",
    "hook-dll64-runtime-reference",
    "hook-dll-project-targets",
}


def isolate_hook_names(relative: str, source: str) -> tuple[str, list[str]]:
    edited = source
    applied = []
    for old, new, marker in REPLACEMENTS.get(relative, ()):
        if edited.count(old) != 1:
            raise SourceImportError(f"Unexpected hook runtime reference in {relative}: {old}")
        edited = edited.replace(old, new, 1)
        applied.append(marker)

    if relative == "screen-hooks/screenhooks.vcxproj":
        # Only change <TargetName>, never the original project/file paths.
        matches = PROJECT_TARGET.findall(edited)
        if len(matches) != 8 or sorted(int(m[2]) for m in matches) != [32] * 4 + [64] * 4:
            raise SourceImportError("Unexpected upstream screenhooks platform target structure")
        edited, count = PROJECT_TARGET.subn(
            lambda m: m.group(1) + "TJ.RemoteAccess.ScreenHooks" +
                      m.group(3) + m.group(4), edited
        )
        if count != 8:
            raise SourceImportError("Incomplete platform-specific hook DLL rename")
        applied.append("hook-dll-project-targets")
    return edited, applied
