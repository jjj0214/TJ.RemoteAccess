# SPDX-License-Identifier: GPL-2.0-or-later
# M2 build-only developer workflow. NO service install/start, GUI launch or firewall changes.
[CmdletBinding()]
param(
    [string] $TightVncSourceZip = 'D:\Repository\PIE.FileTransfer\artifacts\ThirdParty\TightVNC-2.8.90-Sources\tightvnc-2.8.90-src-gpl.zip',
    [switch] $RetargetV143,
    [switch] $SkipOriginalBuild,
    [switch] $SkipPortableTests,
    [switch] $ReuseFork
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $root 'third_party\tightvnc-2.8.90-gpl'
$forkDir = Join-Path $root 'out\m2-fork\tightvnc-2.8.90-tjra-guarded-v4'
$evidence = Join-Path $root 'out\m2-evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$archive = (Resolve-Path -LiteralPath $TightVncSourceZip -ErrorAction Stop).Path

$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($hash -ne 'F3F76393A5865E1F1919C78C3EB43B9C937CF738927C2BFC920ECBD3913B061C') {
    throw "Upstream GPL ZIP hash mismatch: $hash"
}

$script:log = [ordered]@{
    startedUtc = [DateTime]::UtcNow.ToString('o')
    sourceZipSha256 = $hash
    portableTests = 'NOT_TESTED'
    originalServer = 'NOT_TESTED'
    originalViewer = 'NOT_TESTED'
    forkServer = 'NOT_TESTED'
    forkViewer = 'NOT_TESTED'
    forkHasUnreviewedIPC = $true
    runtimeViewAndInput = 'NOT_TESTED'
    serviceCoexistence = 'NOT_TESTED'
}

function Invoke-Build([string] $name, [string] $projectPath, [string] $logFile) {
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        return [pscustomobject]@{ Name = $name; Status = 'NOT_TESTED'; ExitCode = -1; Reason = "Missing: $projectPath" }
    }
    # MSBuild may write diagnostics to stderr. Avoid native-command error
    # promotion stopping the remaining fork builds on Windows PowerShell 5.1.
    $priorPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $buildOutput = & $script:msbuild $projectPath '/m:1' '/t:Build' '/p:Configuration=Release' '/p:Platform=x64' '/v:minimal' 2>&1
        $exit = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $priorPreference
    }
    $buildOutput | Out-File -LiteralPath $logFile -Encoding UTF8
    return [pscustomobject]@{
        Name = $name
        Status = $(if ($exit -eq 0) { 'PASS' } else { 'FAIL' })
        ExitCode = $exit
        LogFile = $logFile
    }
}

function Write-Evidence {
    $script:log.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $script:log | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'M2-build-results.json') -Encoding UTF8
}

