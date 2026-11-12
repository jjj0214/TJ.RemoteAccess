#Requires -Version 5.1
<#
.SYNOPSIS
  Read-only CPU/memory sampling for TJ.RemoteAccess Lab processes.
.DESCRIPTION
  Measures running current-user Server/Viewer processes. Does NOT change
  process priorities, start/stop processes, write logs, change firewall,
  certificates or Windows services.
.EXAMPLE
  .\tools\Measure-TJRemoteAccess-Lab.ps1 -DurationSeconds 30 -IntervalSeconds 5
#>
[CmdletBinding()]
param(
    [ValidateRange(5, 600)][int]$DurationSeconds = 30,
    [ValidateRange(1, 30)][int]$IntervalSeconds = 5
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$names = @('TJ.RemoteAccess.Lab.Server', 'TJ.RemoteAccess.Lab.Viewer')
$cores = [math]::Max(1, [Environment]::ProcessorCount)
$baseline = @{}
$rows = @()
$startedUtc = [DateTime]::UtcNow
$endUtc = $startedUtc.AddSeconds($DurationSeconds)
while ([DateTime]::UtcNow -lt $endUtc) {
    $remaining = [math]::Max(0, ($endUtc - [DateTime]::UtcNow).TotalSeconds)
    Start-Sleep -Milliseconds ([int][math]::Min($IntervalSeconds * 1000, $remaining * 1000))
    $atUtc = [DateTime]::UtcNow
    $processes = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    $samples = @()
    foreach ($process in $processes) {
        try {
            $identity = [string]$process.Id
            $cpuSec = [double]$process.CPU
            $cpuPercent = $null
            if ($baseline.ContainsKey($identity)) {
                $previous = $baseline[$identity]
                $deltaWallSec = ($atUtc - $previous.SampleUtc).TotalSeconds
                $deltaCpuSec = $cpuSec - $previous.CpuSeconds
                if ($deltaWallSec -gt 0 -and $deltaCpuSec -ge 0) {
                    $cpuPercent = [math]::Round(
                        [math]::Min(100.0, 100.0 * $deltaCpuSec / ($deltaWallSec * $cores)), 2)
                }
            }
            $baseline[$identity] = [pscustomobject]@{
                SampleUtc = $atUtc
                CpuSeconds = $cpuSec
            }
            $samples += [pscustomobject]@{
                Process = [string]$process.ProcessName
                PID = [int]$process.Id
                MachineCPUPercent = $cpuPercent
                WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 1)
                PrivateMemoryMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
                Threads = [int]$process.Threads.Count
                Handles = [int]$process.HandleCount
            }
        } catch {
            # A process can exit between enumeration and reading metrics.
        }
    }
    $rows += [pscustomobject]@{
        UtcTime = $atUtc.ToString('o')
        Processes = $samples
    }
}
[pscustomobject]@{
    Probe = 'TJ.RemoteAccess Lab process sample (READ ONLY)'
    DurationSeconds = $DurationSeconds
    IntervalSeconds = $IntervalSeconds
    LogicalProcessors = $cores
    Samples = $rows
    Note = 'MachineCPUPercent is normalized by logical processor count; first sample has no delta.'
} | ConvertTo-Json -Depth 6
