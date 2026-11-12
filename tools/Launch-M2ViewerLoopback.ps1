# SPDX-License-Identifier: GPL-2.0-or-later
# Manual interactive experiment: launches ONLY the derived Viewer and connects
# to the already-running VNC server on the SAME PC via loopback:5900.
# No remote target, service, registry, port binding or firewall changes.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fork = Join-Path $root 'out\m2-fork\tightvnc-2.8.90-tjra\upstream'
if (-not (Test-Path -LiteralPath $fork -PathType Container)) {
    throw 'Derived build missing. Run tools\Build-M2Fork.ps1 first.'
}
$candidates = @(
    Get-ChildItem -LiteralPath $fork -Recurse -File -Filter 'TJ.RemoteAccess.Viewer.exe' |
    Select-Object -ExpandProperty FullName
)
if ($candidates.Count -ne 1) {
    throw "Expected ONE derived Viewer executable, found $($candidates.Count). Review the fork build log."
}
$listening = @(Get-NetTCPConnection -LocalPort 5900 -State Listen -ErrorAction SilentlyContinue |
    Where-Object { $_.LocalAddress -in @('127.0.0.1', '::1', '0.0.0.0', '::') })
if ($listening.Count -eq 0) {
    throw 'No VNC listener on localhost:5900. Existing TightVNC must be started independently on a test PC.'
}
Write-Warning 'Experimental Viewer derived by source substitution. Use only on an authorized development PC.'
Write-Host "Launching $($candidates[0]) -> 127.0.0.1::5900"
Start-Process -FilePath $candidates[0] -ArgumentList '127.0.0.1::5900' -WorkingDirectory (Split-Path -Parent $candidates[0])
Write-Host 'Viewer started. Confirm first frame, mouse/keyboard behavior and disconnect manually.'
