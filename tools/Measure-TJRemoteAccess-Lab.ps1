#Requires -Version 5.1
<#
.SYNOPSIS
  Read-only CPU, memory, handle and GDI sampling for TJ.RemoteAccess Lab.
.DESCRIPTION
  Detects processes that launch during sampling, warns if the requested
  Server/Viewer was not observed, and reports both raw samples and a summary.
  No TJRA processes, certificates, firewall rules or services are modified.
.EXAMPLE
  .\tools\Measure-TJRemoteAccess-Lab.ps1 -Target Server -DurationSeconds 120 -IntervalSeconds 10 -WarmupSeconds 10 -Label "1 Viewer"
.EXAMPLE
  .\tools\Measure-TJRemoteAccess-Lab.ps1 -Target Viewer -DurationSeconds 600 -IntervalSeconds 10 -SummaryOnly
#>
[CmdletBinding()]
param(
    [ValidateSet('Both', 'Server', 'Viewer')][string]$Target = 'Both',
    [ValidateRange(5, 3600)][int]$DurationSeconds = 30,
    [ValidateRange(1, 30)][int]$IntervalSeconds = 5,
    [ValidateRange(0, 120)][int]$WarmupSeconds = 0,
    [ValidateLength(0, 64)][string]$Label = '',
    [switch]$SummaryOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$names = switch ($Target) {
    'Server' { @('TJ.RemoteAccess.Lab.Server') }
    'Viewer' { @('TJ.RemoteAccess.Lab.Viewer') }
    default  { @('TJ.RemoteAccess.Lab.Server', 'TJ.RemoteAccess.Lab.Viewer') }
}
$cores = [math]::Max(1, [Environment]::ProcessorCount)
$warnings = @()
$baseline = @{}
$rows = @()
$guiAvailable = $false
try {
    if (-not ('TJRALabResourceCounters' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TJRALabResourceCounters
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetGuiResources(IntPtr processHandle, int flags);
}
'@ -ErrorAction Stop
    }
    $guiAvailable = $true
} catch {
    $warnings += 'GDI/USER objects unavailable: Windows user32 diagnostic helper could not load.'
}
if ($WarmupSeconds -gt 0) {
    Start-Sleep -Seconds $WarmupSeconds
}
$startedUtc = [DateTime]::UtcNow
$initialProcesses = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
$initialProcessCount = $initialProcesses.Count
if ($initialProcessCount -eq 0) {
    $warnings += 'No requested process was running when measurement began; startup samples cannot establish steady-state trends.'
}
$endUtc = $startedUtc.AddSeconds($DurationSeconds)
while ([DateTime]::UtcNow -lt $endUtc) {
    $remaining = [math]::Max(0, ($endUtc - [DateTime]::UtcNow).TotalSeconds)
    Start-Sleep -Milliseconds ([int][math]::Min($IntervalSeconds * 1000, $remaining * 1000))
    $atUtc = [DateTime]::UtcNow
    $processes = @(Get-Process -Name $names -ErrorAction SilentlyContinue)
    $samples = @()
    foreach ($process in $processes) {
        try {
            $processStarted = $null
            try { $processStarted = $process.StartTime.ToUniversalTime() } catch { }
            $identity = [string]$process.Id
            $startedIso = $null
            if ($null -ne $processStarted) {
                $identity = $identity + ':' + $processStarted.Ticks
                $startedIso = $processStarted.ToString('o')
            }
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
            $gdiObjects = $null
            $userObjects = $null
            if ($guiAvailable) {
                try {
                    $gdiObjects = [TJRALabResourceCounters]::GetGuiResources($process.Handle, 0)
                    $userObjects = [TJRALabResourceCounters]::GetGuiResources($process.Handle, 1)
                } catch { }
            }
            $samples += [pscustomobject]@{
                Process = [string]$process.ProcessName
                PID = [int]$process.Id
                SessionKey = $identity
                ProcessStartUtc = $startedIso
                StartedDuringMeasurement = ($null -ne $processStarted -and $processStarted -ge $startedUtc)
                MachineCPUPercent = $cpuPercent
                WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 1)
                PrivateMemoryMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
                Threads = [int]$process.Threads.Count
                Handles = [int]$process.HandleCount
                GdiObjects = $gdiObjects
                UserObjects = $userObjects
                UtcTime = $atUtc.ToString('o')
            }
        } catch {
            # The process may exit or restart while the snapshot is being read.
        }
        finally {
            $process.Dispose()
        }
    }
    $rows += [pscustomobject]@{
        UtcTime = $atUtc.ToString('o')
        Processes = $samples
    }
}
$all = @(
    foreach ($sample in $rows) {
        foreach ($p in $sample.Processes) { $p }
    }
)
$summaries = @()
foreach ($group in @($all | Group-Object -Property SessionKey)) {
    $ordered = @($group.Group | Sort-Object -Property UtcTime)
    $first = $ordered[0]
    $last = $ordered[$ordered.Count - 1]
    $cpuValues = @($ordered | Where-Object { $null -ne $_.MachineCPUPercent } |
        ForEach-Object { $_.MachineCPUPercent })
    $avgCpu = $null
    if ($cpuValues.Count -gt 0) {
        $avgCpu = [math]::Round(($cpuValues | Measure-Object -Average).Average, 2)
    }
    $gdiChange = $null
    $userChange = $null
    if ($null -ne $first.GdiObjects -and $null -ne $last.GdiObjects) {
        $gdiChange = $last.GdiObjects - $first.GdiObjects
    }
    if ($null -ne $first.UserObjects -and $null -ne $last.UserObjects) {
        $userChange = $last.UserObjects - $first.UserObjects
    }
    $summaries += [pscustomobject]@{
        Process = $first.Process
        PID = $first.PID
        ProcessStartUtc = $first.ProcessStartUtc
        StartedDuringMeasurement = $first.StartedDuringMeasurement
        SampleCount = $ordered.Count
        AverageMachineCPUPercent = $avgCpu
        FirstWorkingSetMB = $first.WorkingSetMB
        LastWorkingSetMB = $last.WorkingSetMB
        WorkingSetChangeMB = [math]::Round($last.WorkingSetMB - $first.WorkingSetMB, 1)
        FirstPrivateMemoryMB = $first.PrivateMemoryMB
        LastPrivateMemoryMB = $last.PrivateMemoryMB
        PrivateMemoryChangeMB = [math]::Round($last.PrivateMemoryMB - $first.PrivateMemoryMB, 1)
        FirstHandles = $first.Handles
        LastHandles = $last.Handles
        HandleChange = $last.Handles - $first.Handles
        GdiObjectChange = $gdiChange
        UserObjectChange = $userChange
        FirstObservedUtc = $first.UtcTime
        LastObservedUtc = $last.UtcTime
    }
}
if ($all.Count -eq 0) {
    $warnings += 'No target process was observed. Run this script on the PC where that component is running.'
}
$serverObserved = @($all | Where-Object {
    $_.Process -eq 'TJ.RemoteAccess.Lab.Server'
}).Count -gt 0
if ($Target -ne 'Viewer' -and -not $serverObserved) {
    $warnings += 'Server NOT observed. Shared desktop capture CPU cannot be evaluated from this measurement.'
}
if (@($all | Where-Object { $_.StartedDuringMeasurement }).Count -gt 0) {
    $warnings += 'One or more processes started during the measurement; early resource growth may be startup initialization.'
}
if ($DurationSeconds -lt 120) {
    $warnings += 'Measurement shorter than 120 seconds; resource changes alone do not establish a memory or handle leak.'
}
$outputSamples = $rows
if ($SummaryOnly) { $outputSamples = @() }
[pscustomobject]@{
    Probe = 'TJ.RemoteAccess Lab process sample (READ ONLY)'
    Label = $Label
    Target = $Target
    MeasurementStartedUtc = $startedUtc.ToString('o')
    DurationSeconds = $DurationSeconds
    IntervalSeconds = $IntervalSeconds
    WarmupSeconds = $WarmupSeconds
    LogicalProcessors = $cores
    InitialMatchingProcessCount = $initialProcessCount
    ServerObserved = $serverObserved
    GdiUserCountersAvailable = $guiAvailable
    ProcessSummaries = $summaries
    Warnings = $warnings
    Samples = $outputSamples
    Note = 'CPU is normalized to all logical processors. First CPU delta is null. Resource deltas are not proof of leaks.'
} | ConvertTo-Json -Depth 8
