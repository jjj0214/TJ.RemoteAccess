#Requires -Version 5.1
<#
.SYNOPSIS
  Install a PRE-BUILT self-contained TJ.RemoteAccess Preview ZIP (no .NET SDK needed).
.DESCRIPTION
  Requires an out-of-band trusted SHA-256 for the ZIP; rejects unsafe ZIP paths,
  unmanifested files, duplicate paths and corrupted file hashes BEFORE changing
  any installed component. Current-user only, atomic per-component rollback.
  No Windows service, firewall, TightVNC or certificate/profile changes.
#>
[CmdletBinding()]
param(
    # Do NOT mark these as mandatory: running a script without arguments
    # otherwise enters PowerShell's hidden interactive mandatory-argument
    # prompt and appears to hang. Reject missing args with an explicit error.
    [string]$PackagePath = '',
    [string]$ExpectedPackageSha256 = '',
    [ValidateSet('Both', 'Server', 'Viewer')]
    [string]$Mode = 'Both'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($PackagePath) -or
    $ExpectedPackageSha256 -notmatch '^[0-9a-fA-F]{64}\z') {
    throw 'Missing -PackagePath <ZIP> and -ExpectedPackageSha256 <64-char hash>. Prefer TJ.RemoteAccess.Installer.exe GUI.'
}
$archivePath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
if ([System.IO.Path]::GetExtension($archivePath) -ine '.zip') {
    throw 'Offline Preview package must be a ZIP archive.'
}
Write-Host "[1/4] Verifying offline ZIP SHA-256: $archivePath"
Write-Host 'Large offline packages include GPL sources; verification may read hundreds of MB.'
$actualPackageHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
Write-Host '[1/4] Package SHA-256 verified.'
if (-not [string]::Equals($actualPackageHash, $ExpectedPackageSha256,
                         [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package SHA-256 does not match the out-of-band trusted hash. No changes made.'
}
$components = if ($Mode -eq 'Both') { @('Server','Viewer','Setup') }
              elseif ($Mode -eq 'Server') { @('Server','Setup') }
              else { @('Viewer','Setup') }
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('TJ.RemoteAccess.Server',
                         'TJ.RemoteAccess.Viewer',
                         'TJ.RemoteAccess.Setup')
})
if ($running.Count -ne 0) {
    throw 'Close all TJ.RemoteAccess Preview processes before updating. Right-click the Server tray icon and select Exit TJ.RemoteAccess.'
}
$publishBase = Join-Path $env:LOCALAPPDATA 'Programs\TJ.RemoteAccess'
New-Item -ItemType Directory -Path $publishBase -Force | Out-Null
$batch = [guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $publishBase ('.stage-' + $batch)
$installed = @()
$allowedRoot = @('LICENSE', 'Install-TJRemoteAccess-Preview-Offline.ps1',
                 'Uninstall-TJRemoteAccess-Preview.ps1', 'Test-TJRemoteAccess-Preview.ps1',
                 'Measure-TJRemoteAccess-Preview.ps1',
                 'Set-TJRemoteAccess-Preview-Startup.ps1',
                 'manifest.json', 'source.zip')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host '[2/4] Checking ZIP entry paths, limits and manifest before changing installed files.'
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $zipEntries = @{}
    [long]$totalUncompressed = 0
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        if ($name.EndsWith('/')) {
            # Directory markers must also obey the safe package namespace.
            $directory = $name.TrimEnd('/')
            if ($directory -notmatch '^(Server|Viewer|Setup)(/[A-Za-z0-9_.-]+)*$') {
                throw "Unexpected ZIP directory: $name"
            }
            continue
        }
        if ($name.StartsWith('/') -or $name.Contains('\') -or $name.Contains(':') -or
            $name -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$' -or
            @($name.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -ne 0) {
            throw "Unsafe ZIP entry name: $name"
        }
        $isComponent = $name -match '^(Server|Viewer|Setup)/'
        if (-not $isComponent -and $name -notin $allowedRoot) {
            throw "Unexpected package root entry: $name"
        }
        # POSIX symlinks are not expected in a Windows executable package.
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
            throw "Symlink entry rejected: $name"
        }
        if ($entry.Length -lt 0 -or $entry.Length -gt 536870912) {
            throw "Oversized ZIP entry: $name"
        }
        $totalUncompressed += [long]$entry.Length
        if ($totalUncompressed -gt 1610612736) {
            throw 'Uncompressed Preview package is too large.'
        }
        $id = $name.ToUpperInvariant()
        if ($zipEntries.ContainsKey($id)) {
            throw "Duplicate or case-colliding ZIP entry: $name"
        }
        $zipEntries[$id] = $entry
    }
    if (-not $zipEntries.ContainsKey('MANIFEST.JSON')) {
        throw 'Package manifest.json is missing.'
    }
    $manifestEntry = $zipEntries['MANIFEST.JSON']
    if ($manifestEntry.Length -gt 1048576) {
        throw 'Package manifest is oversized.'
    }
    $manifestStream = $manifestEntry.Open()
    try {
        $reader = [System.IO.StreamReader]::new(
            $manifestStream, [System.Text.UTF8Encoding]::new($false))
        try { $manifestText = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    } finally { $manifestStream.Dispose() }
    $manifest = ConvertFrom-Json -InputObject $manifestText
    if ($manifest.SchemaVersion -ne 1 -or
        $manifest.Artifact -ne 'TJ.RemoteAccess.Offline.Preview' -or
        $manifest.RuntimeIdentifier -ne 'win-x64' -or
        -not $manifest.Unsigned -or
        $manifest.SourceRevision -notmatch '^[0-9a-fA-F]{40}$' -or
        @($manifest.Files).Count -ne $zipEntries.Count - 1) {
        throw 'Unsupported, mismatched or unsigned-flag-missing package manifest.'
    }
    $manifestPaths = @{}
    foreach ($file in @($manifest.Files)) {
        $name = [string]$file.Path
        if (-not $zipEntries.ContainsKey($name.ToUpperInvariant()) -or
            $name -eq 'manifest.json' -or
            $name -match '(^|/)\.\.?($|/)' -or
            $file.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            [long]$file.Bytes -ne [long]$zipEntries[$name.ToUpperInvariant()].Length) {
            throw "Manifest file invalid: $name"
        }
        if ($manifestPaths.ContainsKey($name.ToUpperInvariant())) {
            throw "Manifest duplicate: $name"
        }
        $manifestPaths[$name.ToUpperInvariant()] = $file
    }
    foreach ($key in $zipEntries.Keys) {
        if ($key -ne 'MANIFEST.JSON' -and -not $manifestPaths.ContainsKey($key)) {
            throw "Unmanifested package entry: $key"
        }
    }
    foreach ($component in @('Server','Viewer','Setup')) {
        $expected = "$component/TJ.RemoteAccess.$component.exe".ToUpperInvariant()
        if (-not $manifestPaths.ContainsKey($expected)) {
            throw "Required executable missing from package: $component"
        }
    }
    Write-Host '[3/4] Verifying and extracting components with visible progress...'
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    # Stream each entry once with a bounded buffer instead of extracting the
    # ENTIRE ZIP and then reading everything a second time. The large GPL
    # source.zip is HASHED but not copied into the installed component staging.
    # This also makes progress visible and Ctrl+C cancellation responsive.
    [long]$totalBytes = 0
    foreach ($row in @($manifest.Files)) { $totalBytes += [long]$row.Bytes }
    [long]$processedBytes = 0
    $lastPercent = -1
    $lastMessageStep = -1
    foreach ($file in @($manifest.Files)) {
        $fileName = [string]$file.Path
        $entry = $zipEntries[$fileName.ToUpperInvariant()]
        $shouldExtract = $false
        foreach ($component in $components) {
            if ($fileName.StartsWith(($component + '/'), [System.StringComparison]::OrdinalIgnoreCase)) {
                $shouldExtract = $true
                break
            }
        }
        $output = $null
        if ($shouldExtract) {
            $fullPath = Join-Path $stageRoot $fileName.Replace('/', '\')
            New-Item -ItemType Directory -Path (Split-Path -Parent $fullPath) -Force | Out-Null
            $output = [System.IO.FileStream]::new(
                $fullPath, [System.IO.FileMode]::CreateNew,
                [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        }
        $sourceStream = $null
        $hasher = $null
        try {
            $sourceStream = $entry.Open()
            $hasher = [System.Security.Cryptography.SHA256]::Create()
            $buffer = New-Object 'byte[]' 131072
            [long]$entryBytes = 0
            while (($read = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $entryBytes += $read
                $processedBytes += $read
                if ($entryBytes -gt [long]$entry.Length -or
                    $processedBytes -gt $totalBytes) {
                    throw "ZIP entry expanded beyond declared manifest: $fileName"
                }
                [void]$hasher.TransformBlock($buffer, 0, $read, $buffer, 0)
                if ($null -ne $output) { $output.Write($buffer, 0, $read) }
                $percent = [int][math]::Floor(100.0 * $processedBytes / [math]::Max(1, $totalBytes))
                if ($percent -ne $lastPercent) {
                    Write-Progress -Id 5 -Activity 'TJ.RemoteAccess offline verify/extract' -Status "$percent% - $fileName" -PercentComplete $percent
                    $lastPercent = $percent
                    $messageStep = [int][math]::Floor($percent / 10)
                    if ($messageStep -gt $lastMessageStep) {
                        Write-Host ("Verified/extracted {0}% ({1:N0}/{2:N0} bytes)" -f $percent, $processedBytes, $totalBytes)
                        $lastMessageStep = $messageStep
                    }
                }
            }
            [void]$hasher.TransformFinalBlock([byte[]]::new(0), 0, 0)
            if ($entryBytes -ne [long]$entry.Length -or
                -not [string]::Equals(
                    [System.BitConverter]::ToString($hasher.Hash).Replace('-', ''),
                    [string]$file.Sha256, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Manifest SHA-256 mismatch: $fileName"
            }
        } finally {
            if ($null -ne $output) { $output.Dispose() }
            if ($null -ne $sourceStream) { $sourceStream.Dispose() }
            if ($null -ne $hasher) { $hasher.Dispose() }
        }
    }
    Write-Progress -Id 5 -Activity 'TJ.RemoteAccess offline verify/extract' -Completed
    Write-Host '[3/4] Entire ZIP verified. Only selected components are staged.'
} catch {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
} finally { $archive.Dispose() }

Write-Host '[4/4] Swapping verified components and retaining backups until successful.'
try {
    foreach ($component in $components) {
        Write-Host ("Installing component: {0}" -f $component)
        $target = Join-Path $publishBase $component
        $backup = Join-Path $publishBase ($component + '.backup-' + $batch)
        $hadPrevious = Test-Path -LiteralPath $target -PathType Container
        if ($hadPrevious) {
            Move-Item -LiteralPath $target -Destination $backup -ErrorAction Stop
        }
        $installed += [pscustomobject]@{
            Target = $target; Backup = $backup; HadPrevious = $hadPrevious
        }
        Move-Item -LiteralPath (Join-Path $stageRoot $component) -Destination $target -ErrorAction Stop
        $exe = Join-Path $target ("TJ.RemoteAccess.$component.exe")
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
            throw "Installed executable verification failed: $exe"
        }
    }
    foreach ($entry in $installed) {
        if ($entry.HadPrevious -and (Test-Path -LiteralPath $entry.Backup)) {
            Remove-Item -LiteralPath $entry.Backup -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
} catch {
    $errorItem = $_
    for ($i = $installed.Count - 1; $i -ge 0; --$i) {
        $entry = $installed[$i]
        if (Test-Path -LiteralPath $entry.Target) {
            Remove-Item -LiteralPath $entry.Target -Recurse -Force -ErrorAction Stop
        }
        if ($entry.HadPrevious -and (Test-Path -LiteralPath $entry.Backup)) {
            Move-Item -LiteralPath $entry.Backup -Destination $entry.Target -ErrorAction Stop
        }
    }
    throw $errorItem
} finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
try {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Preview'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    foreach ($component in $components) {
        $exe = Join-Path (Join-Path $publishBase $component) ("TJ.RemoteAccess.$component.exe")
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu ("TJ.RemoteAccess Preview $component.lnk")))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = Split-Path -Parent $exe
        $shortcut.Description = 'TJ.RemoteAccess PREVIEW TEST ONLY'
        $shortcut.Save()
    }
} catch {
    Write-Warning ("Binaries installed; shortcut creation failed: " + $_.Exception.Message)
}
Write-Host "OFFLINE CURRENT-USER PREVIEW INSTALL PASS: $publishBase"
Write-Host 'TLS profiles, certificates and passwords were NOT replaced.'
Write-Host 'No Windows service, firewall, TightVNC or UAC changes were made.'
