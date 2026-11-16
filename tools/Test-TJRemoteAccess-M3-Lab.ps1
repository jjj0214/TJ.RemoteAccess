#Requires -Version 5.1
<#
.SYNOPSIS
  Read-only status, ownership and coexistence probe for M3 status-only Lab.
.DESCRIPTION
  Does not start or stop processes, read private certificate keys, or modify
  services, registry, user profiles, or firewall rules.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$name = 'TJRA.ControlService'
$dir = Join-Path (Join-Path $env:ProgramFiles 'TJ.RemoteAccess') 'M3Lab'
$exeNames = @('TJ.RemoteAccess.M3.ControlService.exe', 'TJ.RemoteAccess.M3.Agent.exe')
$files = @()
foreach ($exeName in $exeNames) {
    $path = Join-Path $dir $exeName
    $found = Test-Path -LiteralPath $path -PathType Leaf
    $files += [pscustomobject]@{
        Name = $exeName
        Present = $found
        Sha256 = if ($found) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { $null }
    }
}
$record = Get-CimInstance Win32_Service -Filter "Name='$name'" -ErrorAction SilentlyContinue
$processes = @(Get-Process -Name 'TJ.RemoteAccess.M3.Agent' -ErrorAction SilentlyContinue |
    Select-Object Id, SessionId, ProcessName)
$manifestPath = Join-Path $dir 'manifest.json'
$manifestStatus = 'NotInstalled'
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $manifestStatus = if ($manifest.Artifact -eq 'TJ.RemoteAccess.M3.StatusOnly.Lab' -and
                             $manifest.RemoteAccessEnabled -eq $false) {
            'StatusOnly'
        } else { 'INVALID' }
    }
    catch { $manifestStatus = 'INVALID' }
}
$statusFile = Join-Path $dir 'service-status.json'
$runtime = $null
$runtimeStatus = 'NotAvailable'
if (Test-Path -LiteralPath $statusFile -PathType Leaf) {
    try {
        $runtime = Get-Content -LiteralPath $statusFile -Raw | ConvertFrom-Json
        if ($runtime.StatusOnly -eq $true -and $runtime.RemoteAccessEnabled -eq $false -and
            $runtime.Component -eq 'TJRA.ControlService') {
            $runtimeStatus = 'StatusOnly'
        } else {
            $runtimeStatus = 'INVALID'
            $runtime = $null
        }
    } catch {
        $runtimeStatus = 'INVALID'
        $runtime = $null
    }
}
$tightVnc = @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'tightvnc|tvnserver' -or
                   $_.DisplayName -match 'tightvnc' } |
    Select-Object Name, State, StartMode)
[pscustomobject]@{
    Probe = 'TJ.RemoteAccess.M3.StatusOnly (READ ONLY)'
    TimeUtc = [DateTime]::UtcNow.ToString('o')
    Service = $name
    ServicePresent = ($null -ne $record)
    ServiceState = if ($null -ne $record) { $record.State } else { 'NotInstalled' }
    StartMode = if ($null -ne $record) { $record.StartMode } else { $null }
    LocalSystemIdentity = if ($null -ne $record) { $record.StartName -eq 'LocalSystem' } else { $null }
    ExecutablePath = if ($null -ne $record) { $record.PathName } else { $null }
    Files = $files
    ManifestStatus = $manifestStatus
    RuntimeStatus = $runtimeStatus
    LastServiceEvent = $runtime
    AgentProcesses = $processes
    TightVncServices = $tightVnc
    RemoteAccessEnabled = $false
    Note = 'STATUS ONLY: no desktop capture, input, TLS listener, UAC or Winlogon control. This is NOT an M3 runtime acceptance.'
} | ConvertTo-Json -Depth 6
