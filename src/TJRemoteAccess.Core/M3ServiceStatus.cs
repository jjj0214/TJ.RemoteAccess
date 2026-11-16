// SPDX-License-Identifier: GPL-2.0-or-later
// Bounded service lifecycle diagnostics only. Never consumed for authorization.
// No private keys, user accounts, remote IPs, passwords or screen contents.
using System.Text;
using System.Text.Json;

namespace TJ.RemoteAccess.M3;

internal sealed class M3ServiceStatus
{
    private readonly string _directory;
    private readonly object _sync = new();

    internal M3ServiceStatus(string? directory = null)
    {
        // The Service EXE is installed in an administrator-owned Program Files
        // directory. Never create privileged status files in user-creatable
        // ProgramData paths (junction/symlink redirection risk).
        _directory = directory ?? AppContext.BaseDirectory;
    }

    internal string StatusPath => Path.Combine(_directory, "service-status.json");

    internal void TryWrite(string state, uint? agentPid = null,
                           uint? sessionId = null, string? diagnostic = null)
    {
        if (state is not ("Starting" or "AgentAuthenticated" or "AgentFailed" or
                         "SessionRevoked" or "Stopped" or "DesktopHostStarted" or
                         "DesktopHostNotEnrolled"))
            throw new ArgumentException("Unsupported service status value.", nameof(state));
        if (diagnostic is not null &&
            (diagnostic.Length > 80 || diagnostic.Any(c => !char.IsAsciiLetterOrDigit(c))))
            throw new ArgumentException("Diagnostic must be a bounded type name.", nameof(diagnostic));
        if (state is not ("AgentAuthenticated" or "DesktopHostStarted") &&
            (agentPid is not null || sessionId is not null))
            throw new ArgumentException("Only authenticated Agent status carries kernel identities.");
        if (state is ("AgentAuthenticated" or "DesktopHostStarted") &&
            (agentPid is null or 0 || sessionId is null or 0))
            throw new ArgumentException("Authenticated Agent needs nonzero PID and session.");
        lock (_sync)
        {
            string? temp = null;
            try
            {
                Directory.CreateDirectory(_directory);
                temp = Path.Combine(_directory, ".m3-" + Guid.NewGuid().ToString("N") + ".tmp");
                var report = new
                {
                    SchemaVersion = 1,
                    Component = "TJRA.ControlService",
                    StatusOnly = state != "DesktopHostStarted",
                    // Process launch != listener readiness. Never claim a
                    // verified accessible remote desktop from status alone.
                    RemoteAccessEnabled = false,
                    DesktopHostProcessStarted = state == "DesktopHostStarted",
                    TransportVerified = false,
                    TimestampUtc = DateTimeOffset.UtcNow.ToString("O"),
                    State = state,
                    AgentPid = agentPid,
                    SessionId = sessionId,
                    DiagnosticType = diagnostic
                };
                File.WriteAllText(temp, JsonSerializer.Serialize(report), new UTF8Encoding(false));
                File.Move(temp, StatusPath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A nonessential status file must not terminate the service.
            }
            finally
            {
                try { if (temp is not null && File.Exists(temp)) File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
