// SPDX-License-Identifier: GPL-2.0-or-later
// STATUS ONLY: a LocalSystem service supervises a single user-session Agent.
// Does not expose TLS/RFB, desktop capture, remote input or UAC automation.
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
    private M3SessionLifecycle? _lifecycle;
    private readonly M3ServiceStatus _status = new();
    private uint _approvedConsoleSession;

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
            !identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid))
            throw new UnauthorizedAccessException("M3 service requires LocalSystem.");
        if (Process.GetCurrentProcess().SessionId != 0)
            throw new UnauthorizedAccessException("M3 service must execute in Session 0.");
    }

    protected override void OnStart(string[] args)
    {
        if (args.Length != 0)
            throw new ArgumentException("ControlService does not accept startup arguments.");
        ValidateServiceIdentity();
        _status.TryWrite("Starting");
        lock (_sync)
        {
            if (_lifecycle is not null)
                throw new InvalidOperationException("M3 service already started.");
            // Default fail-closed: a protected operator policy must opt into
            // desktop hosting. A user enrollment in isolation is insufficient.
            bool allowDesktopHost = M3ServiceHostPolicyStore.IsEnabled();
            uint console = M3WindowsSessionLauncher.ConsoleSessionId;
            Volatile.Write(ref _approvedConsoleSession,
                console is 0 or uint.MaxValue ? 0 : console);
            _lifecycle = new M3SessionLifecycle(
                token => M3WindowsSessionLauncher.RunSessionOnceAsync(
                    token,
                    (pid, session) => _status.TryWrite("AgentAuthenticated", pid, session),
                    superviseDesktop: allowDesktopHost,
                    onDesktopStatus: (pid, session, started) =>
                        _status.TryWrite(started ? "DesktopHostStarted" :
                            "DesktopHostNotEnrolled", started ? pid : null,
                            started ? session : null)),
                ex => {
                    _status.TryWrite("AgentFailed", diagnostic: ex.GetType().Name);
                    Trace.TraceWarning("M3 Agent stopped: " + ex.GetType().Name);
                },
                mayRetry: () => {
                    uint approved = Volatile.Read(ref _approvedConsoleSession);
                    return allowDesktopHost && M3ServiceHostPolicyStore.IsEnabled() &&
                           approved != 0 && approved == M3WindowsSessionLauncher.ConsoleSessionId;
                });
            _lifecycle.StartOrRebind();
        }
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        lock (_sync)
        {
            _status.TryWrite("SessionRevoked");
            // Revocation always wins when a desktop becomes inaccessible.
            // A subsequent verified logon/unlock creates a NEW authentication
            // key, named pipe, process and session. Never resume old input.
            if (M3ServiceSessionPolicy.MayStartConsoleAgent(change.Reason,
                change.SessionId, M3WindowsSessionLauncher.ConsoleSessionId))
            {
                Volatile.Write(ref _approvedConsoleSession, (uint)change.SessionId);
                _lifecycle?.StartOrRebind();
            }
            else
            {
                Volatile.Write(ref _approvedConsoleSession, 0);
                _lifecycle?.Revoke();
            }
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
        M3SessionLifecycle? old;
        lock (_sync)
        {
            old = _lifecycle;
            Volatile.Write(ref _approvedConsoleSession, 0);
            _lifecycle = null;
        }
        old?.Dispose();
        _status.TryWrite("Stopped");
    }

    private static void Main()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        ServiceBase.Run(new M3ControlService());
    }
}