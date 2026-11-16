#Requires -Version 5.1
<#
.SYNOPSIS
  Remove ONLY the isolated TJRA.ControlService supervised Service Preview installation.
.DESCRIPTION
  Explicit administrator opt-in required. Never removes or changes TightVNC,
  user TLS keys, Lab Server/Viewer profiles, firewall rules, or RFB listeners.
#>
[CmdletBinding()]
param([switch]$ApproveServicePreviewRemoval)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ApproveServicePreviewRemoval) {
    throw 'Use -ApproveServicePreviewRemoval only on a disposable test VM.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator elevation required.'
}
$name = 'TJRA.ControlService'
$installDir = Join-Path (Join-Path $env:ProgramFiles 'TJ.RemoteAccess') 'ServicePreview'
$expectedExe = Join-Path $installDir 'TJ.RemoteAccess.M3.ControlService.exe'
# Check directory ownership/manifest BEFORE changing SCM state.
if (Test-Path -LiteralPath $installDir -PathType Container) {
    $file = Join-Path $installDir 'manifest.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw 'Service Preview manifest absent. Directory preserved for manual review.'
    }
    $manifest = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($manifest.Artifact -ne 'TJ.RemoteAccess.Service.Preview' -or
        $manifest.RemoteAccessEnabled -ne $false) {
        throw 'Service Preview manifest mismatch. Refusing directory removal.'
    }
}
$registered = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
if ($null -ne $registered) {
    $currentImage = ([string]$registered.PathName).Trim().Trim('"')
    if ($currentImage -ine $expectedExe -or
        [string]$registered.StartName -ne 'LocalSystem') {
        throw 'Refusing to remove a service not owned by Service Preview.'
    }
    $service = Get-Service -Name $name -ErrorAction Stop
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $name -ErrorAction Stop
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
    }
    & sc.exe delete $name
    if ($LASTEXITCODE -ne 0) {
        throw 'SCM delete failed. Service Preview files were preserved.'
    }
}
if (Test-Path -LiteralPath $installDir -PathType Container) {
    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction Stop
}
Write-Host 'SERVICE PREVIEW REMOVAL PASS.'
Write-Host 'Other Windows services, TightVNC, TLS profiles and firewall were untouched.'