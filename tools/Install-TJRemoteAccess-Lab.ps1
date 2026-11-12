#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and installs TJ.RemoteAccess Lab CURRENT USER ONLY.
.DESCRIPTION
  Does not install a Windows service, modify firewall, or change TightVNC.
  Requires .NET 9 SDK and an interactive logged-in Windows desktop.
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
    throw 'dotnet SDK was not found. Install the .NET 9 SDK before using this script.'
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
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('TJ.RemoteAccess.Lab.Server','TJ.RemoteAccess.Lab.Viewer','TJ.RemoteAccess.Lab.Setup')
}
if ($running) {
    throw 'Close TJ.RemoteAccess Lab processes before replacing binaries.'
}
New-Item -ItemType Directory -Path $publishBase -Force | Out-Null
foreach ($component in $components) {
    $project = Join-Path $source ("TJ.RemoteAccess.Lab.$component.csproj")
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing project: $project" }
    $destination = Join-Path $publishBase $component
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $destination
    if ($LASTEXITCODE -ne 0) { throw "Publish $component failed: $LASTEXITCODE" }
    $exe = Join-Path $destination ("TJ.RemoteAccess.Lab.$component.exe")
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Missing published executable: $exe"
    }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    Write-Host "PUBLISH PASS $component SHA256=$hash"
}
if (-not $BuildOnly) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Lab'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    foreach ($component in $components) {
        $exe = Join-Path (Join-Path $publishBase $component) ("TJ.RemoteAccess.Lab.$component.exe")
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu ("TJ.RemoteAccess Lab $component.lnk")))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = Split-Path -Parent $exe
        $shortcut.Description = 'TJ.RemoteAccess TEST ONLY (not a production service)'
        $shortcut.Save()
    }
    Write-Host "CURRENT-USER INSTALL PASS: $publishBase"
    Write-Host "Run the installed Setup.exe with argument: init"
    Write-Host "Then launch Lab Server and Lab Viewer on localhost."
} else {
    Write-Host "BUILD-ONLY PASS: $publishBase"
}
Write-Host 'No Windows service, firewall, registry or TightVNC change performed.'
