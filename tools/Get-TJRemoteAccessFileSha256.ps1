#Requires -Version 5.1
<#
.SYNOPSIS
    Computes SHA-256 using .NET, without Get-FileHash or PSModulePath.
.DESCRIPTION
    Windows GitHub runners and constrained PowerShell sessions may omit
    Microsoft.PowerShell.Utility's Get-FileHash. Use a deterministic .NET
    implementation shared by the self-contained Preview packaging scripts.
    This does not weaken or skip any package verification.
#>
function Get-TJRemoteAccessFileSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [ValidateNotNullOrEmpty()]
        [string]$LiteralPath
    )
    $stream = $null
    $sha = $null
    try {
        $stream = [System.IO.File]::Open(
            $LiteralPath, [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
        $sha = [System.Security.Cryptography.SHA256]::Create()
        $digest = $sha.ComputeHash($stream)
        return [System.BitConverter]::ToString($digest).Replace('-', '')
    } finally {
        if ($null -ne $sha) { $sha.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
