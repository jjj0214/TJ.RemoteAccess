#Requires -Version 5.1
<#
  READ ONLY: reports local installation, saved server profile count and listener.
  Does not read private keys, launch apps, create sockets or mutate configuration.
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
$serverFile = Join-Path $configDir 'server.json'
$legacyFile = Join-Path $configDir 'viewer.json'
$profileFile = Join-Path $configDir 'viewer-profiles.json'
$profileCount = 0
$selected = ''
$profileStatus = 'No saved profile catalog'
if (Test-Path -LiteralPath $profileFile -PathType Leaf) {
    try {
        $catalog = Get-Content -LiteralPath $profileFile -Raw | ConvertFrom-Json
        $profileCount = @($catalog.Profiles).Count
        $matching = @($catalog.Profiles | Where-Object { $_.Id -eq $catalog.SelectedProfileId })
        if ($matching.Count -eq 1) { $selected = $matching[0].Name }
        $profileStatus = 'Loaded'
    } catch {
        $profileStatus = 'INVALID: ' + $_.Exception.Message
    }
} elseif (Test-Path -LiteralPath $legacyFile -PathType Leaf) {
    $profileStatus = 'Legacy viewer.json detected; Viewer will migrate automatically'
}
$listeners = @()
try {
    $listeners = @(Get-NetTCPConnection -LocalPort 15444 -State Listen -ErrorAction Stop |
        Select-Object LocalAddress, LocalPort, OwningProcess)
} catch { }
[pscustomobject]@{
    Probe = 'TJ.RemoteAccess Lab (READ_ONLY)'
    TimeUtc = [DateTime]::UtcNow.ToString('o')
    Components = $files
    ServerConfigExists = Test-Path -LiteralPath $serverFile -PathType Leaf
    LegacyViewerConfigExists = Test-Path -LiteralPath $legacyFile -PathType Leaf
    ProfileCatalogExists = Test-Path -LiteralPath $profileFile -PathType Leaf
    ProfileStatus = $profileStatus
    SavedServerCount = $profileCount
    SelectedServerName = $selected
    Tcp15444Listeners = $listeners
    ServiceInstallPerformed = $false
    FirewallChanged = $false
    Note = 'Metadata only. GUI and input require isolated Windows acceptance.'
} | ConvertTo-Json -Depth 6
