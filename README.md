# TJ.RemoteAccess

**Status: M2 guarded native fork remains development-only; a separate interactive .NET 9 Lab Server/Viewer is available for isolated-PC testing.**

A standalone, GPL-2.0-or-later project intended to evolve into a Windows remote-access product based on **TightVNC 2.8.90 GPL**. It is independently buildable and contains no proprietary product source code.

The native foundation includes a C++17 session-policy core, EngineSessionGate and guarded GPL source transformations. **The native fork has no deployable authenticated Gateway, privileged input agent or service installer.** The separately implemented Lab Server/Viewer uses mutual TLS certificate pinning and a current-user interactive desktop, not the native fork. Never deploy either as a production remote-control service.

See docs/M2_IMPLEMENTATION.md for the Windows Release x64 compile evidence and the outstanding original TightVNC fork acceptance gates.

Windows 개발 PC에서 빌드와 테스트를 진행하려면 docs/M2_WINDOWS_TEST_KO.md를 참고하세요. 파생 Server/Viewer 시험 빌드는 tools/Build-M2Fork.ps1이 원본 GPL ZIP을 검증한 후 별도 작업 폴더에 생성합니다.

Windows M2 progress (2026-11-11): source-only `NativeInputBridge` and Win32 namespace
transformation tests compile/pass, and the derived Server/Viewer compile with MSBuild.
**Neither RFB authentication nor actual screen/input transport has passed runtime QA.**
See `docs/M2_RFB_INTEGRATION_REVIEW.md` and `tools/audit_m2_fork_security.py`
for concrete implementation blockers. The static audit never approves deployment.

## Independent TJ.RemoteAccess preview (Windows x64, isolated test PCs only)

- Product-facing solution: `TJ.RemoteAccess.sln` builds **0.5.3-preview** `TJ.RemoteAccess.Server.exe`,
  `TJ.RemoteAccess.Viewer.exe`, `TJ.RemoteAccess.Setup.exe`, an authenticated
  `TJRA.ControlService` + per-user Agent, and regression tests. It uses its
  own user configuration (`%LOCALAPPDATA%\\TJ.RemoteAccess`) and TCP 15544/
  optional 15545 rather than Lab 15444/15445 or TightVNC 5900.
- Standalone Viewer **Quick connect (IP + password)** on TLS port 15545:
  no Viewer PFX/certificate needed. The Viewer first probes a self-signed
  Server's SHA-256 fingerprint over TLS WITHOUT sending its password, closes
  the probe, requires explicit verification against the real Server PC,
  then opens a SECOND exact-pinned TLS channel to authenticate. Only a
  successful Server Welcome remembers the endpoint's public SHA pin.
  Changed identities fail closed; passwords are never persisted in Viewer.
  The original certificate/pinned TLS mode and 3-Viewer/1-Controller policy
  remain intact. Server tray > Password access is opt-in and ViewOnly by
  default. Guide: `docs/QUICK_PASSWORD_ACCESS_0_5_2_20261116_KO.md`.
- Windows Server **system tray UI**: live gray/blue/green status icon,
  up to 3 Viewer/controller count, Open/Start/Stop, Client access, Password
  access and explicit Exit. Window X/minimize hides to tray, not disconnects.
  A second launch restores the existing Server rather than duplicating icons.
  Tray menu also allows the local user to opt in/out of service supervision;
  it does not bypass the independent administrator policy.
- Updated `Measure-TJRemoteAccess-Preview.ps1` performs read-only CPU,
  memory, handle/GDI/USER sampling for Server/Viewer/Agent/Service without
  running or reconfiguring remote-access components. Long-running 270 PPM
  and GPU/tact impact acceptance still requires real inspection systems.
- A user-approved visible desktop Server can be supervised by a Windows Service:
  both administrator-owned `desktop-host-policy.json` AND current-user TLS
  certificate-bound `service-host enable --confirm-local-owner` enrollment are
  required. Default is status-only, and a Job object kills the Server if its
  user Agent dies. Winlogon, lock screen, UAC/secure desktop are **denied**.
