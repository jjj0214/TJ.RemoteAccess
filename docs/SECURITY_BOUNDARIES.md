# Security design invariants

**Status:** design invariants only; not implemented in the upstream TightVNC source.

1. `TJ.RemoteAccess` must run independently. Any separately licensed third-party application remains an **external client**, never a library linked into the GPL engine.
2. No direct RFB TCP listener in the production fork. Even a local listener requires explicit session authorization and a test proving other local principals cannot bypass the Gateway.
3. Remote access requires a VPN/private gateway plus mTLS, a registered per-master identity, and short-lived single-use attach authorization. Internet exposed 5444 is not the default.
4. Fail closed if TLS/client certificate/authentication/control permissions fail; never fall back to unencrypted TightVNC.
5. Windows 10 Schannel may require approved TLS 1.2; Windows 11 supports TLS 1.3. Do not silently downgrade.
6. The VNC parser/decoder is an untrusted attack surface, including from authenticated clients. Move parser to low privilege, patch unsafe IPC (including upstream `setAllAccess()` paths) and fuzz packet decoding.
7. Session policy requires max three slots, 1 controller and 2 viewers, server-authorized control epochs and monotonic input sequences. The final privileged agent validates immediately before input injection.
8. An input-release request is issued on transfer, revoke, disconnect or timeout. **The core holds new control behind `ConfirmInputReleased()`** until the trusted agent acknowledges releasing all keys and buttons; it never claims that this is complete by itself.
9. Client Viewer `Connected` is not true until authenticated first-frame render ACK matching the server session ID. Boot/session IDs and lease status are tracked.
10. No GPL-derived source, other proprietary source, tokens, certificates, real customer identifiers or internal IPs are committed to the wrong repository. Public repository review is required before publication.

## Missing implementation

- Authenticated Gateway, secure transport and production input injector
- Identity namespace fork changes and Windows source compile/rebuild
- Windows logon/UAC integration, service IPC ACL, manifest signing, continuous security review

These are **blocking** for a deployable remote-access product.
