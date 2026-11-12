#Requires -Version 5.1
<#
  READ-ONLY preflight; does not start apps, open sockets or modify configuration.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$components = @('Server','Viewer','Setup')
$files = @()
foreach ($component in $components) {
    $project = Join-Path $root ("src\TJRemoteAccess.Core\TJ.RemoteAccess.Lab.$component.csproj")
    $exe = Join-Path $env:LOCALAPPDATA ("Programs\TJ.RemoteAccess.Lab\$component\TJ.RemoteAccess.Lab.$component.exe")
    $files += [pscustomobject]@{
        Component = $component
        ProjectExists = Test-Path -LiteralPath $project -PathType Leaf
        InstalledExeExists = Test-Path -LiteralPath $exe -PathType Leaf
    }
}
$configDir = Join-Path $env:LOCALAPPDATA 'TJ.RemoteAccess.Lab'
$server = Join-Path $configDir 'server.json'
$viewer = Join-Path $configDir 'viewer.json'
$tcp = @()
try {
    $tcp = @(Get-NetTCPConnection -LocalPort 15444 -State Listen -ErrorAction Stop |
        Select-Object LocalAddress, LocalPort, OwningProcess)
} catch { }
[pscustomobject]@{
    Probe = 'TJ.RemoteAccess Lab (READ_ONLY)'
    TimeUtc = [DateTime]::UtcNow.ToString('o')
    Components = $files
    ServerConfigExists = Test-Path -LiteralPath $server -PathType Leaf
    ViewerConfigExists = Test-Path -LiteralPath $viewer -PathType Leaf
    Tcp15444Listeners = $tcp
    ServiceInstallPerformed = $false
    FirewallChanged = $false
    Note = 'Compile/config/port metadata only. Actual GUI and input require an isolated-PC test.'
} | ConvertTo-Json -Depth 6
