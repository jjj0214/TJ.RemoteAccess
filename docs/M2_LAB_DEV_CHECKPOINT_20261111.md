# TJ.RemoteAccess M2 Lab Development Checkpoint — 2026-11-11

## Branch and scope

- Worktree: D:\Repository\TJ.RemoteAccess-ChatGPT-M2-Engine
- Branch: feature/chatgpt-m2-engine-foundation
- Worktree retains uncommitted M2 changes. No commit/push/tag/release.
- The original TightVNC service and guarded-v4 fork were not installed or launched.
- The LAB is a separate **.NET 9 self-contained test implementation**, not a production-strength fork integration or a tested unattended service.

## New deliverables

- TJ.RemoteAccess.Lab.sln (VS2022, .NET 9 x64 projects)
- src/TJRemoteAccess.Core/TJ.RemoteAccess.Lab.Server.csproj
- src/TJRemoteAccess.Core/TJ.RemoteAccess.Lab.Viewer.csproj
- src/TJRemoteAccess.Core/TJ.RemoteAccess.Lab.Setup.csproj
- src/TJRemoteAccess.Core/LabServer.cs: visible current-user desktop host, 3 TLS-authenticated viewers, one controller, GDI JPEG capture, mouse/keyboard injection + release on disconnect, explicit LAN opt-in, default loopback.
- src/TJRemoteAccess.Core/LabViewer.cs: WinForms remote viewer, TLS client certificate authentication, server SHA256 pin, JPEG display, keyboard and pointer send, disconnect/keepalive.
- src/TJRemoteAccess.Core/LabProtocol.cs: bounded framed messages, validation of key/pointer/desktop metadata.
- src/TJRemoteAccess.Core/LabCertificates.cs and LabSetup.cs: Windows CurrentUser private certificates, 30-day self-signed pinned server/client certs, password-protected client pairing export, secure import.
- tools/Install-TJRemoteAccess-Lab.ps1: user-only, self-contained win-x64 publish and shortcuts; NO service or firewall changes.
- tools/Uninstall-TJRemoteAccess-Lab.ps1 and Test-TJRemoteAccess-Lab.ps1: user-only uninstall and read-only state check.
- docs/M2_LAB_INSTALL_TEST_KO.md: installation and actual remote-control test steps, including two-PC pairing.
- tests/TJ.RemoteAccess.Lab.Tests.csproj: protocol and actual mutually authenticated TLS loopback xUnit tests.
- tests/test_m2_lab_delivery.py: static installer/certificate/project safety contract tests.

## Verified directly on JJJ Dev Windows PC

- TJ.RemoteAccess.Lab.Server.csproj, Viewer.csproj and Setup.csproj Release x64: PASS (0 errors, 0 warnings).
- TJ.RemoteAccess.Lab.sln Release Any CPU/x64-target projects: PASS (0 errors, 0 warnings).
- xUnit 11/11 PASS, including actual TCP loopback TLS mutual authentication, correct client certificate pin enforcement and rejection of non-allowlisted client identities.
- Python repository tests: previously 41/41 PASS, new delivery contracts added (verify current final count from test output).
- Existing M2 C++ test solution Release x64: previously PASS, compilation only.
- Previous guarded-v4 native fork Server/Viewer/Control/HookLoader/ScreenHooks Release x64: PASS, compile only.

## Not verified or not implemented

- PowerShell installer/publish complete runtime was NOT executed remotely: JJJ Dev MCP has typed MSBuild/dotnet tools but no arbitrary PowerShell execution tool.
- No real Lab Server/Viewer window was started by ChatGPT: actual desktop image, input injection, clipboard and GUI interaction were NOT field tested.
- No Windows service, logon/lock/UAC secure desktop, unattended connectivity, screen capture soak, 3-viewer performance, LAN firewall acceptance or installer rollback was validated.
- Lab is not integrated with native C++ EngineSessionGate; guarded-v4 GPL-derived fork remains fail-closed. The Lab does not claim to resolve all fork legacy attack surfaces.
- Test certificate revocation/rotation, enterprise PKI and endpoint security certification remain incomplete.

## Security caveat

The Lab uses self-signed short-lived certificates pinned by SHA-256 DER hash rather than enterprise PKI. TLS encryption works in xUnit loopback; it has no certificate revocation checks, automated key renewal or production auth controls. Running on an interactive user account does NOT grant access to the Windows secure desktop.

**Decision: do not deploy to customer/production/Internet.** Test only on an owned isolated Windows machine after reviewing the pairing files. Full TJ.RemoteAccess remote-access engine still requires native fork integration, trusted IPC and service acceptance.

See docs/M2_LAB_INSTALL_TEST_KO.md for hands-on validation commands.

## Final build/tooling reconciliation

- Lab solution Release build: PASS, errors 0/warnings 0.
- Lab xUnit tests: 11/11 PASS; denied-client TLS handshake verified.
- Python offline and delivery tests: 46/46 PASS.
- Native C++ M2 test solution Release x64: PASS after separating native
  MSBuild NuGet project-extensions paths from the sibling .NET xUnit project.
- Git ignore rules now exclude generated nested bin/ and obj/ trees.
- Git diff --check: PASS; no commits/push performed.
- Actual current-user PowerShell installation and GUI/physical input remain
  unverified by the JJJ Dev MCP; user isolated Windows acceptance is required.
