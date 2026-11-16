#Requires -Version 5.1
<#
.SYNOPSIS
  Read-only ownership, supervision and coexistence probe for Service Preview.
.DESCRIPTION
  Does not start or stop processes, read private certificate keys, or modify
  services, registry, user profiles, or firewall rules.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$name = 'TJRA.ControlService'
$dir = Join-Path (Join-Path $env:ProgramFiles 'TJ.RemoteAccess') 'ServicePreview'
$exeNames = @(
    'TJ.RemoteAccess.M3.ControlService.exe', 'TJ.RemoteAccess.M3.Agent.exe',
    'TJ.RemoteAccess.Server.exe', 'TJ.RemoteAccess.Viewer.exe', 'TJ.RemoteAccess.Setup.exe'
)
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
        $manifestStatus = if ($manifest.Artifact -eq 'TJ.RemoteAccess.Service.Preview' -and
                             $manifest.RemoteAccessEnabled -eq $false) {
            'Preview'
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
        if ($runtime.RemoteAccessEnabled -eq $false -and
            $runtime.Component -eq 'TJRA.ControlService') {
            $runtimeStatus = 'Preview'
        } else {
            $runtimeStatus = 'INVALID'
            $runtime = $null
        }
    } catch {
        $runtimeStatus = 'INVALID'
        $runtime = $null
    }
}
$ownerPolicyPath = Join-Path $dir 'desktop-host-policy.json'
$operatorPolicyStatus = 'Disabled'
if (Test-Path -LiteralPath $ownerPolicyPath -PathType Leaf) {
    try {
        $operatorPolicy = Get-Content -LiteralPath $ownerPolicyPath -Raw | ConvertFrom-Json
        $operatorPolicyStatus = if ($operatorPolicy.SchemaVersion -eq 1 -and
            $operatorPolicy.EnableSupervisedDesktop -eq $true -and
            $operatorPolicy.Mode -eq 'InteractiveOwnerConsentOnly') {
            'Enabled (local user enrollment also required)'
        } else { 'INVALID' }
    } catch { $operatorPolicyStatus = 'INVALID' }
}
$hostListeners = @()
try {
    $hostListeners = @(Get-NetTCPConnection -LocalPort 15544 -State Listen -ErrorAction Stop |
        Select-Object LocalAddress, LocalPort, OwningProcess)
} catch { }
$tightVnc = @(Get-CimInstance Win32_Service -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match 'tightvnc|tvnserver' -or
                   $_.DisplayName -match 'tightvnc' } |
    Select-Object Name, State, StartMode)
[pscustomobject]@{
    Probe = 'TJ.RemoteAccess.Service.Preview (READ ONLY)'
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
    OperatorDesktopPolicy = $operatorPolicyStatus
    Tcp15544Listeners = $hostListeners
    AgentProcesses = $processes
    TightVncServices = $tightVnc
    RemoteAccessEnabled = $false
    Note = 'Visible desktop only after TWO opt-ins; Winlogon/UAC never authorized. Listener presence does not imply TLS acceptance.'
} | ConvertTo-Json -Depth 6