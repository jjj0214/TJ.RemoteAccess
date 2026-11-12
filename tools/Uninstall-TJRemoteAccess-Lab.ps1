#Requires -Version 5.1
<#
  Removes only CURRENT-USER Lab binaries and shortcuts. Does not remove
  certificate-store credentials or pairing material without separate review.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('TJ.RemoteAccess.Lab.Server','TJ.RemoteAccess.Lab.Viewer','TJ.RemoteAccess.Lab.Setup')
}
if ($running) { throw 'Close all TJ.RemoteAccess Lab processes before uninstalling.' }
$install = Join-Path $env:LOCALAPPDATA 'Programs\TJ.RemoteAccess.Lab'
$taskName = 'TJ.RemoteAccess.Lab.CurrentUser.Server'
$serverExe = Join-Path $install 'Server\TJ.RemoteAccess.Lab.Server.exe'
if (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue) {
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($task) {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $command = @($task.Actions)
        if ($command.Count -ne 1 -or
            -not [string]::Equals([string]$command[0].Execute, $serverExe,
                [System.StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([string]$task.Principal.UserId, $identity,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'A task with the TJRA name belongs to another program/account. Refusing removal.'
        }
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
        Write-Host 'TJRA current-user logon task safely removed.'
    }
}

$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Lab'
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
if (Test-Path -LiteralPath $menu) { Remove-Item -LiteralPath $menu -Recurse -Force }
Write-Host 'TJ.RemoteAccess Lab binaries and shortcuts removed for current user.'
Write-Host 'Certificate store and local pairing/config files remain for manual cleanup.'
Write-Host 'TightVNC, Windows services and firewall are unchanged.'
