# Test evidence — 2026-11-06

Environment: container Linux, g++ 14.2.0, CMake, Python 3.x.

M2 Windows evidence (JJJ Dev, 2026-11-06): Visual Studio 2022 MSBuild Release x64 compiled both tests/TJRemoteAccess.Core.Tests.vcxproj and tests/TJRemoteAccess.EngineGate.Tests.vcxproj (exit code 0). Executing the built test binaries and running CMake/CTest on JJJ Dev remain NOT TESTED; this compile check is not a TightVNC fork build.

| Check | Result |
|---|---|
| Fresh CMake configure (Debug) | PASS |
| Build C++17 portable session core | PASS |
| CTest session registry | PASS (including 64 concurrent reservations) |
| Python offline importer/audit unit tests | PASS (7 tests) |
| Archive SHA-256 on developer PC | PASS: `F3F76393A5865E1F1919C78C3EB43B9C937CF738927C2BFC920ECBD3913B061C` |
| Real 2.8.90 source import | NOT TESTED in this environment (ZIP is on a different PC) |
| Original Windows TightVNC build | NOT TESTED |
| Windows x64 build and runtime | NOT TESTED |
| Parallel service install, UAC and logon | NOT TESTED |
| mTLS, privileged input, RFB hardening | NOT IMPLEMENTED |

Passing the portable core tests **does not** establish remote-control functionality or network security. CI workflows are provided but have not been run on GitHub.

## Windows revalidation — 2026-11-11 (JJJ Dev MCP v1.8.0)

| Check | Result |
|---|---|
| project_discover repository targets | PASS |
| Derived Server (existing generated tree), MSBuild Release x64 | PASS; 0 errors, 1 C4819 codepage warning |
| Derived Viewer (existing generated tree), MSBuild Release x64 | PASS; 0 errors, 0 warnings |
| M2 EngineGate native test project + typed NativeInputBridge | PASS **compile only** |
| Full M2 test solution, MSBuild Release x64 | PASS **compile only** |
| Python unittest after namespace/audit tests added | PASS; test count not returned by MCP |
| project_analyze_log diagnostics | PASS |
| git diff --check | PASS |
| Native test executables | NOT RUN; explicit binary SHA-256 approval required |
| CMake/CTest and actual Server/Viewer runtime | NOT VERIFIED |

The Win32 identity rewrites added to `prepare_m2_fork.py` are verified through
synthetic source-generation tests, not a newly regenerated real fork. Static
`audit_m2_fork_security.py` reports deployment blockers; it does not certify
protocol security or authorize service installation.

## Authenticated transport policy extension — 2026-11-11

- `authenticated_session_coordinator.cpp` enforces connection binding,
  reservation expiry, one-controller policy and fail-closed input delivery.
- `secure_session_id.cpp` uses the Windows OS CSPRNG, with no weak fallback.
- C++ M2 test solution Release x64: **compile PASS**, zero errors/warnings.
- Python fork-generation regression suite: **41/41 PASS**.
- New C++ test scenarios: **compiled but not executed in this MCP session**.
- guarded-v4 derived Server/Viewer/Control/HookLoader/ScreenHooks:
  **Release x64 compile PASS** (existing vendor C4819 warning remains).
- PowerShell execution, mTLS, privileged IPC, real screen/input and service
  coexistence: **NOT VERIFIED**. Deployment remains blocked.
- See `docs/M2_AUTHENTICATED_SESSION_PROGRESS_20261111.md`.

## Interactive M2 Lab .NET 9 (separate from the guarded native fork)

- TJ.RemoteAccess.Lab.sln Release build: **PASS**, 0 compiler errors/warnings.
- Lab.Server, Lab.Viewer, Lab.Setup build: **PASS**.
- Lab xUnit Windows test: **11/11 PASS**, including authenticated TLS
  loopback, SHA-256 certificate pinning, unapproved-client handshake denial,
  versioned framed protocol, malformed packet and keyboard/mouse validation.
- Python source and delivery tests: **46/46 PASS** (41 previous + 5 lab checks).
- PowerShell installer/run and interactive desktop capture/mouse/keyboard:
  **NOT RUN** on JJJ Dev through MCP; user isolated-PC acceptance required.
- The Lab is **not** the production fork, and does not implement unattended
  Windows Service, secure desktop/UAC, enterprise PKI or PIE.FileTransfer IPC.
- See `docs/M2_LAB_INSTALL_TEST_KO.md` and `docs/M2_LAB_DEV_CHECKPOINT_20261111.md`.

## Multi-server Viewer update — 2026-11-12

- Viewer profiles save name, IP, port, server SHA-256 pin and client certificate thumbprint.
- Existing viewer.json is migrated without modification or deletion.
- GUI Add/Edit/Remove/Identity/Refresh; Setup import-client and list-profiles.
- Repeated pairing is idempotent; saved catalog uses an exclusive file lock and backup.
- Lab .NET 9 Release solution: **PASS, zero errors and warnings**.
- Lab xUnit TLS/protocol/profile and legacy-backup recovery tests: **27/27 PASS**.
- Python offline/installer contracts: **51/51 PASS**.
- Native M2 C++ Release x64 build: **PASS**; Git diff --check: **PASS**.
- PowerShell install/publish full runtime and actual new GUI switching: **NOT EXECUTED**
  through the JJJ Dev MCP. User isolated two-PC acceptance is required.
- User confirmed earlier Lab GUI connection; new profile UI remains to be tested.
- Details: docs/M2_LAB_FINAL_ACCEPTANCE_20261112_KO.md.

## User two-PC acceptance and post-acceptance hardening (2026-11-12)

- User-confirmed live Server/Viewer acceptance: **8/8 PASS** on f06bd426.
- Validated: legacy profile restored, new server pairing, A/B switching,
  keyboard/mouse/display, settings persistence, duplicate pairing protection,
  reconnect behavior, 3-viewer limit, fourth denied, original TightVNC unaffected.
- **After** that acceptance: bound input packets to 32 bytes; bound TLS+Hello
  admission, idle reads and frame writes; stop-session input isolation; bounded
  UI frame queue; reduced JPEG decoding memory allocations; certificate expiry
  metadata in read-only PowerShell probe.
- Lab .NET 9 Release build: **PASS, 0 errors/warnings**.
- xUnit protocol, TLS, byte-compatible concurrent transmission and profile regression: **35/35 PASS**.
- Python offline + hardening source tests: **55/55 PASS**.
- Native M2 C++ Release x64 build: **PASS**.
- These new patches have **not yet** been rerun in a real interactive
  two-PC session. See docs/M2_LAB_POST_ACCEPTANCE_HARDENING_20261112_KO.md.
- The GPL-derived native fork remains blocked for deployment; no claim of
  unattended Windows Service/UAC/production readiness.
