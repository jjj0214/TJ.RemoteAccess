# Draft external management protocol (not implemented)

`TJ.RemoteAccess` exposes a stable external process/admin interface for orchestration by an independent application from a separate program. Management API calls **do not** carry frames or input events.

- `GetCapabilities`: engine/protocol version; supported features; supported OS sessions
- `GetStatus`: installed/running/state/certificate health; active/available session count
- `StartApprovedSession`: authorized principal and target; returns opaque short-lived attach reference
- `EndSession`: session id and caller credentials; target enforces identity and privilege
- `RevokeControl`: authorized revocation; must complete privileged key-release sequence

All methods are subject to authenticated principal and authorization checks. Not part of the transport source or TightVNC internals. Schema versioning, transport (Windows named pipe vs authenticated local RPC) and binary compatibility require separate review before implementation.
