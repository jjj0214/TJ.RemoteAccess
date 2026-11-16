#Requires -Version 5.1
<#
.SYNOPSIS
  Install the isolated TJ.RemoteAccess SERVICE PREVIEW Windows Service.
.DESCRIPTION
  Requires administrator approval and a trusted ZIP SHA-256 supplied separately
  from the archive. Verifies all ZIP entries, manifest, and individual hashes
  before stopping/replacing any prior Service Preview install. Automatic service startup; optional user-consented desktop preview.
  NEVER installs remote-control hooks, opens firewall, or modifies TightVNC.
  Intended ONLY for a disposable Windows 10/11 test VM.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string]$ExpectedPackageSha256,
    [switch]$ApproveServicePreview,
    [switch]$Upgrade,
    [switch]$EnableSupervisedDesktop,
    [switch]$Start
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $ApproveServicePreview) {
    throw 'Service Preview requires explicit -ApproveServicePreview on an isolated test VM.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run in an elevated PowerShell on an isolated Windows test VM.'
}
$archive = (Resolve-Path -LiteralPath $PackagePath -ErrorAction Stop).Path
if ([IO.Path]::GetExtension($archive) -ine '.zip') {
    throw 'Only a prebuilt Service Preview ZIP is accepted.'
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $ExpectedPackageSha256) {
    throw 'Untrusted or corrupted Service Preview ZIP: SHA-256 mismatch. No changes made.'
}
$serviceName = 'TJRA.ControlService'
$serviceExe = 'TJ.RemoteAccess.M3.ControlService.exe'
$agentExe = 'TJ.RemoteAccess.M3.Agent.exe'
$allowed = @($serviceExe, $agentExe, 'LICENSE', 'source.zip',
    'Install-TJRemoteAccess-ServicePreview.ps1',
    'Uninstall-TJRemoteAccess-ServicePreview.ps1',
    'Test-TJRemoteAccess-ServicePreview.ps1',
    'Measure-TJRemoteAccess-Preview.ps1',
    'TJ.RemoteAccess.Server.exe', 'TJ.RemoteAccess.Setup.exe', 'TJ.RemoteAccess.Viewer.exe')
$allowedAll = @($allowed + @('manifest.json'))
$rootDir = Join-Path $env:ProgramFiles 'TJ.RemoteAccess'
$installDir = Join-Path $rootDir 'ServicePreview'
$installedServiceExe = Join-Path $installDir $serviceExe
$batch = [guid]::NewGuid().ToString('N')
$stage = Join-Path $rootDir ('.m3-stage-' + $batch)
$backup = Join-Path $rootDir ('.m3-backup-' + $batch)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $zipFiles = @{}
    [long]$total = 0
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName
        if ($name.EndsWith('/') -or $name.Contains('\') -or $name.Contains(':') -or
            $name.Contains('/') -or $name -match '^\.' -or
            $name -notin $allowedAll) {
            throw "Unexpected M3 package entry: $name"
        }
        if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
            throw "Symlink M3 package entry forbidden: $name"
        }
        if ($entry.Length -lt 0 -or $entry.Length -gt 536870912) {
            throw "Oversized M3 package entry: $name"
        }
        $total += [long]$entry.Length
        if ($total -gt 1610612736) {
            throw 'Service Preview package uncompressed size limit exceeded.'
        }
        $id = $name.ToUpperInvariant()
        if ($zipFiles.ContainsKey($id)) {
            throw "Duplicate or case-conflicting ZIP entry: $name"
        }
        $zipFiles[$id] = $entry
    }
    if ($zipFiles.Count -ne $allowedAll.Count) {
        throw 'M3 package contents are incomplete or unexpected.'
    }
    foreach ($name in $allowedAll) {
        if (-not $zipFiles.ContainsKey($name.ToUpperInvariant())) {
            throw "M3 package missing $name"
        }
    }
    $manifestEntry = $zipFiles['MANIFEST.JSON']
    if ($manifestEntry.Length -lt 5 -or $manifestEntry.Length -gt 1048576) {
        throw 'M3 manifest length invalid.'
    }
    $stream = $manifestEntry.Open()
    try {
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false))
        try { $manifest = ConvertFrom-Json -InputObject $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
    if ($manifest.SchemaVersion -ne 1 -or
        $manifest.Artifact -ne 'TJ.RemoteAccess.Service.Preview' -or
        $manifest.RuntimeIdentifier -ne 'win-x64' -or
        -not $manifest.Unsigned -or
        $manifest.ServiceStartup -ne 'Automatic' -or
        -not $manifest.DesktopHostingAvailable -or
        $manifest.RemoteAccessEnabled -ne $false -or
        $manifest.SourceRevision -notmatch '^[0-9a-fA-F]{40}$' -or
        @($manifest.Files).Count -ne $allowed.Count) {
        throw 'Unsupported M3 manifest or remote-control claims.'
    }
    $manifestRows = @{}
    foreach ($row in @($manifest.Files)) {
        $name = [string]$row.Path
        $id = $name.ToUpperInvariant()
        if ($name -notin $allowed -or $manifestRows.ContainsKey($id) -or
            $row.Sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            [long]$row.Bytes -ne [long]$zipFiles[$id].Length) {
            throw "M3 manifest mismatch: $name"
        }
        $manifestRows[$id] = $row
    }
    foreach ($name in $allowed) {
        $id = $name.ToUpperInvariant()
        if (-not $manifestRows.ContainsKey($id)) {
            throw "Unmanifested M3 file: $name"
        }
        $entryStream = $zipFiles[$id].Open()
        try {
            $digest = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($digest.ComputeHash($entryStream)).Replace('-', '') }
            finally { $digest.Dispose() }
        } finally { $entryStream.Dispose() }
        if ($actual -ine [string]$manifestRows[$id].Sha256) {
            throw "M3 file SHA-256 mismatch: $name"
        }
    }
}
finally { $zip.Dispose() }

