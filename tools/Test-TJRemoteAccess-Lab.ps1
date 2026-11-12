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
$certificates = @{}
$certificateStoreError = ''
try {
    foreach ($certificate in @(Get-ChildItem -Path 'Cert:\CurrentUser\My' -ErrorAction Stop)) {
        $certificates[$certificate.Thumbprint.ToUpperInvariant()] = $certificate
    }
} catch {
    $certificateStoreError = $_.Exception.Message
}
function Get-TJRALabCertificateState {
    param([string]$Thumbprint)
    if ([string]::IsNullOrWhiteSpace($Thumbprint)) {
        return [pscustomobject]@{ State = 'NotConfigured'; DaysRemaining = $null; ValidUntilUtc = $null }
    }
    if ($certificateStoreError) {
        return [pscustomobject]@{ State = 'ProbeUnavailable'; DaysRemaining = $null; ValidUntilUtc = $null }
    }
    $candidate = $certificates[$Thumbprint.Trim().ToUpperInvariant()]
    if ($null -eq $candidate) {
        return [pscustomobject]@{ State = 'Missing'; DaysRemaining = $null; ValidUntilUtc = $null }
    }
    $validUntil = $candidate.NotAfter.ToUniversalTime()
    $days = [math]::Floor(($validUntil - [DateTime]::UtcNow).TotalDays)
    $state = if (-not $candidate.HasPrivateKey) { 'NoPrivateKey' } elseif ($days -lt 0) {
        'Expired'
    } elseif ($days -le 7) { 'ExpiringSoon' } else { 'Valid' }
    return [pscustomobject]@{
        State = $state
        DaysRemaining = [int]$days
        ValidUntilUtc = $validUntil.ToString('o')
    }
}
$serverCertificateState = $null
if (Test-Path -LiteralPath $serverFile -PathType Leaf) {
    try {
        $settings = Get-Content -LiteralPath $serverFile -Raw | ConvertFrom-Json
        $serverCertificateState = Get-TJRALabCertificateState -Thumbprint $settings.ServerCertificateThumbprint
    } catch {
        $serverCertificateState = [pscustomobject]@{
            State = 'InvalidServerConfig'; DaysRemaining = $null; ValidUntilUtc = $null
        }
    }
}
$viewerCertificateStates = @()
$profilesForProbe = @()
$profileCount = 0
$selected = ''
$profileStatus = 'No saved profile catalog'
if (Test-Path -LiteralPath $profileFile -PathType Leaf) {
    try {
        $catalog = Get-Content -LiteralPath $profileFile -Raw | ConvertFrom-Json
        $profileCount = @($catalog.Profiles).Count
        $profilesForProbe = @($catalog.Profiles)
        $matching = @($catalog.Profiles | Where-Object { $_.Id -eq $catalog.SelectedProfileId })
        if ($matching.Count -eq 1) { $selected = $matching[0].Name }
        $profileStatus = 'Loaded'
    } catch {
        $profileStatus = 'INVALID: ' + $_.Exception.Message
    }
} elseif (Test-Path -LiteralPath $legacyFile -PathType Leaf) {
    $profileStatus = 'Legacy viewer.json detected; Viewer will migrate automatically'
}
foreach ($profile in $profilesForProbe) {
    $state = Get-TJRALabCertificateState -Thumbprint $profile.ClientCertificateThumbprint
    $viewerCertificateStates += [pscustomobject]@{
        ProfileName = [string]$profile.Name
        CertificateState = $state.State
        DaysRemaining = $state.DaysRemaining
        ValidUntilUtc = $state.ValidUntilUtc
    }
}
$attentionCount = @($viewerCertificateStates | Where-Object {
    $_.CertificateState -ne 'Valid'
}).Count
if ($serverCertificateState -and $serverCertificateState.State -ne 'Valid') {
    $attentionCount++
}
$clientRosterFile = Join-Path $configDir 'server-clients.json'
$clientRosterStatus = if (Test-Path -LiteralPath $serverFile -PathType Leaf) {
    'Legacy single-pin configuration (not migrated yet)'
} else { 'No Server configuration' }
$allowedClients = 0
$disabledClients = 0
$viewOnlyClients = 0
if (Test-Path -LiteralPath $clientRosterFile -PathType Leaf) {
    try {
        $roster = Get-Content -LiteralPath $clientRosterFile -Raw | ConvertFrom-Json
        if ($roster.SchemaVersion -ne 1 -or $null -eq $roster.Clients) {
            throw 'Unsupported client roster schema'
        }
        $allowedClients = @($roster.Clients | Where-Object { $_.Enabled }).Count
        $disabledClients = @($roster.Clients | Where-Object { -not $_.Enabled }).Count
        $viewOnlyClients = @($roster.Clients | Where-Object {
            $_.Enabled -and -not $_.CanControl
        }).Count
        $clientRosterStatus = 'Loaded'
    } catch {
        $clientRosterStatus = 'INVALID: ' + $_.Exception.Message
    }
} elseif (Test-Path -LiteralPath (Join-Path $configDir 'server-clients.migrated')) {
    $clientRosterStatus = 'MISSING AFTER MIGRATION (SERVER DENIES ALL)'
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
    ServerCertificateStatus = $serverCertificateState
    ViewerCertificateStatuses = $viewerCertificateStates
    CertificateAttentionCount = $attentionCount
    ServerClientRosterStatus = $clientRosterStatus
    ServerAllowedClientCount = $allowedClients
    ServerDisabledClientCount = $disabledClients
    ServerViewOnlyClientCount = $viewOnlyClients
    Tcp15444Listeners = $listeners
    ServiceInstallPerformed = $false
    FirewallChanged = $false
    Note = 'Metadata only. GUI and input require isolated Windows acceptance.'
} | ConvertTo-Json -Depth 6
