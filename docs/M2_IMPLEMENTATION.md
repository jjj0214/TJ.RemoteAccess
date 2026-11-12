# M2 engine integration foundation (2026-11-06)

> Development only: this does NOT ship a working TightVNC-derived Server or
> Viewer, Windows service, mTLS gateway, or internet-accessible RFB listener.
> Do not install on a production inspection workstation.

## Implemented

- **engine_session_gate.hpp/.cpp** bridges a *trusted authenticated identity*
  to the M1 session registry and the final native input callback.
- Normal mode admits 1 reservation/viewer; Maintenance admits at most 3.
  Reducing the limit refuses to disconnect existing viewers.
- Input checking uses session ID, principal, control epoch and monotonic input
  sequence while holding the same gate mutex through native injection.
  All actual callers MUST use this bridge to maintain the check/inject ordering.
- A trusted native agent MUST physically release keys and mouse buttons
  before issuing ConfirmInputReleased after revoke, transfer or disconnect.
  That physical input agent is not implemented in M2 yet.
- Administrator revoke cancels any pending control transfer.
- CMake native tests, VS2022 x64-only unit-test .vcxproj files, and separate
  Windows baseline/coexistence scripts are included.

## Windows build and tests

The following commands are examples for an approved developer workstation.
MSVC compilation does not imply successful runtime tests.

    MSBuild.exe tests\TJRemoteAccess.Core.Tests.vcxproj /p:Configuration=Release /p:Platform=x64
    MSBuild.exe tests\TJRemoteAccess.EngineGate.Tests.vcxproj /p:Configuration=Release /p:Platform=x64
    .\out\msbuild\bin\Release\TJRemoteAccess.Core.Tests.exe
    .\out\msbuild\bin\Release\TJRemoteAccess.EngineGate.Tests.exe

Canonical CMake build and CTest:

    cmake -S . -B out\build -A x64
    cmake --build out\build --config Release
    ctest --test-dir out\build -C Release --output-on-failure
    python -m unittest discover -s tests -p "test_*.py" -v

Baseline source import and unmodified original build:

    .\tools\Invoke-M2Baseline.ps1 -TightVncSourceZip "C:\path\to\tightvnc-2.8.90-src-gpl.zip"

SHA-256 is pinned to:
**F3F76393A5865E1F1919C78C3EB43B9C937CF738927C2BFC920ECBD3913B061C**.

An existing extracted tree must be reviewed before reuse. ReuseImportedSource
is NOT proof that the extracted files still equal the vendor archive.
Never replace original vendor copyright and third-party notices.

Read-only Windows coexistence snapshot:

    .\tools\Inspect-M2Coexistence.ps1

## Next fork integration gates

1. Extract and audit the authentic GPL source, then compile the **unmodified**
   original Server and Viewer Release x64. Keep v141_xp/SDK 8.1 differences
   separate from functional changes.
2. Preserve a pristine upstream tree and construct a traceable derived tree.
   Review service, processes, registry, named pipes, shared mappings, mutexes,
   window classes, messages, hook DLL, MSI product/upgrade identifiers,
   settings, log and temp paths. Blind global rename is forbidden.
3. Deny unauthenticated local IPC and every unintended public RFB listener.
   Exclude unneeded HTTP VNC, reverse and file transfer listeners.
4. Hook authenticated RFB attach/detach and all input events to the
   EngineSessionGate. The privileged injection agent checks permission at the
   **final** input site, not only inside Viewer UI.
5. Test independent server service lifecycle, two-viewer compatibility, 1/3
   connection caps, single control ownership and 4th-viewer rejection.
6. Test logon, lock, UAC, resolution changes and reinstall/uninstall on an
   approved isolated Windows 10/11 PC. Do not weaken UAC or Windows ACLs.
7. After stable M2 completion, implement M3 least-privilege parser/agent
   separation, mTLS Gateway and non-RFB authenticated management API.

## Verified status

| Check | Result |
| --- | --- |
| EngineSessionGate, NativeInputBridge and C++ test project compiled Release x64 | PASS (compile only) |
| Native C++ test executable run on JJJ Dev | NOT RUN (separate SHA-256 approval required) |
| Python unittest through JJJ Dev project_test | PASS (tests discovered; exact count not supplied) |
| CMake/CTest through JJJ Dev | NOT VERIFIED |
| GPL ZIP SHA-256 verified in earlier JJJ Dev baseline | PASS (prior evidence) |
| Original source import on the JJJ Dev worktree | EXTRACTED TREE PRESENT; SOURCE VERIFICATION NOT RERUN |
| Original Server/Viewer x64 build | NOT TESTED |
| Existing derived Server/Viewer Release x64 compilation via project_build | PASS (Server C4819 warning, Viewer no warning) |
| New fork Win32 namespace transformation | PYTHON TESTS PASS; EXISTING DERIVED TREE NOT REGENERATED |
| Static fork security/coexistence audit | PYTHON TESTS PASS; DEPLOYMENT BLOCKED |
| Viewer frames, mouse/keyboard and two-service coexistence | NOT TESTED |
| Windows logon, UAC and uninstall independence | NOT TESTED |
| mTLS, final injector and local IPC ACLs | NOT IMPLEMENTED |

The native test projects are compilation fixtures, **not** an upstream
TightVNC Server/Viewer build. No part of this report asserts that an RFB
connection or Windows remote-control session works.
