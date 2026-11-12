#Requires -Version 5.1
<#
.SYNOPSIS
  Install a PRE-BUILT self-contained TJ.RemoteAccess Lab ZIP (no .NET SDK needed).
.DESCRIPTION
  Requires an out-of-band trusted SHA-256 for the ZIP; rejects unsafe ZIP paths,
  unmanifested files, duplicate paths and corrupted file hashes BEFORE changing
  any installed component. Current-user only, atomic per-component rollback.
  No Windows service, firewall, TightVNC or certificate/profile changes.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedPackageSha256,
    [ValidateSet('Both', 'Server', 'Viewer')]
    [string]$Mode = 'Both'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$archivePath = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
if ([System.IO.Path]::GetExtension($archivePath) -ine '.zip') {
    throw 'Offline Lab package must be a ZIP archive.'
}
$actualPackageHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if (-not [string]::Equals($actualPackageHash, $ExpectedPackageSha256,
                         [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package SHA-256 does not match the out-of-band trusted hash. No changes made.'
}
$components = if ($Mode -eq 'Both') { @('Server','Viewer','Setup') }
              elseif ($Mode -eq 'Server') { @('Server','Setup') }
              else { @('Viewer','Setup') }
$running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object {
    $_.ProcessName -in @('TJ.RemoteAccess.Lab.Server',
                         'TJ.RemoteAccess.Lab.Viewer',
                         'TJ.RemoteAccess.Lab.Setup')
})
if ($running.Count -ne 0) {
    throw 'Close all TJ.RemoteAccess Lab processes before updating.'
}
$publishBase = Join-Path $env:LOCALAPPDATA 'Programs\TJ.RemoteAccess.Lab'
New-Item -ItemType Directory -Path $publishBase -Force | Out-Null
$batch = [guid]::NewGuid().ToString('N')
$stageRoot = Join-Path $publishBase ('.stage-' + $batch)
$installed = @()
$allowedRoot = @('LICENSE', 'Install-TJRemoteAccess-Lab-Offline.ps1',
                 'Uninstall-TJRemoteAccess-Lab.ps1', 'Test-TJRemoteAccess-Lab.ps1',
                 'manifest.json', 'source.zip')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
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
            throw 'Uncompressed Lab package is too large.'
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
        $manifest.Artifact -ne 'TJ.RemoteAccess.Lab.Offline' -or
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
        $expected = "$component/TJ.RemoteAccess.Lab.$component.exe".ToUpperInvariant()
        if (-not $manifestPaths.ContainsKey($expected)) {
            throw "Required executable missing from package: $component"
        }
    }
    New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $stageRoot)
    foreach ($file in @($manifest.Files)) {
        $fullPath = Join-Path $stageRoot ([string]$file.Path).Replace('/', '\')
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf) -or
            -not [string]::Equals(
                (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash,
                [string]$file.Sha256,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Manifest hash verification failed: $($file.Path)"
        }
    }
} catch {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
} finally { $archive.Dispose() }

try {
    foreach ($component in $components) {
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
        $exe = Join-Path $target ("TJ.RemoteAccess.Lab.$component.exe")
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
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TJ.RemoteAccess Lab'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    foreach ($component in $components) {
        $exe = Join-Path (Join-Path $publishBase $component) ("TJ.RemoteAccess.Lab.$component.exe")
        $shortcut = $shell.CreateShortcut((Join-Path $startMenu ("TJ.RemoteAccess Lab $component.lnk")))
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = Split-Path -Parent $exe
        $shortcut.Description = 'TJ.RemoteAccess LAB TEST ONLY'
        $shortcut.Save()
    }
} catch {
    Write-Warning ("Binaries installed; shortcut creation failed: " + $_.Exception.Message)
}
Write-Host "OFFLINE CURRENT-USER LAB INSTALL PASS: $publishBase"
Write-Host 'TLS profiles, certificates and passwords were NOT replaced.'
Write-Host 'No Windows service, firewall, TightVNC or UAC changes were made.'
