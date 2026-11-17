# TJ.RemoteAccess Preview UI + LAN installer follow-up (2026-11-17)

## Current scope (feature/chatgpt-product-ux-m3)
The 0.5.4-preview Compact installer and interactive Windows Server/Viewer are enhanced, not replaced by a production remote desktop engine. TightVNC is never uninstalled or reconfigured.

### Implemented in this branch
- Installer Server shortcut passes `--allow-lan`; standalone Preview Server directly launched also accepts LAN mode by default. Lab test Server continues to require the explicit LAN switch.
- New Server installs invoke `Setup.exe init-server` after successful component installation. If there is no existing `server.json`, a current-user TLS server certificate/private key and LAN address `0.0.0.0:15544` are initialized. Existing server settings, TLS pins, PFXs, and Viewer profiles are NOT overwritten. This does not enable password auth, create client grants, install a service, or change Windows Firewall.
- Server displays a single Access settings entry in its window and tray. The access-management window retains certificate Viewer controls and offers an entry into the password configuration dialog. Password changes still require the current secure listener-restart logic.
- Viewer can display Fit, 50%, 75%, 100%, 125%, 150%, and 200% local scales with scrollbars, save a local PNG snapshot, freeze/resume local rendered frames while continuing to read TLS, and send Ctrl+Shift+Esc only while its Server-assigned role is Controller. Pausing releases held remote keys and blocks new input.

### Deliberately NOT implemented or verified
- TLS-authenticated bidirectional FILE TRANSFER. Needs a distinct authorization capability, per-file consent and safe destination picker, filename/path normalization, quotas, bounded chunks, cancellation, hash integrity, atomic writes, and no arbitrary path escape.
- Remote scroll wheel, clipboard sharing, Ctrl+Alt+Del / UAC / Winlogon input, and selectable remote JPEG quality/fps. A changed on-wire input protocol must be understood and checked by both Server and Viewer before adoption.
- Status-only Windows Service to full authenticated remote desktop Agent integration, automatic unattended post-boot remote operation, safe desktop transition, and actual SCM service lifecycle verification.
- Live Windows 10/11 GUI and 2-PC acceptance for new zoom/coords/keyboard controls, LAN firewall readiness, and exact installation in a clean user profile.
- Actual long-running 3-viewer or inspection-system performance.
Do not advertise this branch as TightVNC feature parity or as production ready.

### Acceptance gates
1. Release build and Preview/Lab tests PASS; portable Python contract tests PASS except a documented pristine TightVNC GPL source absence in the local checkout.
2. Compact Installer GitHub Windows CI PASS for Server-only new install, Viewer-only install, upgrade, idempotent server identity, TLS certificate private key, local Runtime, clean uninstall, and configuration preservation.
3. Two isolated Windows PCs: installer double-click, Server no startup error, actual 15544 TLS certificate pairing, optional 15545 password + verified pin, Viewer 3 max/Controller 1 max.
4. In Viewer test Fit + 50/75/100/125/150/200 scale, clicking/dragging at scroll offset, local freeze/resume, PNG screenshot, Controller-only remote task manager shortcut, and pressed-key release on disconnect.
5. Explicit user approval before field deployment, service enablement, privileged desktop control, or TightVNC removal.

## Security behavior
An allow-lan bind only controls network interface choice; it does not allow unauthenticated Viewer access. No plaintext password mode and no automatic password listener. This installer does NOT add firewall rules. For already-provisioned installations with `server.json` bound to loopback, operator must explicitly review/adjust the existing listen-address setting; installer preserves that configuration by design.
