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
