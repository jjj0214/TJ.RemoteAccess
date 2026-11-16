#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the unsigned TJ.RemoteAccess PREVIEW Windows GUI installer bundle.
.DESCRIPTION
  Release engineer/build runner only. End users NEVER run PowerShell.
  Output is a ZIP containing a self-contained Installer.exe, the verified
  offline application ZIP, its SHA-256 sidecar, and GPL license/source.
  Double-clicking Installer.exe displays install/update/remove UI.
  This is NOT an MSI, Windows Service installer, or public release.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9.+-]{1,40}$')]
    [string]$Version = '0.5.3-preview',
    [switch]$UseExistingOfflinePackage
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$changes = @(& git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or @($changes | Where-Object { $_ }).Count -ne 0) {
    throw 'Commit all tested sources first. The installer must match a clean Git HEAD.'
}
$packageDir = Join-Path $root 'out\packages'
$offline = Join-Path $packageDir "TJ.RemoteAccess-$Version-win-x64.zip"
$offlineSha = $offline + '.sha256'
$bundle = Join-Path $packageDir "TJ.RemoteAccess-$Version-GUI-Installer-win-x64.zip"
if ((Test-Path -LiteralPath $bundle) -or (Test-Path -LiteralPath ($bundle + '.sha256'))) {
    throw "Refusing to overwrite an existing installer build: $bundle"
}
Write-Host '[1/5] Checking or building SHA-256 manifest offline application package...'
if (-not $UseExistingOfflinePackage) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Build-TJRemoteAccess-Preview-OfflinePackage.ps1') -Version $Version
    if ($LASTEXITCODE -ne 0) { throw 'Offline application package build failed.' }
}
if (-not (Test-Path -LiteralPath $offline -PathType Leaf) -or
    -not (Test-Path -LiteralPath $offlineSha -PathType Leaf)) {
    throw 'Offline ZIP or its SHA-256 sidecar is missing.'
}
$shaLine = ((Get-Content -LiteralPath $offlineSha -TotalCount 1).Trim() -split '\s+')
if ($shaLine.Count -ne 2 -or $shaLine[0] -notmatch '^[0-9A-Fa-f]{64}$' -or
    $shaLine[1] -cne (Split-Path -Leaf $offline)) {
    throw 'Offline SHA-256 sidecar is malformed.'
}
Write-Host '[2/5] Verifying offline ZIP SHA-256 (may be slow for large source bundle)...'
$actual = (Get-FileHash -LiteralPath $offline -Algorithm SHA256).Hash
if (-not [string]::Equals($actual, $shaLine[0], [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Offline package digest mismatch. Build aborted.'
}
Write-Host '[3/5] Publishing standalone self-contained graphical Installer.exe...'
$project = Join-Path $root 'src\TJRemoteAccess.Core\TJ.RemoteAccess.Installer.csproj'
$out = Join-Path $root 'out\installer-publish'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'GUI Installer publish failed.' }
$exe = Join-Path $out 'TJ.RemoteAccess.Installer.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Self-contained Installer.exe is missing.' }
Write-Host '[4/5] Packaging GUI installer, offline ZIP, digest and license...'
$stage = Join-Path $packageDir ('.gui-installer-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($source in @($exe, $offline, $offlineSha, (Join-Path $root 'LICENSE'))) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $stage (Split-Path -Leaf $source)) -ErrorAction Stop
    }
    $instructions = @'
TJ.RemoteAccess 0.5.3-preview - Windows GUI Installer
=====================================================
1. Extract THIS OUTER ZIP into a normal folder (do not run from inside ZIP).
2. Double-click TJ.RemoteAccess.Installer.exe.
3. Choose Both, Server, or Viewer and select Install / Update.
4. The bundled offline ZIP and .sha256 sidecar are found automatically.
5. The UI shows verification/extraction progress and records a local log.
6. Close existing TJ.RemoteAccess Server (including tray Exit), Viewer and Setup.
7. Existing passwords/certificates/profiles and TightVNC settings are preserved.

NOTE: Unsigned test preview, NOT an MSI or a production release. Windows
SmartScreen/unknown-publisher warnings may appear. The adjacent .sha256 file
checks corruption but is NOT independent authentication: compare the hash
against a trusted separately distributed hash before installing.

End-user installation does NOT run PowerShell or require .NET SDK.
Windows 10/11 x64; Windows Service/UAC/Winlogon control is not installed.
'@
    [System.IO.File]::WriteAllText((Join-Path $stage 'README_INSTALL.txt'),
        $instructions, [System.Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $bundle -ErrorAction Stop
    if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) { throw 'GUI bundle was not created.' }
    Write-Host '[5/5] Computing installer ZIP SHA-256...'
    $finalSha = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(($bundle + '.sha256'),
        "$finalSha  $(Split-Path -Leaf $bundle)" + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "GUI INSTALLER BUNDLE BUILT: $bundle"
    Write-Host "SHA-256: $finalSha"
    Write-Warning 'Unsigned TEST Preview. Extract downloaded ZIP before running Installer.exe.'
} catch {
    if (Test-Path -LiteralPath $bundle) {
        Remove-Item -LiteralPath $bundle -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath ($bundle + '.sha256')) {
        Remove-Item -LiteralPath ($bundle + '.sha256') -Force -ErrorAction SilentlyContinue
    }
    throw
} finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