- **0.5.5-preview compact offline installer (Server/Viewer UX and transfer update):**
  Native Inno Setup 6 produces ONE install EXE (no managed Installer runtime).
  Server/Viewer/Setup are .NET 10 framework-dependent, each apphost searches
  ONLY ../Runtime. Microsoft .NET 10.0.15 Core and WindowsDesktop runtime ZIPs
  are independently SHA-512 authenticated against official release metadata,
  then merged ONCE into the common private Runtime folder (no duplicate hosts).
  Component choices: Server+Setup, Viewer+Setup, Both; GPL corresponding
  source.zip is embedded in the EXE. Per-user install, update/uninstall, and
  LocalAppData TLS/profile preservation are tested in a dedicated Windows
  compact-installer.yml CI job. The old 0.5.3 installer is retained as fallback.
  New compact CI requires .NET 10 SDK/Inno Setup 6; the existing local machine
  currently only has SDK 9.0.318, so it validates legacy paths separately.
  0.5.5 adds an installed Server LAN shortcut, one Access/Security manager,
  optional signed-in-user startup, Viewer scale/fullscreen/pause/resume,
  local view-only, normal-desktop key chords, wheel and TLS file transfer
  with receiver approval, SHA-256 and a strict 512 MiB/file cap.
  Optional M3 supervision has bounded I/O recovery and resolves the installed
  current-user Compact Server, but the LocalSystem service still has a
  separate administrator-owned opt-in installation and cannot control
  Winlogon/UAC or a user who has not logged on.
  Instructions: docs/COMPACT_VIEWER_UX_0_5_5_20261117_KO.md.
- Standalone **0.5.3-preview graphical installer**:
  `TJ.RemoteAccess.Installer.exe` is self-contained for Windows x64,
  double-clickable (no PowerShell or .NET SDK needed on the target PC).
  It accepts an offline ZIP and SHA-256, shows progress/logs/active
  TJRA process IDs, validates every manifest file before changing any
  installed component, and rolls back incomplete component swaps.
  The offline ZIP builder writes explicit forward-slash names using
  `tools/New-TJRemoteAccess-PreviewZip.ps1` (never implicit Windows
  `Compress-Archive` file names). Windows integration tests run the exact
  builder on a disposable Git repository and verify each ZIP manifest name,
  size and SHA-256 to prevent the previously reported
  `Archive missing manifested file` regression. The GUI/offline
  packaging path also avoids `Get-FileHash`: a .NET SHA-256 file-stream
  helper is tested with `Get-FileHash` deliberately unavailable, to
  prevent the GitHub Windows runner command-not-found regression.
  Build artifact is produced in GitHub Actions from
  `tools/Build-TJRemoteAccess-Preview-GuiPackage.ps1`;
  this is an **unsigned EXE setup window, not an MSI**.
  The old PowerShell offline installer remains as a fallback, now with
  streaming hash validation/progress, instead of silent full ZIP extraction.
  Instructions: `docs/GUI_INSTALLER_0_5_3_20261116_KO.md`.
- The password-mode Server minimum is **6 characters** (5 rejected;
  12+ recommended). TLS 1.2/1.3, Server pinning, lockout controls and
  Viewer password non-persistence remain unchanged.
- Developer-only `Build-TJRemoteAccess-Preview-OfflinePackage.ps1` and
  `Build-TJRemoteAccess-ServicePreviewPackage.ps1` produce GPL
  source-corresponding, unsigned, checksum-manifested ZIP artifacts.
- Development tests (Release compilation and process/IPC checks) **do not**
  prove LocalSystem SCM installation, real remote GUI from the service,
  production security, Win10/11 compatibility or unattended/secure desktop
  behavior. The standalone build is a **PREVIEW, NOT a finished TightVNC
  replacement**. Do not deploy at customer facilities.
- Release build of Product and Lab solutions and C++ native test projects, Windows xUnit **170/170**, product-preview xUnit **42/42**, and Python unittest all passed (2026-11-16). Windows PowerShell AST parsing of installer scripts passed; actual installer/SCM execution, GUI acceptance, code signing and MSI are **not tested**.
- Windows test gates and initial commands:
  `docs/STANDALONE_PREVIEW_BUILD_TEST_20261116_KO.md`. Tray-specific
  test steps and 0.5.1-preview acceptance checklist:
  `docs/TRAY_0_5_1_ACCEPTANCE_20261116_KO.md`.

## Interactive Lab remote-control prototype (Windows, test PC only)

- Solution: TJ.RemoteAccess.Lab.sln (Server, Viewer, Setup, xUnit tests).
- Build: dotnet build TJ.RemoteAccess.Lab.sln -c Release
- Install current user: powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/Install-TJRemoteAccess-Lab.ps1 -Mode Both
- Create local server/client certificates with the installed Lab.Setup.exe init.
- Start Lab Server UI, click Start, then connect Lab Viewer to 127.0.0.1:15444.
- Independent .NET 9 implementation: it does NOT replace or unblock the GPL
  guarded-v4 native fork. Actual remote desktop GUI/keyboard/mouse operation
  passed user two-PC acceptance 8/8 on the previous Lab build. Further transport
  hardening requires a short recheck before broader test use.
- Full instructions and limits: docs/M2_LAB_INSTALL_TEST_KO.md.
- Multi-server Viewer update (2026-11-12): a saved server dropdown, Add/Edit/
  Remove/Identity/Refresh controls and secure per-server certificate pins.
