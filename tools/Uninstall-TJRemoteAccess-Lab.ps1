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
$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Lab'
if (Test-Path -LiteralPath $install) { Remove-Item -LiteralPath $install -Recurse -Force }
if (Test-Path -LiteralPath $menu) { Remove-Item -LiteralPath $menu -Recurse -Force }
Write-Host 'TJ.RemoteAccess Lab binaries and shortcuts removed for current user.'
Write-Host 'Certificate store and local pairing/config files remain for manual cleanup.'
Write-Host 'TightVNC, Windows services and firewall are unchanged.'
