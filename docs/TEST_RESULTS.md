# Test evidence — 2026-11-06

Environment: container Linux, g++ 14.2.0, CMake, Python 3.x.

| Check | Result |
|---|---|
| Fresh CMake configure (Debug) | PASS |
| Build C++17 portable session core | PASS |
| CTest session registry | PASS (including 64 concurrent reservations) |
| Python offline importer/audit unit tests | PASS (7 tests) |
| Archive SHA-256 on developer PC | PASS: `F3F76393A5865E1F1919C78C3EB43B9C937CF738927C2BFC920ECBD3913B061C` |
| Real 2.8.90 source import | NOT TESTED in this environment (ZIP is on a different PC) |
| Original Windows TightVNC build | NOT TESTED |
| Windows x64 build and runtime | NOT TESTED |
| Parallel service install, UAC and logon | NOT TESTED |
| mTLS, privileged input, RFB hardening | NOT IMPLEMENTED |

Passing the portable core tests **does not** establish remote-control functionality or network security. CI workflows are provided but have not been run on GitHub.
