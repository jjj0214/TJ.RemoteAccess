# SPDX-License-Identifier: GPL-2.0-or-later
# Baseline checks only. Does not install/start services or change Windows firewall.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $TightVncSourceZip,
    [switch] $TryUpstreamBuild
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) {
        throw 'Python 3.10+ is required for the verified upstream source importer.'
    }
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
        throw 'CMake 3.18+ is required.'
    }
    python tools/vendor_upstream.py --zip $TightVncSourceZip
    if ($LASTEXITCODE -ne 0) { throw 'Upstream ZIP verification/import failed.' }
    $reportDir = Join-Path $root 'reports'
    New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    python tools/audit_identifiers.py --source third_party/tightvnc-2.8.90-gpl --out reports/identity-audit.json
    if ($LASTEXITCODE -ne 0) { throw 'Identifier audit failed.' }
    cmake -S . -B out/build -A x64
    if ($LASTEXITCODE -ne 0) { throw 'Core CMake configure failed.' }
    cmake --build out/build --config Release
    if ($LASTEXITCODE -ne 0) { throw 'Core Release build failed.' }
    ctest --test-dir out/build -C Release --output-on-failure
    if ($LASTEXITCODE -ne 0) { throw 'Core C++ tests failed.' }
    python -m unittest discover -s tests -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Importer/audit tests failed.' }
    if ($TryUpstreamBuild) {
        $solution = Join-Path $root 'third_party/tightvnc-2.8.90-gpl/tightvnc2017.sln'
        if (-not (Test-Path -LiteralPath $solution)) { throw 'Original TightVNC solution not found.' }
        if (-not (Get-Command msbuild -ErrorAction SilentlyContinue)) {
            throw 'Run from a Visual Studio 2022 Developer PowerShell or install MSBuild.'
        }
        # The original sources use v141_xp / Windows SDK 8.1; missing toolsets are
        # reported rather than silently retargeted (retargeting requires a separate review).
        & msbuild $solution /m:1 /t:Build /p:Configuration=Release '/p:Platform=x64' /v:minimal
        if ($LASTEXITCODE -ne 0) { throw 'Original TightVNC build FAILED (toolchain compatibility unresolved).' }
    }
    Write-Host 'M1 baseline checks completed. No installed remote access service was modified.'
} finally {
    Pop-Location
}
