# Implementation status and acceptance gates (2026-11-06)

| Gate | Work | Status |
|---|---|---|
| M1.a | Verify upstream ZIP SHA-256 on connected development PC | **HASH VERIFIED (local archive; 3,018,194 bytes)** |
| M1.b | Separate public project source and portable C++17 policy core | **IMPLEMENTED / Linux-tested** |
| M1.c | Validate original Windows TightVNC Server/Viewer Release x64 build | **BLOCKED: original Windows SDK 8.1 toolchain unavailable** |
| M1.d | Audit service / IPC / registry / MSI namespace from extracted source | **Source audit implemented; service/IPC security still blocking** |
| M2.policy | Session gate, authenticated transport binding coordinator, typed native input, CNG session IDs | **Release x64 COMPILE PASS; native runtime tests not executed; no actual mTLS yet** |
| M2.a | Separate names / hook artifacts / listener interlocks in fork | **guarded-v4 Server/Viewer/Control/HookLoader/Hook DLL Release x64 COMPILE PASS; no service coexistence verification** |
| M2.lab | Interactive .NET 9 Lab, multi-server pinning, original TightVNC coexistence | **User 8/8 two-PC acceptance PASS at f06bd42; new changes require field verification** |
| M2.lab.clients | Independent Viewer client certificates, local allowlist, disable, ViewOnly/Controller | **Implemented; Windows TLS roster handshake and revocation loopback tested; 60/60 xUnit PASS overall** |
| M2.lab.capture | One shared JPEG producer for three TLS viewers + read-only performance sampler | **Built + in-memory concurrency tested; CPU/fps and GUI LAN runtime pending** |
| M3.design | Windows Service / interactive SessionAgent privilege and IPC boundary | **Design documented; service, Agent and secure desktop NOT IMPLEMENTED** |
| M2.b | Original + fork parallel installation and independent operation | **NOT TESTED** |
| M2.c | Logon / lock / UAC / reboot | **NOT TESTED** |
| M3 | mTLS Gateway, no direct RFB, least-privilege EngineHost, IPC ACL | **NOT IMPLEMENTED** |
| M4 | Independent Viewer / client management API and integration with an external management client | **NOT IMPLEMENTED** |
| M5 | 270 PPM / RTX3080 / RTX4080 / 3-viewer performance | **NOT TESTED** |
| M6 | Signed installation/update and rollback | **NOT IMPLEMENTED** |
| M7 | Customer site migration and original TightVNC removal | **NOT STARTED** |

## Immediate follow-up on Windows

1. Move or clone **TJ.RemoteAccess** to a separate working directory (not inside any proprietary application repository).
2. Run upstream importer against verified source ZIP.
3. Run identifier audit, then review findings by subsystem before editing C++ names.
4. Build unmodified upstream with the available toolchain; capture exact errors.
5. Implement one minimal service/IPC namespace slice; prove two servers do not interfere.
6. Only after namespace independence, implement Gateway/IPC isolation; do not expose legacy RFB publicly.

No release/public push should be mistaken for completion of the security gates.
