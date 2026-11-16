#Requires -Version 5.1
<#
.SYNOPSIS
    Produce a ZIP whose entry names are EXACTLY forward-slash relative paths.
.DESCRIPTION
    PowerShell 5.1 Compress-Archive on Windows can produce archive entry
    names using backslashes. The Preview manifest uses forward slashes and
    the offline Installer intentionally rejects backslashes, so archives
    must be created using explicit ZipArchive.CreateEntry names.
    Never modifies source files; refuses to overwrite an existing output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StageDirectory,
    [Parameter(Mandatory = $true)]
    [string]$DestinationZip
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$stage = (Resolve-Path -LiteralPath $StageDirectory -ErrorAction Stop).Path.TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $stage -PathType Container)) {
    throw "ZIP stage directory not found: $stage"
}
$destination = [System.IO.Path]::GetFullPath($DestinationZip)
if ($destination.StartsWith(($stage + [System.IO.Path]::DirectorySeparatorChar),
                            [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Destination ZIP must NOT be inside its own input stage.'
}
if (Test-Path -LiteralPath $destination) {
    throw "Refusing to overwrite an existing ZIP: $destination"
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force | Sort-Object FullName)
if ($files.Count -eq 0) {
    throw 'ZIP stage is empty.'
}
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in $files) {
    if (($file.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "ZIP source symlink/reparse point rejected: $($file.FullName)"
    }
    $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
    if ($relative -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$' -or
        @($relative.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -ne 0 -or
        -not $seen.Add($relative)) {
        throw "Unsafe or duplicate ZIP relative path: $relative"
    }
}
$archive = $null
try {
    # Caller has already built the manifest and exact file hashes.
    $archive = [System.IO.Compression.ZipFile]::Open(
        $destination, [System.IO.Compression.ZipArchiveMode]::Create)
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        Write-Host "ZIP entry: $relative"
        $entry = $archive.CreateEntry(
            $relative, [System.IO.Compression.CompressionLevel]::Optimal)
        $source = $null
        $target = $null
        try {
            $source = [System.IO.File]::OpenRead($file.FullName)
            $target = $entry.Open()
            $source.CopyTo($target)
        } finally {
            if ($null -ne $target) { $target.Dispose() }
            if ($null -ne $source) { $source.Dispose() }
        }
    }
} catch {
    if ($null -ne $archive) {
        $archive.Dispose()
        $archive = $null
    }
    if (Test-Path -LiteralPath $destination -PathType Leaf) {
        Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
    }
    throw
} finally {
    if ($null -ne $archive) { $archive.Dispose() }
}
Write-Host "Forward-slash ZIP entries created: $($files.Count)"
