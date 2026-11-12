# TJ.RemoteAccess M2 - authenticated session policy implementation (2026-11-11)

Branch: `feature/chatgpt-m2-engine-foundation`
Environment: JJJ Dev MCP v1.8.0, Visual Studio 2022, Release x64.

## Scope

This checkpoint adds an **in-process policy/lifecycle component**, not an
operational encrypted remote desktop server.

- `src/TJRemoteAccess.Core/authenticated_session_coordinator.hpp/.cpp`:
  the transport's trusted attestation callback binds a connection handle to
  a principal, unique live-channel binding, and server-issued session ID.
- Connections are re-attested on reserve, attach, control grant and input.
  A different identity/binding/session ID fails closed. Attestation exceptions
  and unauthenticated handles fail closed.
- Reserved sessions expire after at most 60 seconds. Expired reservations are
  collected before a new connection is admitted. Normal is 1 viewer and
  Maintenance is 3 viewers (existing EngineSessionGate rules).
- Only a server-owned increasing input sequence and a current gate epoch may
  reach the NativeInputBridge. No client-supplied epoch or sequence is trusted.
- Native input delivery failure revokes control and requires a separate,
  **real physical key/button release** before control resumes.
- Explicit disconnect and `SweepDisconnectedChannels` remove dead/replaced
  channels. The sweep reports key-release required and the exact release epoch.
  It does **not** perform or acknowledge physical input release.
- `secure_session_id.cpp` uses Windows CNG `BCryptGenRandom` for 256-bit
  server-side opaque session identifiers (64 lowercase hex characters).
  Failure has no weak random fallback.
- `CMakeLists.txt`, `tests/TJRemoteAccess.EngineGate.Tests.vcxproj`, and
  `tests/engine_session_gate_tests.cpp` include the new components and
  extended negative-path test scenarios.

## Important design boundary

`AuthenticatedChannelEvidence` is provided by a callback owned by a
**future mutually authenticated transport**. A fake callback is used in
C++ policy tests. No actual TLS certificate validation, TLS exporter,
client-certificate allowlist, signed short-lived attach token or Windows
named-pipe ACL has been implemented here.

The production transport MUST:

1. Validate client certificate chain, EKU, revocation policy, trust pins
   and authorized client/principal mapping (never trust RFB credentials).
2. Generate a distinct strong channel binding per live authenticated TLS
   connection and a new OS-CSPRNG session ID; pin both to the native connection
   lifetime. Never reuse an active handle for another TLS connection.
3. Call `Disconnect` on every transport close/failure and call
   `SweepDisconnectedChannels` to handle missed close events.
4. Install a privileged, non-reentrant final Windows input sink and release
   *all* physically held keys/buttons before calling
   `EngineSessionGate::ConfirmInputReleased`.
5. Enforce least-privilege local IPC ACLs; the RFB parser and Viewer cannot
   manufacture `AuthenticatedChannelEvidence`, invoke the attestor, or
   directly reach OS input APIs.
6. Prevent any bypass through legacy listener/clipboard/file-transfer,
   reverse RFB, mirror driver, service control or additional hook paths.
7. Connect actual server-side RFB sessions to the coordinator only after
   trusted transport and input protection exist. The guarded-v4 binary still
   intentionally disables legacy RFB, HTTP, file transfer and OS input.

The code is not a cryptographic authentication implementation or proof of
end-to-end channel safety. A policy build does not permit production use.

## Verification

- M2 native C++ test solution **Release x64 compile PASS**, zero reported
  errors/warnings after adding coordinator and Windows CNG generator.
- Python unittest: **41/41 PASS** on the current v4 fork-generation and audit
  suite (count taken from the detailed test log).
- `git diff --check`: PASS.
- CMake build: unavailable through the current JJJ Dev tool invocation.
- C++ test EXE *execution*: **NOT RUN** in this MCP session. The binary could
  not be opened through the artifact metadata tool despite the MSBuild
  success path. Do not report the new C++ scenarios as executed.
- Actual mTLS, RFB screen/keyboard/mouse, service coexistence, input
  release, secure desktop/UAC, and performance/soak tests: **NOT TESTED**.
- PowerShell script execution: **NOT RUN** (no general PowerShell execution
  action is exposed by the JJJ Dev MCP connector). Source/contract tests and
  typed MSBuild were used instead.
- No service, registry, firewall, TLS credential or live VNC process was
  installed, started, modified or stopped.

## Next integration gate

Implement the trusted Windows mTLS gateway and authenticated IPC channel,
then hook the real RFB connection lifecycle and final privileged input sink
into this policy. Run native C++ tests with the required exact SHA-256 and
explicit approval, then isolated Windows service coexistence and real
screen/input trials. Preserve TightVNC GPL license and attribution.

**Deployment allowed: NO.**
