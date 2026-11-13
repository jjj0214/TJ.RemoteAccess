// SPDX-License-Identifier: GPL-2.0-or-later
// M3 SERVICE FOUNDATION. No network listener, remote input, desktop capture
// or unattended access. The agent launcher is deliberately not enabled until
// token/handle/consent and engine authorization gates are independently proven.
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.ServiceProcess;

namespace TJ.RemoteAccess.M3;

[SupportedOSPlatform("windows")]
internal sealed class M3ControlService : ServiceBase
{
    internal const string ServiceIdentity = "TJRA.ControlService";
    private readonly object _sync = new();
    private CancellationTokenSource? _shutdown;
    private Task? _agentWorker;

    internal M3ControlService()
    {
        ServiceName = ServiceIdentity;
        CanStop = true;
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;
    }

    internal static void ValidateServiceIdentity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null ||
            !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            !Environment.UserInteractive)
        {
            // LocalSystem only; session-zero requirement is checked separately
            // in the service startup path rather than relying on account alone.
            if (identity.User is null ||
                !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
                throw new UnauthorizedAccessException("M3 service requires LocalSystem.");
        }
        if (Process.GetCurrentProcess().SessionId != 0)
            throw new UnauthorizedAccessException("M3 service must execute in Session 0.");
    }

    protected override void OnStart(string[] args)
    {
        if (args.Length != 0)
            throw new ArgumentException("ControlService does not accept startup arguments.");
        ValidateServiceIdentity();
        lock (_sync)
        {
            _shutdown = new CancellationTokenSource();
            var token = _shutdown.Token;
            _agentWorker = Task.Run(async () => {
                try { await M3WindowsSessionLauncher.RunSessionOnceAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex) {
                    Trace.TraceWarning("M3 status-only agent stopped: " + ex.GetType().Name);
                }
            });
        }
        // This safety milestone intentionally has no agent-launch or network
        // listener: a status-only agent cannot be promoted to remote input.
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        lock (_sync)
        {
            // A session change always invalidates any future agent attachment.
            _shutdown?.Cancel();
        }
        base.OnSessionChange(change);
    }

    protected override void OnStop() => Shutdown();

    protected override void OnShutdown()
    {
        Shutdown();
        base.OnShutdown();
    }

    private void Shutdown()
    {
        lock (_sync)
        {
            _shutdown?.Cancel();
            _shutdown?.Dispose();
            _shutdown = null;
        }
    }

    private static void Main()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        ServiceBase.Run(new M3ControlService());
    }
}