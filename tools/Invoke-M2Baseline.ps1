# SPDX-License-Identifier: GPL-2.0-or-later
# Developer-only baseline. Never installs, starts, stops or removes a VNC service.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $TightVncSourceZip,
    [switch] $ReuseImportedSource,
    [switch] $SkipOriginalBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expected = 'F3F76393A5865E1F1919C78C3EB43B9C937CF738927C2BFC920ECBD3913B061C'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$archivePath = (Resolve-Path -LiteralPath $TightVncSourceZip -ErrorAction Stop).Path
$actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToUpperInvariant()
if ($actual -cne $expected) { throw "Refusing unverified source ZIP: $actual" }
$source = Join-Path $root 'third_party\tightvnc-2.8.90-gpl'
$solution = Join-Path $source 'tightvnc2017.sln'
$evidence = Join-Path $root 'out\m2-evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null

function Invoke-Checked {
    param([string] $Name, [scriptblock] $Action)
    Write-Host "M2 BASELINE: $Name"
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "$Name failed (exit code $LASTEXITCODE)" }
}

Push-Location $root
try {
    if (-not (Get-Command python -ErrorAction SilentlyContinue)) { throw 'Python 3.10+ missing.' }
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw 'CMake 3.18+ missing.' }
    if (Test-Path -LiteralPath $source) {
        if (-not $ReuseImportedSource) {
            throw 'Source tree exists. Review before rerun with -ReuseImportedSource; no automatic overwrites.'
        }
        Write-Host 'Existing source selected: verifying every extracted file against the pinned ZIP.'
    } else {
        Invoke-Checked 'verified GPL source import' {
            python (Join-Path $root 'tools\vendor_upstream.py') --zip $archivePath
        }
    }
    if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
        throw "Original VS solution missing: $solution"
    }
    Invoke-Checked 'upstream namespace audit' {
        python (Join-Path $root 'tools\audit_identifiers.py') --source $source --out (Join-Path $evidence 'upstream-identity-audit.json')
    }
    $buildDir = Join-Path $root 'out\m2-core-build'
    Invoke-Checked 'portable x64 CMake configure' { cmake -S $root -B $buildDir -A x64 }
    Invoke-Checked 'portable x64 Release build' { cmake --build $buildDir --config Release --parallel 2 }
    Invoke-Checked 'portable C++ tests' { ctest --test-dir $buildDir -C Release --output-on-failure }
    Invoke-Checked 'offline importer tests' {
        python -m unittest discover -s (Join-Path $root 'tests') -p 'test_*.py' -v
    }
    if (-not $SkipOriginalBuild) {
        $command = Get-Command msbuild -ErrorAction SilentlyContinue
        $msbuild = if ($null -ne $command) { $command.Source } else { $null }
        if (-not $msbuild) {
            $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
            if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
                throw 'VS2022 MSBuild missing. Install C++ Build Tools.'
            }
            $matches = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
            $msbuild = $matches | Where-Object { $_ -match '\\MSBuild.exe$' } | Select-Object -First 1
        }
        if (-not $msbuild) { throw 'MSBuild.exe not found.' }
        # Preserve upstream v141_xp/SDK 8.1 references. Retargeting is separate.
        Invoke-Checked 'unmodified upstream x64 Release solution' {
            & $msbuild $solution '/m:1' '/t:Build' '/p:Configuration=Release' '/p:Platform=x64' '/v:minimal'
        }
    }
    [pscustomobject]@{
        Check = 'M2 upstream baseline'
        SourceArchiveSha256 = $actual
        OriginalBuild = $(if ($SkipOriginalBuild) { 'NOT_TESTED' } else { 'PASS' })
        ServicesOrFirewallChanged = $false
        CompletedUtc = [DateTime]::UtcNow.ToString('o')
    } | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $evidence 'baseline.json') -Encoding UTF8
    Write-Host 'M2 baseline completed. No service, firewall or registry changed.'
} finally {
    Pop-Location
}