Push-Location $root
try {
    $msbuildCommand = Get-Command msbuild -ErrorAction SilentlyContinue
    $script:msbuild = if ($msbuildCommand) { $msbuildCommand.Source } else { $null }
    if (-not $script:msbuild) {
        $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere)) { throw 'MSBuild not found; install VS 2022 C++ Build Tools.' }
        $candidates = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe')
        $script:msbuild = $candidates | Where-Object { $_ -match '\\MSBuild.exe$' } | Select-Object -First 1
    }
    if (-not $script:msbuild) { throw 'MSBuild.exe not found.' }
    $script:log.msbuild = $script:msbuild

    if (-not (Test-Path -LiteralPath $source)) {
        & python (Join-Path $root 'tools\vendor_upstream.py') --zip $archive
        if ($LASTEXITCODE -ne 0) { throw 'Verified upstream import failed.' }
    }
    & python (Join-Path $root 'tools\verify_upstream_tree.py') --zip $archive --source $source
    if ($LASTEXITCODE -ne 0) { throw 'Extracted upstream verification FAILED.' }

    if (-not $SkipPortableTests) {
        if ((Get-Command cmake -ErrorAction SilentlyContinue) -and (Get-Command ctest -ErrorAction SilentlyContinue)) {
            $buildPath = Join-Path $root 'out\m2-core-build'
        & cmake -S $root -B $buildPath -A x64
        if ($LASTEXITCODE -ne 0) { throw 'M2 CMake configure failed.' }
        & cmake --build $buildPath --config Release --parallel 2
        if ($LASTEXITCODE -ne 0) { throw 'M2 portable core build failed.' }
        & ctest --test-dir $buildPath -C Release --output-on-failure
        if ($LASTEXITCODE -ne 0) { throw 'M2 portable CTest failed.' }
        } else {
            Write-Warning 'CMake/CTest not found; building and running the MSVC x64 regression tests instead.'
            $testSolution = Join-Path $root 'tests\TJRA.M2.Tests.sln'
            $coreBuild = Invoke-Build 'M2 core tests' $testSolution (Join-Path $evidence 'M2-Core-MSBuild.log')
            if ($coreBuild.Status -ne 'PASS') { throw 'M2 core MSBuild failed: see M2-Core-MSBuild.log' }
            $testBin = Join-Path $root 'out\msbuild\bin\Release'
            foreach ($name in @('TJRemoteAccess.Core.Tests.exe', 'TJRemoteAccess.EngineGate.Tests.exe')) {
                $exe = Join-Path $testBin $name
                if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Missing MSVC test binary: $exe" }
                & $exe
                if ($LASTEXITCODE -ne 0) { throw "$name failed with exit code $LASTEXITCODE" }
            }
            $script:log.portableTestRunner = 'MSBuild x64 + native EXE tests'
        }
        & python -m unittest discover -s (Join-Path $root 'tests') -p 'test_*.py' -v
        if ($LASTEXITCODE -ne 0) { throw 'M2 Python tests failed.' }
        $script:log.portableTests = 'PASS'
    }

    if (-not $SkipOriginalBuild) {
        $sdk81 = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\8.1\Include'
        if (-not (Test-Path -LiteralPath $sdk81 -PathType Container)) {
            Write-Warning 'Upstream build requires Windows SDK 8.1 (MSB8036). Skipping legacy build; continuing with separate v143 fork.'
            $script:log.originalServer = [pscustomobject]@{ Status = 'BLOCKED_TOOLCHAIN'; Reason = 'Windows SDK 8.1 not installed' }
            $script:log.originalViewer = [pscustomobject]@{ Status = 'BLOCKED_TOOLCHAIN'; Reason = 'Windows SDK 8.1 not installed' }
        } else {
            # Keep the hash-pinned extracted original tree pristine.
            $originalCopy = Join-Path $root 'out\m2-original\upstream'
            if (Test-Path -LiteralPath $originalCopy) {
                Write-Warning 'Existing original build snapshot found. Not overwriting it.'
            } else {
                New-Item -ItemType Directory -Path (Split-Path -Parent $originalCopy) -Force | Out-Null
                Copy-Item -LiteralPath $source -Destination $originalCopy -Recurse -ErrorAction Stop
            }
            $script:log.originalServer = Invoke-Build 'Original Server' (Join-Path $originalCopy 'tvnserver\tvnserver.vcxproj') (Join-Path $evidence 'Original-Server.log')
            $script:log.originalViewer = Invoke-Build 'Original Viewer' (Join-Path $originalCopy 'tvnviewer\tvnviewer.vcxproj') (Join-Path $evidence 'Original-Viewer.log')
        }
    }

    if (Test-Path -LiteralPath $forkDir) {
        if (-not $ReuseFork) { throw "M2 fork already exists: $forkDir. Use -ReuseFork for a reviewed incremental rebuild." }
        $manifestPath = Join-Path $forkDir 'TJRA_FORK_MANIFEST.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Fork manifest missing; refusing reuse.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($manifest.status -ne 'TEST_FORK_NOT_SECURITY_APPROVED') { throw 'Unexpected fork manifest status.' }
        if ($manifest.source_archive_sha256.ToUpperInvariant() -ne $hash.ToUpperInvariant()) { throw 'Fork archive SHA-256 differs from pinned original.' }
        if ([bool]$manifest.retargeted_v143 -ne [bool]$RetargetV143) { throw 'Fork retarget setting does not match this build request.' }
        $requiredInterlocks = @('main-rfb-listener', 'legacy-http-listener',
            'legacy-control-pipe', 'extra-rfb-reload', 'extra-rfb-startup',
            'legacy-session-admission', 'legacy-keyboard-input',
            'legacy-pointer-input', 'legacy-rfb-file-transfer')
        if ($null -eq $manifest.PSObject.Properties['mandatory_interlocks']) {
            throw 'Existing fork predates M2 fail-closed source gates. Refusing stale fork reuse.'
        }
        foreach ($gate in $requiredInterlocks) {
            if (@($manifest.mandatory_interlocks) -cnotcontains $gate) {
                throw "Existing fork is missing mandatory interlock: $gate"
            }
        }
        if ([bool]$manifest.legacy_listeners_enabled -or [bool]$manifest.production_deployment_allowed) {
            throw 'Existing fork has an unexpected listener/deployment status.'
        }
        $requiredHooks = @('hook-loader-runtime-reference',
            'hook-dll32-runtime-reference', 'hook-dll64-runtime-reference',
            'hook-dll-project-targets')
        if ($null -eq $manifest.PSObject.Properties['hook_artifact_isolation']) {
            throw 'Fork lacks independent hook-loader/DLL names. Refusing reuse.'
        }
        foreach ($name in $requiredHooks) {
            if (@($manifest.hook_artifact_isolation) -cnotcontains $name) {
                throw "Fork is missing hook isolation: $name"
            }
        }
        Write-Warning 'Reusing a previously created derived test tree. No existing source files will be overwritten.'
    } else {
        if ($ReuseFork) { throw 'Cannot -ReuseFork because no derived test tree exists.' }
        $forkArgs = @((Join-Path $root 'tools\prepare_m2_fork.py'), '--zip', $archive, '--source', $source, '--out', $forkDir)
        if ($RetargetV143) { $forkArgs += '--retarget-v143' }
        & python @forkArgs
        if ($LASTEXITCODE -ne 0) { throw 'M2 test-fork preparation failed.' }
    }
    $script:log.forkSource = $forkDir
    $forkRoot = Join-Path $forkDir 'upstream'
    # A static code scan is evidence only, never an authorization to deploy.
    $auditPath = Join-Path $evidence 'M2-security-audit.json'
    $auditJson = (& python (Join-Path $root 'tools\audit_m2_fork_security.py') --tree $forkRoot | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'M2 security audit failed to execute.' }
    $auditReport = $auditJson | ConvertFrom-Json
    if ([bool]$auditReport.deployAllowed) { throw 'Static scan cannot approve deployment.' }
    $auditJson | Set-Content -LiteralPath $auditPath -Encoding UTF8
    $script:log.securityAudit = [pscustomobject]@{
        Status = $auditReport.status
        DeployAllowed = $false
        Findings = @($auditReport.findings).Count
        Report = $auditPath
    }

    $script:log.forkServer = Invoke-Build 'TJ Server' (Join-Path $forkRoot 'tvnserver\tvnserver.vcxproj') (Join-Path $evidence 'TJ-Server.log')
    $script:log.forkViewer = Invoke-Build 'TJ Viewer' (Join-Path $forkRoot 'tvnviewer\tvnviewer.vcxproj') (Join-Path $evidence 'TJ-Viewer.log')
    $script:log.forkControl = Invoke-Build 'TJ Control' (Join-Path $forkRoot 'tvncontrol-app\tvncontrol-app.vcxproj') (Join-Path $evidence 'TJ-Control.log')
    $script:log.generatedExecutables = @(
        Get-ChildItem -LiteralPath $forkRoot -File -Recurse -Filter 'TJ.RemoteAccess.*.exe' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty FullName
    )
    Write-Evidence
    Write-Host "M2 build report: $(Join-Path $evidence 'M2-build-results.json')"
    Write-Host 'No VNC service, registry, firewall or network listener was changed.'
    if ($script:log.forkServer.Status -ne 'PASS' -or $script:log.forkViewer.Status -ne 'PASS') {
        throw 'TJ derived Server/Viewer build FAILED. Inspect TJ-Server.log and TJ-Viewer.log.'
    }
    foreach ($expectedExe in @('TJ.RemoteAccess.Server.exe', 'TJ.RemoteAccess.Viewer.exe')) {
        $matches = @($script:log.generatedExecutables | Where-Object { [IO.Path]::GetFileName($_) -ceq $expectedExe })
        if ($matches.Count -ne 1) {
            throw "Expected exactly one $expectedExe, found $($matches.Count). Check the linker OutputFile and TargetName settings."
        }
    }
} finally {
    Write-Evidence
    Pop-Location
}
