#Requires -Version 5.1
<#
.SYNOPSIS
  Produce an offline, SOURCE-CORRESPONDING, unsigned Standalone Service Preview ZIP.
.DESCRIPTION
  Builds the independent Windows Service and SessionAgent as self-contained
  win-x64 executables, publishes their hashes and bundles the exact GPL source.
  Does not install, start, register, sign, or change any Windows service.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9.+-]{1,40}$')]
    [string]$Version = '0.5.2-preview'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$changes = @(& git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or @($changes | Where-Object { $_ }).Count -ne 0) {
    throw 'M3 package requires a CLEAN committed source tree and matching GPL source.'
}
$commit = (& git -C $root rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'M3 package could not establish source revision.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 9 SDK is required for Preview Service packaging.'
}
$publishDir = Join-Path $root 'out\preview-service-publish'
$packageDir = Join-Path $root 'out\packages'
$zip = Join-Path $packageDir ("TJ.RemoteAccess.ServicePreview-$Version-win-x64.zip")
if (Test-Path -LiteralPath $zip) {
    throw "M3 package exists and cannot be overwritten: $zip"
}
$batch = [guid]::NewGuid().ToString('N')
$work = Join-Path $publishDir ('.stage-' + $batch)
$payload = Join-Path $work 'payload'
$source = Join-Path $root 'src\TJRemoteAccess.Core'
$serviceExe = 'TJ.RemoteAccess.M3.ControlService.exe'
$agentExe = 'TJ.RemoteAccess.M3.Agent.exe'
$built = @(
    [pscustomobject]@{ Project = 'TJ.RemoteAccess.M3.ControlService.csproj'; Exe = $serviceExe; Folder = 'Service' },
    [pscustomobject]@{ Project = 'TJ.RemoteAccess.M3.Agent.csproj'; Exe = $agentExe; Folder = 'Agent' },
    [pscustomobject]@{ Project = 'TJ.RemoteAccess.Server.csproj'; Exe = 'TJ.RemoteAccess.Server.exe'; Folder = 'Server' },
    [pscustomobject]@{ Project = 'TJ.RemoteAccess.Setup.csproj'; Exe = 'TJ.RemoteAccess.Setup.exe'; Folder = 'Setup' },
    [pscustomobject]@{ Project = 'TJ.RemoteAccess.Viewer.csproj'; Exe = 'TJ.RemoteAccess.Viewer.exe'; Folder = 'Viewer' }
)
try {
    # Native PowerShell AST parsing is syntax-only: no installer execution.
    foreach ($script in @('Build-TJRemoteAccess-ServicePreviewPackage.ps1',
                          'Install-TJRemoteAccess-ServicePreview.ps1',
                          'Uninstall-TJRemoteAccess-ServicePreview.ps1',
                          'Test-TJRemoteAccess-ServicePreview.ps1',
                          'Measure-TJRemoteAccess-Preview.ps1')) {
        $tokens = $null
        $syntaxErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot $script),
            [ref]$tokens, [ref]$syntaxErrors
        ) | Out-Null
        if (@($syntaxErrors).Count -ne 0) {
            throw "M3 PowerShell parse failed for $script. Package not created."
        }
    }
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    foreach ($component in $built) {
        $project = Join-Path $source $component.Project
        $destination = Join-Path $work $component.Folder
        & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o $destination
        if ($LASTEXITCODE -ne 0) {
            throw "M3 publish failed: $($component.Project)"
        }
        $published = Join-Path $destination $component.Exe
        if (-not (Test-Path -LiteralPath $published -PathType Leaf)) {
            throw "Missing M3 published executable: $($component.Exe)"
        }
        Copy-Item -LiteralPath $published -Destination (Join-Path $payload $component.Exe)
        Write-Host "M3 BUILD PASS $($component.Exe)"
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $payload 'LICENSE')
    foreach ($script in @('Install-TJRemoteAccess-ServicePreview.ps1',
                          'Uninstall-TJRemoteAccess-ServicePreview.ps1',
                          'Test-TJRemoteAccess-ServicePreview.ps1',
                          'Measure-TJRemoteAccess-Preview.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $script) -Destination (Join-Path $payload $script)
    }
    & git -C $root archive --format=zip "--output=$(Join-Path $payload 'source.zip')" HEAD
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $payload 'source.zip'))) {
        throw 'Exact GPL source archive generation failed.'
    }
    $fileRows = @(
        Get-ChildItem -LiteralPath $payload -File | Sort-Object Name | ForEach-Object {
            [pscustomobject]@{
                Path = $_.Name
                Bytes = [long]$_.Length
                Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        }
    )
    $manifest = [ordered]@{
        SchemaVersion = 1
        Artifact = 'TJ.RemoteAccess.Service.Preview'
        SourceRevision = $commit
        RuntimeIdentifier = 'win-x64'
        Unsigned = $true
        ServiceStartup = 'Automatic'
        RemoteAccessEnabled = $false
        DesktopHostingAvailable = $true
        Files = $fileRows
    }
    $manifestPath = Join-Path $payload 'manifest.json'
    [System.IO.File]::WriteAllText($manifestPath,
        ($manifest | ConvertTo-Json -Depth 6),
        [System.Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Path $packageDir -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText(($zip + '.sha256'),
        "$hash  $(Split-Path -Leaf $zip)" + [Environment]::NewLine,
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "PREVIEW SERVICE PACKAGE: $zip"
    Write-Host "SHA256: $hash"
    Write-Warning 'This is an UNSIGNED DEVELOPMENT package. Supervisor can launch user-consented, visible desktop Server only. Winlogon/UAC not supported.'
}
catch {
    if (Test-Path -LiteralPath $zip) {
        Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath ($zip + '.sha256')) {
        Remove-Item -LiteralPath ($zip + '.sha256') -Force -ErrorAction SilentlyContinue
    }
    throw
}
finally {
    if (Test-Path -LiteralPath $work) {
        Remove-Item -LiteralPath $work -Recurse -Force
    }
}