- Existing viewer.json is auto-migrated without overwriting the original file.
- CLI Setup import-client now adds multiple independently pinned server profiles.
- Current-user installer stages every component before replacement and retains
  LocalAppData certificates/profiles across upgrades.
- **Latest two-PC acceptance guide:** docs/M2_LAB_FINAL_ACCEPTANCE_20261112_KO.md.
- **8/8 user acceptance and transport resilience updates:** see
  docs/M2_LAB_POST_ACCEPTANCE_HARDENING_20261112_KO.md.
- Per-Viewer TLS certificates, independent allow/disable and view/control rights:
  docs/M2_LAB_MULTI_CLIENT_ACCESS_20261112_KO.md.
- One shared JPEG capture producer for up to three TLS viewers, with optional
  read-only CPU/memory sampling: docs/M2_LAB_SHARED_CAPTURE_PERFORMANCE_20261112_KO.md.
- Secure Windows Service / user-session agent separation (design only):
  docs/M3_SERVICE_AGENT_ARCHITECTURE_20261112_KO.md.
- Optional **password authentication over server-pinned TLS** on separate
  port 15445 (default disabled; existing client-certificate port 15444 remains
  unchanged). Server GUI generates Viewer-2/Viewer-3 PFX bundles directly:
  docs/M2_LAB_PASSWORD_AND_GUI_PAIRING_20261112_KO.md.
- Current-user startup, trusted-profile-only external Viewer CLI, audited
  connections, M3 named-pipe authentication, and unsigned offline packaging
  have isolated tests, but do NOT constitute Windows Service/UAC completion.
  Details and remaining gates: docs/M3_M6_PROGRESS_AND_GATES_20261113_KO.md.
- The unified `TJ.RemoteAccess.Lab.sln` also builds M3 Service/Agent; SDK-style
  projects sharing `src/TJRemoteAccess.Core` use per-project intermediate `obj` folders.
- M3 Windows Service + SessionAgent **status-only** development checkpoint (2026-11-16):
  single-session revoke/rebind with exact active-console session validation,
  cross-process inherited-key Win32 IPC acceptance,
  bounded handshake, diagnostic status, checksum-verified **manual-start** Lab
  service ZIP/installer/rollback scripts. This is NOT a remote-control service;
  production MSI, code signing, unattended access and UAC/Winlogon
  are NOT complete. See docs/M3_STATUS_ONLY_LAB_PACKAGE_AND_GATES_20261116_KO.md.

## Prerequisites

- Git, CMake >= 3.18, a C++17 compiler (MSVC VS 2022 recommended for Windows)
- Python 3.10+ (for upstream import/audit and tool tests)
- TightVNC 2.8.90 GPL source ZIP downloaded from the official website

## Build and tests

```powershell
cmake -S . -B out/build -A x64
cmake --build out/build --config Release
ctest --test-dir out/build -C Release --output-on-failure
python -m unittest discover -s tests -p "test_*.py" -v
```

For Linux/gcc, omit `-A x64` and use `-DCMAKE_BUILD_TYPE=Release`.

## Import exact upstream source (offline)

Download the official GPL source ZIP from the upstream site and supply its local path. The importer accepts any location, but only the official bytes with SHA-256
`f3f76393a5865e1f1919c78c3eb43b9c937cf738927c2bfc920ecbd3913b061c`.

```powershell
python tools/vendor_upstream.py --zip "C:\Downloads\tightvnc-2.8.90-src-gpl.zip"
python tools/audit_identifiers.py --source third_party/tightvnc-2.8.90-gpl --out reports/identity-audit.json
```

Create the `reports` directory before requesting `--out`. Import is **fail-closed**: it verifies the hash, rejects unsafe ZIP names, limits archive size and never overwrites an existing source folder.

The upstream `.sln` and `.vcxproj` are older Visual Studio projects. Their Windows build is a separate M1 acceptance gate; it is **not validated** by the portable core build.

## Security and legal boundaries

- Avoid internet exposure: no public RFB listener; future clients must authenticate via a VPN/private network and an mTLS Gateway.
- Gateway must validate individual principals, short-lived attach authorization, rate limits and bounded stream sizes.
- RFB parser and Viewer decoder must remain untrusted even after TLS authentication.
- The privileged input agent must independently validate control rights and physically release held keys/buttons on revocation. The core requires a release acknowledgement before the next controller is activated.
- A separate public administrative protocol should allow independent software to invoke this tool as an **independent program**. Separately licensed consumers should not link/copy GPL engine classes or share internal memory structures. This separation helps, but does not itself settle the legal GPL boundary.
- Keep original copyright and license notices in imported upstream files. Do not replace attribution with TJ.
- Before publishing, verify employment/IP rights, GPL-compatible source obligations, upstream third-party licenses and source-to-binary correspondence.

See `docs/ROADMAP.md` and `docs/SECURITY_BOUNDARIES.md`.
