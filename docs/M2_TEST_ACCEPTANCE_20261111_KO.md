# TJ.RemoteAccess M2 - 2026-11-11 acceptance and development checkpoint

## User-run test evidence (JJJ Dev Windows PC)

- `tools/Run-M2CoreTests.ps1`: **PASS**. Output ended with
  `M2 local core tests PASS. No VNC Server/Viewer was launched.`
- `tools/Inspect-M2Coexistence.ps1`: READ ONLY, normal result.
  - Original `tvnserver` installed, **Stopped**, PID 0.
  - `TJ.RemoteAccess.Server` **NotInstalled**.
  - Original RFB 5900 listeners 0; fork listeners 0; non-loopback listeners 0.
  - Actual two-service coexistence and GUI/input remain **NOT TESTED**.
- `tools/audit_m2_fork_security.py`: **17 findings** in the existing
  generated fork (8 known risk signatures + 9 shared Win32 identifiers).
  `BLOCKED_FOR_DEPLOYMENT` is the expected protective status.

These are development-machine results supplied by the user, not a security
certification or evidence of a functioning remote desktop session.

## Additional source-only changes made after test review

- `tools/m2_fail_closed.py`: six mandatory fail-closed transformations
  for the *next generated fork*, preserving the original GPL source:
  1. Disable creation of main unauthenticated RFB listener.
  2. Disable legacy HTTP viewer listener.
  3. Disable legacy, overly permissive local Control named pipe.
  4. Disable extra RFB listener reload path.
  5. Disable extra RFB listener startup path.
  6. Reject legacy RFB session admission without a cryptographically
     authenticated and connection-bound TJRA principal.
- `tools/prepare_m2_fork.py` now requires all six transformations to be
  applied and writes their IDs and explicit `production_deployment_allowed:
  false` into the derived-fork manifest.
- `tools/audit_m2_fork_security.py` checks source markers and reports
  `generationInterlocks=NOT_APPLIED_TO_THIS_FORK` for older generated trees,
  or `SOURCE_ONLY_PRESENT` for newly generated trees. Even a source-only
  present result never permits deployment.
- Existing identity-generation rules preserve vendor attribution and
  isolate selected Win32 object names; hook DLL/EXE and mirror-driver
  interoperability remain blockers.

## Verification

- **Python unittest: 37/37 PASS**, including real extracted GPL source
  structure compatibility for all six fail-closed transformation methods.
- The transform compatibility test is read-only: it neither regenerates a fork
  nor verifies that newly transformed C++ builds successfully.
- Existing M2 C++ test solution previously compiled Release x64; user
  subsequently ran `Run-M2CoreTests.ps1` and reported PASS.

## Crucial socket implementation detail

In upstream `network/TcpServer.cpp`, `TcpServer::TcpServer` calls
`m_listenSocket.bind()` and `m_listenSocket.listen()` **in its constructor**.
Removing `TcpServer::start()` alone DOES NOT prevent the socket from opening.
For this reason, the M2 fail-closed transformations prevent construction of
legacy RFB/HTTP servers instead of merely skipping their start threads.

## Remaining gates (do not deploy)

1. Regenerate a **new**, separate hash-verified derived source tree rather
   than overwriting the existing generated fork; build new Server/Viewer/Control
   projects Release x64 and audit resulting manifests.
2. Implement trusted gateway identity, short-lived attach authorization,
   least-privilege IPC ACLs, final privileged input-agent enforcement and
   physical key-release acknowledgement.
3. Independently isolate service, registry, per-user configuration, ports,
   named objects, hook DLL and hook loader identifiers; verify actual binaries.
4. Run native C++ tests under their approved SHA-256 execution policy.
5. Perform Windows 10/11 isolated-PC screen/input, two-service coexistence,
   login/UAC/lock and rollback acceptance tests only after appropriate review.

Do not start a generated server on a production PC, enable a firewall rule,
install services or publish the project at this stage.
