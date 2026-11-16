#Requires -Version 5.1
<#
.SYNOPSIS
  Produce one unsigned native EXE containing exactly one private .NET 10 LTS
  Desktop Runtime plus framework-dependent Server/Viewer/Setup.
.DESCRIPTION
  Official runtime ZIP SHA-512 verified against Microsoft release metadata.
  The resulting user installer does not need admin, PowerShell, or network.
#>
[CmdletBinding()]
param(
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+-preview$')]
    [string]$Version = '0.5.4-preview',
    [ValidatePattern('^10\.0\.[0-9]+$')]
    [string]$RuntimeVersion = '10.0.15'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# PowerShell 7 argument handling: prevent Windows PowerShell 5.1 native
# command line collapsing multi-value MSBuild properties on hosted runners.
if ($PSVersionTable.PSVersion.Major -ge 7) {
    $PSNativeCommandArgumentPassing = 'Standard'
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
. (Join-Path $PSScriptRoot 'Get-TJRemoteAccessFileSha256.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Require-File([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required file missing: $path"
    }
}
function Run-Checked([string]$label,[string]$executable,[string[]]$arguments) {
    if ($null -eq $arguments -or $arguments.Count -eq 0) {
        throw "Missing process arguments for $label"
    }
    Write-Host "[run] $label"
    Write-Host ("[argv] " + $executable + " [" + ($arguments -join "] [") + "]")
    & $executable @arguments
    if ($LASTEXITCODE -ne 0) { throw "$label failed: exit=$LASTEXITCODE" }
}
function Sha512-File([string]$path) {
    $stream = [IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $hash = [Security.Cryptography.SHA512]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-','') }
    finally { $hash.Dispose(); $stream.Dispose() }
}
$changes = @(& git -C $root status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or @($changes | Where-Object { $_ }).Count -gt 0) {
    throw 'Commit tested changes before packaging. Source revision must be exact.'
}
$commit = (& git -C $root rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-fA-F0-9]{40}$') {
    throw 'Unable to determine exact source commit.'
}

$stage = Join-Path $root 'out\compact-publish'
$runtime = Join-Path $stage 'Runtime'
$downloadDir = Join-Path $root 'out\compact-download'
$dist = Join-Path $root 'out\compact-dist'
$runtimeZip = Join-Path $downloadDir "windowsdesktop-runtime-$RuntimeVersion-win-x64.zip"
$setupExe = Join-Path $dist "TJ.RemoteAccess-$Version-Compact-Setup-win-x64.exe"
$sourceZip = Join-Path $dist 'source.zip'
$provenance = Join-Path $dist 'runtime-provenance.json'
if (Test-Path -LiteralPath $setupExe -PathType Leaf) {
    throw "Refusing to overwrite existing install package: $setupExe"
}
foreach ($dir in @($stage,$downloadDir,$dist)) {
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

Write-Host '[1/7] Framework-dependent .NET 10 Server, Viewer, Setup publish...'
foreach ($app in @('Server','Viewer','Setup')) {
    $dest = Join-Path $stage $app
    if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    $project = Join-Path $root "src\TJRemoteAccess.Core\TJ.RemoteAccess.$app.csproj"
    # All three legacy .csproj files live in ONE source directory. Their
    # default obj/project.assets.json is shared, and the baseline net9
    # solution build occurs before this net10 publish. Never reuse that
    # NET9 assets cache for the NET10/win-x64 runtime-specific target.
    # Use a DIFFERENT MSBuild intermediate/restore location for each app.
    $obj = (Join-Path $root ("out\compact-obj\" + $app)).TrimEnd('\','/') + '/'
    [IO.Directory]::CreateDirectory($obj) | Out-Null
    $msbuild = @(
        '-p:TargetFramework=net10.0-windows',
        '-p:BaseIntermediateOutputPath=' + $obj,
        '-p:MSBuildProjectExtensionsPath=' + $obj,
        '-p:AppHostDotNetSearch=AppRelative',
        '-p:AppHostRelativeDotNet=../Runtime',
        "-p:Version=$Version",'-p:AssemblyVersion=0.5.4.0',
        '-p:FileVersion=0.5.4.0'
    )
    Run-Checked "$app isolated .NET 10/win-x64 NuGet restore" 'dotnet' (
        @('restore',$project,'-r','win-x64','--force','--nologo') + $msbuild
    )
    Run-Checked "$app .NET 10 publish from isolated restore" 'dotnet' (
        @('publish',$project,'-c','Release','-r','win-x64',
          '--no-restore','--self-contained','false',
          '-p:PublishSingleFile=false','-p:PublishTrimmed=false',
          '-p:PublishReadyToRun=false','-p:UseAppHost=true',
          '-o',$dest,'--nologo') + $msbuild
    )
    foreach ($ext in @('.exe','.dll','.runtimeconfig.json','.deps.json')) {
        Require-File (Join-Path $dest ("TJ.RemoteAccess.$app" + $ext))
    }
    $config = Get-Content -LiteralPath (Join-Path $dest "TJ.RemoteAccess.$app.runtimeconfig.json") -Raw |
        ConvertFrom-Json
    if ([string]$config.runtimeOptions.tfm -ne 'net10.0') {
        throw "$app publish targeted wrong .NET framework."
    }
}
Write-Host '[2/7] Resolve SHA-512-authenticated Microsoft Desktop Runtime ZIP...'
$metadataUrl = 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$metadata = Invoke-RestMethod -Uri $metadataUrl -TimeoutSec 60
$release = @($metadata.releases | Where-Object { $_.'release-version' -eq $RuntimeVersion })
if ($release.Count -ne 1) {
    throw "Official Microsoft 10.0 metadata lacks exact $RuntimeVersion. Failing closed."
}
$assets = @($release[0].windowsdesktop.files | Where-Object {
    $_.rid -eq 'win-x64' -and $_.name -eq 'windowsdesktop-runtime-win-x64.zip'
})
if ($assets.Count -ne 1) { throw 'Missing Microsoft win-x64 Desktop Runtime ZIP metadata.' }
$asset = $assets[0]
$url = [Uri]::new([string]$asset.url)
if ($url.Scheme -ne 'https' -or $url.Host -ne 'builds.dotnet.microsoft.com' -or
    $url.AbsolutePath -ne "/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.zip" -or
    [string]$asset.hash -notmatch '^[a-fA-F0-9]{128}$') {
    throw 'Official Microsoft Runtime URL/hash verification failed.'
}
if (-not (Test-Path -LiteralPath $runtimeZip -PathType Leaf)) {
    Invoke-WebRequest -Uri $url.AbsoluteUri -OutFile $runtimeZip -TimeoutSec 180
}
$actualSha512 = Sha512-File $runtimeZip
if (-not [string]::Equals($actualSha512,[string]$asset.hash,
                         [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $runtimeZip -Force -ErrorAction SilentlyContinue
    throw 'Microsoft Desktop Runtime ZIP SHA-512 mismatch; refusing build.'
}
Write-Host '[3/7] Stage runtime ONCE, isolated from global .NET...'
if (Test-Path -LiteralPath $runtime) { Remove-Item -LiteralPath $runtime -Recurse -Force }
[IO.Compression.ZipFile]::ExtractToDirectory($runtimeZip,$runtime)
foreach ($file in @(
    'dotnet.exe',
    "host\fxr\$RuntimeVersion\hostfxr.dll",
    "shared\Microsoft.NETCore.App\$RuntimeVersion\hostpolicy.dll",
    "shared\Microsoft.WindowsDesktop.App\$RuntimeVersion\Microsoft.WindowsDesktop.App.runtimeconfig.json"
)) { Require-File (Join-Path $runtime $file) }
$runtimeFiles = @(Get-ChildItem -LiteralPath $runtime -Recurse -File)
$runtimeBytes = ($runtimeFiles | Measure-Object Length -Sum).Sum
Write-Host "[runtime] $($runtimeFiles.Count) files; $runtimeBytes bytes; version $RuntimeVersion"

Write-Host '[4/7] Smoke-test Setup apphost with invalid global .NET path...'
$localSetup = Join-Path (Join-Path $stage 'Setup') 'TJ.RemoteAccess.Setup.exe'
$priorRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT','Process')
$priorRootX64 = [Environment]::GetEnvironmentVariable('DOTNET_ROOT_X64','Process')
try {
    $invalid = Join-Path $stage 'missing-global-dotnet'
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',$invalid,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64',$invalid,'Process')
    Run-Checked 'Setup service-host status (read only)' $localSetup @('service-host','status')
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',$priorRoot,'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64',$priorRootX64,'Process')
}

Write-Host '[5/7] Package GPL corresponding source and runtime provenance...'
& git -C $root archive --format=zip "--output=$sourceZip" HEAD
if ($LASTEXITCODE -ne 0) { throw 'GPL source snapshot failed.' }
Require-File $sourceZip
$provenanceData = [ordered]@{
    schemaVersion = 1
    product = 'TJ.RemoteAccess'
    version = $Version
    sourceRevision = $commit
    runtimeName = 'Microsoft .NET Windows Desktop Runtime'
    runtimeVersion = $RuntimeVersion
    runtimeRid = 'win-x64'
    runtimeUrl = $url.AbsoluteUri
    runtimeSha512 = $actualSha512
    runtimeMetadataUrl = $metadataUrl
    runtimeExtractedBytes = [long]$runtimeBytes
    appHostSearch = 'AppRelative'
    appHostRelativeDotNet = '../Runtime'
    nativeInstaller = 'Inno Setup 6'
}
[IO.File]::WriteAllText($provenance,
    ($provenanceData | ConvertTo-Json -Depth 5),
    [Text.UTF8Encoding]::new($false))

Write-Host '[6/7] Compile native Inno Setup installer (no .NET for installer)...'
$compiler = (Get-Command ISCC.exe -ErrorAction SilentlyContinue |
    Select-Object -First 1 -ExpandProperty Source)
if (-not $compiler) {
    foreach ($candidate in @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $compiler = $candidate
            break
        }
    }
}
if (-not $compiler) { throw 'Inno Setup 6 ISCC.exe missing on build machine.' }
Run-Checked 'Inno Setup compiler' $compiler @(
    "/DRepoRoot=$root", (Join-Path $PSScriptRoot 'TJ.RemoteAccess.iss')
)
Require-File $setupExe

Write-Host '[7/7] Record size, SHA-256, exact source revision...'
$info = [ordered]@{
    name = [IO.Path]::GetFileName($setupExe)
    version = $Version
    bytes = (Get-Item -LiteralPath $setupExe).Length
    sha256 = Get-TJRemoteAccessFileSha256 -LiteralPath $setupExe
    runtimeVersion = $RuntimeVersion
    runtimeBytes = [long]$runtimeBytes
    sourceRevision = $commit
    sourceSha256 = Get-TJRemoteAccessFileSha256 -LiteralPath $sourceZip
}
[IO.File]::WriteAllText((Join-Path $dist 'compact-size-report.json'),
    ($info | ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText(($setupExe + '.sha256'),
    "$($info.sha256)  $($info.name)" + [Environment]::NewLine,
    [Text.UTF8Encoding]::new($false))
Write-Host "COMPACT INSTALLER PASS: $setupExe"
Write-Host "EXE BYTES: $($info.bytes)"
Write-Host "SHARED RUNTIME BYTES: $($info.runtimeBytes)"
Write-Host "SHA-256: $($info.sha256)"
Write-Warning 'Unsigned Preview. Service / Winlogon / UAC remote access is not included.'
