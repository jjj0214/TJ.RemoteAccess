# SPDX-License-Identifier: GPL-2.0-or-later
# Local unprivileged tests only; no Windows service, network or VNC launching.
[CmdletBinding()]
param([switch] $SkipPythonTests)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$exeDir = Join-Path $root 'out\msbuild\bin\Release'
$tests = @(
    'TJRemoteAccess.Core.Tests.exe',
    'TJRemoteAccess.EngineGate.Tests.exe'
)
Push-Location $root
try {
    foreach ($name in $tests) {
        $path = Join-Path $exeDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Missing $path. Build tests\TJRA.M2.Tests.sln Release x64 first."
        }
        Write-Host "Running: $name"
        & $path
        if ($LASTEXITCODE -ne 0) {
            throw "$name FAILED (exit code $LASTEXITCODE)"
        }
    }
    if (-not $SkipPythonTests) {
        $python = Get-Command python -ErrorAction SilentlyContinue
        if (-not $python) { throw 'Python 3.10+ required for offline source-tool tests.' }
        python -m unittest discover -s (Join-Path $root 'tests') -p 'test_*.py' -v
        if ($LASTEXITCODE -ne 0) { throw 'Python offline tests FAILED' }
    }
    Write-Host 'M2 local core tests PASS. No VNC Server/Viewer was launched.'
} finally {
    Pop-Location
}
