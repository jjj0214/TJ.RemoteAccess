#Requires -Version 5.1
<#
.SYNOPSIS
  Builds/updates TJ.RemoteAccess Lab for the CURRENT USER only.
.DESCRIPTION
  Publishes into a same-volume staging folder, verifies all three (or two)
  executables, then replaces binaries with rollback on failure. Configuration
  and private certificates live outside the install directory and are retained.
  Does not install a Windows service, modify firewall or touch TightVNC.
#>
[CmdletBinding()]
param(
    [ValidateSet('Both','Server','Viewer')]
    [string]$Mode = 'Both',
    [switch]$BuildOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $root 'src\TJRemoteAccess.Core'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 9 SDK is required to build TJ.RemoteAccess Lab.'
}
$publishBase = if ($BuildOnly) {
    Join-Path $root 'out\lab-publish'
} else {
    Join-Path $env:LOCALAPPDATA 'Programs\TJ.RemoteAccess.Lab'
}
$components = if ($Mode -eq 'Both') { @('Server','Viewer','Setup') } elseif ($Mode -eq 'Server') {
    @('Server','Setup')
} else {
    @('Viewer','Setup')
}
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('TJ.RemoteAccess.Lab.Server',
                        'TJ.RemoteAccess.Lab.Viewer',
                        'TJ.RemoteAccess.Lab.Setup')
})
if ($running.Count -gt 0) {
    throw 'Close TJ.RemoteAccess Lab Server/Viewer/Setup before updating binaries.'
}
$profileDirectory = Join-Path $env:LOCALAPPDATA 'TJ.RemoteAccess.Lab'
$batch = [guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $publishBase -Force | Out-Null
$stageRoot = Join-Path $publishBase ('.stage-' + $batch)
$installed = @()
try {
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    foreach ($component in $components) {
        $project = Join-Path $source ("TJ.RemoteAccess.Lab.$component.csproj")
        if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
            throw "Missing project: $project"
        }
        $destination = Join-Path $stageRoot $component
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $destination
        if ($LASTEXITCODE -ne 0) { throw "Publish $component failed: $LASTEXITCODE" }
        $exe = Join-Path $destination ("TJ.RemoteAccess.Lab.$component.exe")
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
            throw "Published executable missing: $exe"
        }
        $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        Write-Host "PUBLISH PASS $component SHA256=$hash"
    }

    # Each staged folder is on the SAME filesystem as its final destination.
    # A failed update restores the previous complete component directories.
    foreach ($component in $components) {
        $target = Join-Path $publishBase $component
        $backup = Join-Path $publishBase ($component + '.backup-' + $batch)
        $hadPrevious = Test-Path -LiteralPath $target -PathType Container
        if ($hadPrevious) {
            Move-Item -LiteralPath $target -Destination $backup -ErrorAction Stop
        }
        $installed += [pscustomobject]@{
            Target = $target; Backup = $backup; HadPrevious = $hadPrevious
        }
        Move-Item -LiteralPath (Join-Path $stageRoot $component) -Destination $target -ErrorAction Stop
        $installedExe = Join-Path $target ("TJ.RemoteAccess.Lab.$component.exe")
        if (-not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
            throw "Post-update verification failed: $installedExe"
        }
    }
    foreach ($entry in $installed) {
        if ($entry.HadPrevious -and (Test-Path -LiteralPath $entry.Backup)) {
            Remove-Item -LiteralPath $entry.Backup -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
} catch {
    $failure = $_
    for ($i = $installed.Count - 1; $i -ge 0; $i--) {
        $entry = $installed[$i]
        if (Test-Path -LiteralPath $entry.Target) {
            Remove-Item -LiteralPath $entry.Target -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($entry.HadPrevious -and (Test-Path -LiteralPath $entry.Backup)) {
            Move-Item -LiteralPath $entry.Backup -Destination $entry.Target -ErrorAction Stop
        }
    }
    throw $failure
} finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force
    }
}
if (-not $BuildOnly) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Lab'
    try {
        New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
        $shell = New-Object -ComObject WScript.Shell
        foreach ($component in $components) {
            $exe = Join-Path (Join-Path $publishBase $component) ("TJ.RemoteAccess.Lab.$component.exe")
            $shortcut = $shell.CreateShortcut((Join-Path $startMenu ("TJ.RemoteAccess Lab $component.lnk")))
            $shortcut.TargetPath = $exe
            $shortcut.WorkingDirectory = Split-Path -Parent $exe
            $shortcut.Description = 'TJ.RemoteAccess LAB TEST ONLY'
            $shortcut.Save()
        }
    } catch {
        Write-Warning ("Binaries installed; Start Menu shortcut creation failed: " + $_.Exception.Message)
    }
    Write-Host "CURRENT-USER INSTALL/UPDATE PASS: $publishBase"
    Write-Host "Existing profiles/certificates preserved under: $profileDirectory"
    Write-Host "If the Viewer already has a paired server, do NOT run Setup init again."
    Write-Host "To add a server, use Viewer > Add server or Setup import-client."
} else {
    Write-Host "BUILD-ONLY PASS: $publishBase"
}
Write-Host 'No Windows service, firewall, registry or TightVNC change performed.'