# All manifest hashes passed BEFORE touching the installation/service.
$previous = Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue
if ($null -ne $previous) {
    if (-not $Upgrade) {
        throw 'Service Preview service already exists. Explicit -Upgrade required.'
    }
    $previousPath = ([string]$previous.PathName).Trim().Trim('"')
    if ($previousPath -ine $installedServiceExe -or
        [string]$previous.StartName -ne 'LocalSystem') {
        throw 'Existing service is not the expected LocalSystem Service Preview installation.'
    }
}
if ((Test-Path -LiteralPath $installDir) -and (-not $Upgrade -or $null -eq $previous)) {
    throw 'Unowned Service Preview install directory exists; will not overwrite it.'
}
$wasRunning = $false
$createdService = $false
$previousSaved = $false
$newPlaced = $false
try {
    New-Item -ItemType Directory -Path $rootDir -Force | Out-Null
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $stage)
    # Verify disk writes and component identities before any system change.
    foreach ($name in $allowed) {
        $file = Join-Path $stage $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine
               [string]$manifestRows[$name.ToUpperInvariant()].Sha256) {
            throw "Post-extract hash mismatch: $name"
        }
    }
    if ($null -ne $previous) {
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        $wasRunning = $service.Status -eq 'Running'
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -ErrorAction Stop
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(20))
        }
    }
    if (Test-Path -LiteralPath $installDir) {
        Move-Item -LiteralPath $installDir -Destination $backup -ErrorAction Stop
        $previousSaved = $true
    }
    Move-Item -LiteralPath $stage -Destination $installDir -ErrorAction Stop
    $newPlaced = $true
    # Administrator-owned policy: absent unless explicitly approved.
    # A second opt-in in the signed-in user's TJ.RemoteAccess.Setup is
    # required before the Agent will launch a visible TLS Server.
    if ($EnableSupervisedDesktop) {
        $policy = [pscustomobject]@{
            SchemaVersion = 1
            EnableSupervisedDesktop = $true
            Mode = 'InteractiveOwnerConsentOnly'
        }
        [IO.File]::WriteAllText((Join-Path $installDir 'desktop-host-policy.json'),
            ($policy | ConvertTo-Json -Depth 3),
            [Text.UTF8Encoding]::new($false))
    }
    if ($null -eq $previous) {
        & sc.exe create $serviceName binPath= ('"' + $installedServiceExe + '"') start= auto obj= LocalSystem DisplayName= 'TJ.RemoteAccess Service Preview'
        if ($LASTEXITCODE -ne 0) {
            throw 'Windows SCM create failed.'
        }
        $createdService = $true
    }
    if ($Start) {
        Start-Service -Name $serviceName -ErrorAction Stop
        (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    }
    Write-Host "SERVICE PREVIEW INSTALL PASS: $installDir"
    Write-Host 'Service startup: AUTOMATIC, but no interactive TLS Server without BOTH opt-ins.'
    Write-Host 'TightVNC, firewall, certificates and existing Lab profiles were NOT changed.'
    if ($previousSaved) { Remove-Item -LiteralPath $backup -Recurse -Force }
}
catch {
    $original = $_
    if ($createdService) {
        try { & sc.exe delete $serviceName | Out-Null } catch { }
    }
    if ($newPlaced -and (Test-Path -LiteralPath $installDir)) {
        Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($previousSaved -and (Test-Path -LiteralPath $backup)) {
        Move-Item -LiteralPath $backup -Destination $installDir -ErrorAction Stop
    }
    if ($wasRunning -and $null -ne $previous) {
        try { Start-Service -Name $serviceName -ErrorAction Stop }
        catch { Write-Warning 'Previous M3 service restart failed; inspect SCM event log.' }
    }
    throw $original
}
finally {
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}