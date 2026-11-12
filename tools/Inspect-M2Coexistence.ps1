# SPDX-License-Identifier: GPL-2.0-or-later
# Read-only evidence probe; no service, firewall, registry or socket mutation.
[CmdletBinding()]
param(
    [string] $OriginalService = 'tvnserver',
    [string] $ForkService = 'TJ.RemoteAccess.Server'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Get-ServiceEvidence([string] $name) {
    $escaped = $name.Replace("'", "''")
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$escaped'" -ErrorAction Stop
    if ($null -eq $service) {
        return [pscustomobject]@{ Name = $name; Installed = $false; State = 'NotInstalled'; ProcessId = 0; ImagePath = '' }
    }
    return [pscustomobject]@{
        Name = $service.Name
        Installed = $true
        State = $service.State
        ProcessId = [int]$service.ProcessId
        ImagePath = [string]$service.PathName
    }
}
$original = Get-ServiceEvidence $OriginalService
$fork = Get-ServiceEvidence $ForkService
$listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop)
$forkSockets = @()
if ($fork.ProcessId -gt 0) {
    $forkSockets = @($listeners | Where-Object { $_.OwningProcess -eq $fork.ProcessId })
}
$unsafeForkBinds = @($forkSockets | Where-Object { $_.LocalAddress -notin @('127.0.0.1', '::1') })
$legacy5900 = @($listeners | Where-Object { $_.LocalPort -eq 5900 })
$probe = [ordered]@{
    Probe = 'TJ.RemoteAccess coexistence (READ_ONLY)'
    TimeUtc = [DateTime]::UtcNow.ToString('o')
    Original = $original
    Fork = $fork
    OriginalRfb5900ListenerCount = $legacy5900.Count
    ForkListenerCount = $forkSockets.Count
    ForkNonLoopbackListenerCount = $unsafeForkBinds.Count
    NamespaceCheck = $(if (-not $original.Installed -or -not $fork.Installed) {
        'NOT_TESTED (both services required)'
    } elseif ($original.ImagePath -eq $fork.ImagePath) {
        'FAIL (same executable path)'
    } else {
        'PRELIMINARY_PASS (service names / paths differ)'
    })
    GuiInputAndSecureDesktop = 'NOT_TESTED (interactive test PC required)'
}
$probe | ConvertTo-Json -Depth 6
if ($unsafeForkBinds.Count -gt 0) {
    Write-Warning 'Fork listener binds outside loopback. Do not expose it; inspect process ownership and firewall.'
}
