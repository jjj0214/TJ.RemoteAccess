#Requires -Version 5.1
<#
.SYNOPSIS
  Opt-in startup of the CURRENT USER's visible Lab Server after Windows logon.
.DESCRIPTION
  Registers a per-user, least-privilege logon task. Explicit Enable only.
  Does NOT install a Windows service, run as SYSTEM, alter UAC, unlock the PC,
  change firewall rules, or enable remote access before login.
#>
[CmdletBinding()]
param(
    [ValidateSet('Status', 'Enable', 'Disable')]
    [string]$Action = 'Status',
    [switch]$AllowLan
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskName = 'TJ.RemoteAccess.Lab.CurrentUser.Server'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\TJ.RemoteAccess.Lab'
$serverExe = Join-Path $installRoot 'Server\TJ.RemoteAccess.Lab.Server.exe'
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
if ([string]::IsNullOrWhiteSpace($identity)) {
    throw 'Current interactive user identity is unavailable.'
}
if (-not (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue)) {
    throw 'Windows ScheduledTasks PowerShell module is required.'
}
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($existing) {
    $commands = @($existing.Actions | ForEach-Object { $_.Execute })
    if ($commands.Count -ne 1 -or
        -not [string]::Equals($commands[0].Trim('"'), $serverExe,
                               [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Existing Task $taskName is not the expected TJRA Lab Server. No change made."
    }
    $principals = @($existing.Principal.UserId)
    if ($principals.Count -ne 1 -or
        -not [string]::Equals($principals[0], $identity,
                               [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Existing task belongs to a different account. No change made.'
    }
}
if ($Action -eq 'Status') {
    [pscustomobject]@{
        Task = $taskName
        Installed = [bool]$existing
        User = $identity
        ServerBinaryPresent = Test-Path -LiteralPath $serverExe -PathType Leaf
        State = if ($existing) { [string]$existing.State } else { 'NotInstalled' }
        TaskArguments = if ($existing) { @($existing.Actions)[0].Arguments } else { $null }
        Note = 'Starts only after interactive user logon; no Windows Service or lock-screen support.'
    } | Format-List
    return
}
if ($Action -eq 'Disable') {
    if ($existing) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Host 'TJRA Lab current-user logon startup removed.'
    } else {
        Write-Host 'No TJRA Lab current-user logon startup task exists.'
    }
    Write-Host 'Existing interactive Server process was not forcibly stopped.'
    return
}
if (-not (Test-Path -LiteralPath $serverExe -PathType Leaf)) {
    throw "Install the Server first: $serverExe"
}
$configPath = Join-Path $env:LOCALAPPDATA 'TJ.RemoteAccess.Lab\server.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) {
    throw 'Server has not been paired. Run Setup init manually before enabling auto-start.'
}
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$serverIP = $null
if (-not [System.Net.IPAddress]::TryParse([string]$config.ListenAddress, [ref]$serverIP)) {
    throw 'Invalid Server ListenAddress. Refusing startup registration.'
}
if (-not [System.Net.IPAddress]::IsLoopback($serverIP) -and -not $AllowLan) {
    throw 'LAN Server requires explicit -AllowLan. Verify your firewall/VPN restrictions.'
}
$arguments = if ($AllowLan) { '--allow-lan --auto-start' } else { '--auto-start' }
$taskAction = New-ScheduledTaskAction -Execute $serverExe -Argument $arguments
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
$register = @{
    TaskName = $taskName
    Action = $taskAction
    Trigger = $trigger
    Principal = $principal
    Settings = $settings
    Description = 'TJ.RemoteAccess Lab: user-visible Server after explicit user logon (not a system service)'
    Force = $true
    ErrorAction = 'Stop'
}
Register-ScheduledTask @register | Out-Null
Write-Host "Current-user visible TJRA Lab Server startup ENABLED for $identity."
Write-Host "Launch arguments: $arguments"
Write-Host 'Task starts on NEXT logon, not on secure desktop or before login.'
Write-Host 'The Server intentionally stops on session lock/logoff/disconnect; it does not auto-restart on unlock.'
Write-Host 'To undo: rerun this script with -Action Disable.'
