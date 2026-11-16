// SPDX-License-Identifier: GPL-2.0-or-later
// Windows PowerShell AST syntax acceptance ONLY. Never runs the installers.
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

public sealed class TJRemoteAccessPowerShellParseTests
{
    [Theory]
    [InlineData("Install-TJRemoteAccess-Preview.ps1")]
    [InlineData("Build-TJRemoteAccess-Preview-OfflinePackage.ps1")]
    [InlineData("Get-TJRemoteAccessFileSha256.ps1")]
    [InlineData("New-TJRemoteAccess-PreviewZip.ps1")]
    [InlineData("Build-TJRemoteAccess-Preview-GuiPackage.ps1")]
    [InlineData("Install-TJRemoteAccess-Preview-Offline.ps1")]
    [InlineData("Test-TJRemoteAccess-Preview.ps1")]
    [InlineData("Uninstall-TJRemoteAccess-Preview.ps1")]
    [InlineData("Set-TJRemoteAccess-Preview-Startup.ps1")]
    [InlineData("Measure-TJRemoteAccess-Preview.ps1")]
    [InlineData("Build-TJRemoteAccess-M3-LabPackage.ps1")]
    [InlineData("Install-TJRemoteAccess-M3-Lab.ps1")]
    [InlineData("Uninstall-TJRemoteAccess-M3-Lab.ps1")]
    [InlineData("Test-TJRemoteAccess-M3-Lab.ps1")]
    [InlineData("Build-TJRemoteAccess-ServicePreviewPackage.ps1")]
    [InlineData("Install-TJRemoteAccess-ServicePreview.ps1")]
    [InlineData("Uninstall-TJRemoteAccess-ServicePreview.ps1")]
    [InlineData("Test-TJRemoteAccess-ServicePreview.ps1")]
    public async Task ScriptsHaveNoPowerShellParserErrors(string fileName)
    {
        if (!OperatingSystem.IsWindows()) return;
        string path = Path.Combine(SourceDirectory(), "tools", fileName);
        Assert.True(File.Exists(path), "Expected versioned source script missing.");

        // The fixed PowerShell AST Parser analyzes source. It does NOT run any
        // script content and never installs, launches or changes services.
        string literal = path.Replace("'", "''");
        string script = "$t=$null;$e=$null;" +
            "[System.Management.Automation.Language.Parser]::ParseFile('" +
            literal + "',[ref]$t,[ref]$e)|Out-Null;" +
            "if(@($e).Count -gt 0){$e|Out-String|Write-Error;exit 2};exit 0";
        var start = new ProcessStartInfo("powershell.exe") {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Windows PowerShell 5.1 unavailable.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        Assert.True(process.ExitCode == 0,
            fileName + " PowerShell parser exit " + process.ExitCode + ": " + await stderr);
    }

    private static string SourceDirectory([CallerFilePath] string fileName = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fileName)!, ".."));
}
