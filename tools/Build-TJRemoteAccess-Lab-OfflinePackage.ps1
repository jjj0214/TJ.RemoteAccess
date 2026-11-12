#Requires -Version 5.1
<#
.SYNOPSIS
  Build a SHA-256-manifested, unsigned, offline, self-contained win-x64 Lab ZIP.
.DESCRIPTION
  Release engineering input only. No public release, signing, service installation,
  firewall change, registry change or TightVNC modification.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9.+-]{1,40}$')]
    [string]$Version = '0.3.0-dev',
    [switch]$SkipBuild
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$changes = @(& git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or @($changes | Where-Object { $_ }).Count -ne 0) {
    throw 'Source tree is dirty or Git status failed; commit verified source before packaging.'
}
# Source archive must match this exact committed revision.
$publish = Join-Path $root 'out\lab-publish'
$packageDir = Join-Path $root 'out\packages'
$components = @('Server','Viewer','Setup')
if (-not $SkipBuild) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (
        Join-Path $PSScriptRoot 'Install-TJRemoteAccess-Lab.ps1'
    ) -Mode Both -BuildOnly
    if ($LASTEXITCODE -ne 0) {
        throw "Lab self-contained publish failed: $LASTEXITCODE"
    }
}
foreach ($component in $components) {
    $exe = Join-Path (Join-Path $publish $component) ("TJ.RemoteAccess.Lab.$component.exe")
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Build-only output missing: $exe"
    }
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
$zip = Join-Path $packageDir "TJ.RemoteAccess.Lab-$Version-win-x64.zip"
if (Test-Path -LiteralPath $zip) {
    throw "An artifact with this version already exists. No overwrite: $zip"
}
$batch = [guid]::NewGuid().ToString('N')
$stage = Join-Path $packageDir ('.package-stage-' + $batch)
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($component in $components) {
        Copy-Item -LiteralPath (Join-Path $publish $component) -Destination (
            Join-Path $stage $component
        ) -Recurse -ErrorAction Stop
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (
        Join-Path $stage 'LICENSE'
    )
    foreach ($script in @('Install-TJRemoteAccess-Lab-Offline.ps1',
                          'Uninstall-TJRemoteAccess-Lab.ps1',
                          'Test-TJRemoteAccess-Lab.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (
            Join-Path $stage $script
        ) -ErrorAction Stop
    }
    $commit = (& git -C $root rev-parse HEAD)
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Unable to establish exact local source revision.'
    }
    $sourceArchive = Join-Path $stage 'source.zip'
    & git -C $root archive --format=zip "--output=$sourceArchive" HEAD
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $sourceArchive -PathType Leaf)) {
        throw 'Unable to include corresponding GPL source snapshot.'
    }
    $fileRows = @(
        Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName |
            ForEach-Object {
                $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
                [pscustomobject]@{
                    Path = $relative
                    Bytes = [long]$_.Length
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            }
    )
    $manifest = [pscustomobject]@{
        SchemaVersion = 1
        Artifact = 'TJ.RemoteAccess.Lab.Offline'
        Version = $Version
        SourceRevision = $commit.ToLowerInvariant()
        RuntimeIdentifier = 'win-x64'
        Unsigned = $true
        RequiresUserCertificatePairing = $true
        Components = $components
        Files = $fileRows
    }
    $manifestText = ConvertTo-Json -InputObject $manifest -Depth 6
    [System.IO.File]::WriteAllText((Join-Path $stage 'manifest.json'),
        $manifestText + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
        if ('manifest.json' -notin $entryNames) {
            throw 'Archive lacks root manifest.json.'
        }
        foreach ($row in $fileRows) {
            if ($row.Path -notin $entryNames) {
                throw "Archive missing manifested file: $($row.Path)"
            }
        }
    } finally { $archive.Dispose() }
    $zipHash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $digestPath = $zip + '.sha256'
    [System.IO.File]::WriteAllText($digestPath,
        "$zipHash  $(Split-Path -Leaf $zip)" + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "OFFLINE PACKAGE BUILT: $zip"
    Write-Host "SHA-256: $zipHash"
    Write-Warning 'Unsigned LAB artifact: SHA-256 detects modification only when checked against a trusted source. Not a production/customer release.'
} catch {
    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath ($zip + '.sha256')) {
        Remove-Item -LiteralPath ($zip + '.sha256') -Force -ErrorAction SilentlyContinue
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
