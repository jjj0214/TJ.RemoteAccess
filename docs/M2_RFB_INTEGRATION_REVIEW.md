# M2 RFB integration boundary review (2026-11-11)

Status: DESIGN / SOURCE REVIEW ONLY. No live RFB gate has been wired. Never deploy this derived fork or open an external listener on the basis of this document.

## Confirmed upstream call sites (derived source under out/m2-fork/.../upstream)

1. `tvnserver-app/RfbServer.cpp` `onAcceptConnection` (57-86): incoming TCP sockets are admitted based on IP access rules, then handed to `RfbClientManager::addNewConnection`. IP allowlisting is NOT a cryptographic client identity.
2. `rfb-sconn/RfbClient.cpp` `execute` (190-209): legacy `RfbInitializer::authPhase` runs, followed by `RfbClientManager::onClientAuth`. Existing VNC password success is NOT sufficient to supply `trusted_principal_id` to `EngineSessionGate`.
3. `tvnserver-app/RfbClientManager.cpp` `onClientAuth` (57-120): existing shared/exclusive behavior can disconnect existing clients. It must be reconciled with Normal=1 and Maintenance=3 admission rules before the fork can run.
4. `rfb-sconn/ClientInputHandler.cpp` `onRequest` (43-65): keyboard and pointer events dispatch through `ClientInputEventListener` after only a view-only check.
5. `rfb-sconn/RfbClient.cpp` `onKeyboardEvent` (316-333) and `onMouseEvent` (335-360): events reach `m_desktop->setKeyboardEvent` and `m_desktop->setMouseEvent` without `EngineSessionGate::DispatchInput`. These calls are the current bypasses that must be closed at the final trusted injection boundary.
6. `RfbClient.cpp` (283-296) and `RfbClientManager.cpp` `validateClientList` (260-309): asynchronous disconnect/removal needs exactly-once `End` and physical key/button release before control transfer.

## Required fail-closed integration contract

- Do not synthesize an authenticated principal from remote IP address, user-agent, RFB shared flag, VNC password, or a field supplied by the peer.
- Establish a cryptographically authenticated, freshness-checked identity and an unforgeable connection-to-session binding through a trusted local gateway/transport BEFORE attempting `Reserve` or `Attach`. Until this exists, reject production RFB sessions.
- Bind every authenticated connection to a distinct securely generated session ID, immutable trusted principal, current control epoch, and monotonically increasing per-session input sequence. A client must not choose its own trusted principal or epoch.
- Reserve before admitting the RFB session, attach only after the authenticated connection is established, and atomically roll back reservations on all failure paths. Normal permits 1 viewer, Maintenance 3 viewers, with one controller maximum; refuse the fourth session.
- Fail closed on missing authentication, stale epochs, replay, input-release pending, callback failure, shutdown, or unexpected exception.
- Route ALL keyboard and pointer paths through the same non-reentrant final injector callback guarded by `EngineSessionGate::DispatchInput`. Do not only check the viewer UI or `ClientInputHandler`. Audit clipboard/file transfer, reverse RFB, HTTP VNC and hook/IPC paths separately; disable unused functionality by default.
- On revoke, disconnect or transfer, physically release held keys/buttons via a trusted native agent; call `ConfirmInputReleased` only after successful release. No pending transfer may be completed by an untrusted caller.
- Avoid mixing the RFB client's existing list mutex with the gate mutex in inconsistent lock order. All callbacks must be short, non-blocking, noexcept and non-reentrant.
- Generated vendor-derived files under `out/` are NOT authoritative project source. Make persistent fork changes through reviewed, hash-pinned transformation scripts or tracked GPL-derived patches and preserve provenance.

## Test gates before any service installation

1. Authenticated and unauthenticated local-loopback admission, session binding, and fail-closed default.
2. 1/3 viewer capacity, fourth viewer rejection, one controller, transfer, revoke, expired reservation and disconnect release.
3. Keyboard/mouse cannot reach OS injection without a current authorization; negative tests for replay, wrong principal and stale epoch.
4. Independent identifiers: service name, EXE, MSI product/upgrade codes, registry, port, mutex, pipe, mapping, log/config paths and hook DLL.
5. Static auditing plus dynamic isolated Windows 10/11 coexistence and screen/control testing. No firewall/service changes on development or production PCs without separate approval.

## Current verification (JJJ Dev v1.8.0 MCP)

- `project_discover`: PASS (M2 test solution and projects found; generated/vendor projects intentionally excluded).
- Derived Server Release x64 `project_build`: PASS; one C4819 code-page warning (`log-writer/ProfileLogger.h`).
- Derived Viewer Release x64 `project_build`: PASS, zero reported warnings.
- `project_analyze_log`: PASS, correctly extracted Server warning.
- Python `project_test` unittest suite: PASS, discovered tests; tool did not report an exact test count.
- `tests/TJRA.M2.Tests.sln` Release x64 `project_build`: PASS.
- Native test executable execution: NOT RUN; exact artifact hash and explicit `NATIVE_TEST_EXECUTION_APPROVED` required.
- Screen/control, independent Windows services, crypto identity/IPC security: NOT TESTED or NOT IMPLEMENTED.

This document is a technical review and implementation contract, not a security certification.
