# TJ.RemoteAccess

**Status: M1/M2 foundation (development only, no working remote-control binary).**

A standalone, GPL-2.0-or-later project intended to evolve into a Windows remote-access product based on **TightVNC 2.8.90 GPL**. It is independently buildable and contains no proprietary product source code.

This first implementation includes a portable C++17 session-policy core, automated tests, and tools to import and audit the exact upstream GPL source. **It does not yet ship a VNC server/viewer, mTLS gateway, privileged input agent, installer or externally accessible port.** Do not deploy this prototype as a remote-control service.

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
