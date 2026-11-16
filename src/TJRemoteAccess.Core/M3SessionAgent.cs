// SPDX-License-Identifier: GPL-2.0-or-later
// Status-only interactive process launched by the SYSTEM ControlService.
// No screen capture, input, RFB listener, or UAC/secure-desktop handling.
using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace TJ.RemoteAccess.M3;

internal sealed record M3AgentArguments(string PipeInstance, IntPtr KeyReadHandle, uint ServicePid,
                                        bool SuperviseDesktop = false)
{
    internal static M3AgentArguments Parse(string[] args)
    {
        if (args is null || args.Length is not (6 or 7) || args[0] != "--pipe" ||
            args[2] != "--key-handle" || args[4] != "--service-pid" ||
            (args.Length == 7 && args[6] != "--supervise-server"))
            throw new ArgumentException("A private service launch is required.");
        string pipe = M3WindowsPipe.PipeName(args[1]);
        if (pipe.Length == 0 ||
            !ulong.TryParse(args[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong raw) ||
            raw == 0 || raw > long.MaxValue ||
            !uint.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) ||
            pid == 0)
            throw new ArgumentException("Invalid inherited launch handle or service PID.");
        return new(args[1], new IntPtr((long)raw), pid, args.Length == 7);
    }
}

internal static class M3SessionAgentProgram
{
    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeServerProcessId", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetServerPid(SafePipeHandle pipe, out uint pid);

    internal static void ValidateParent(NamedPipeClientStream pipe, uint expectedServicePid)
    {
        if (!pipe.IsConnected || expectedServicePid == 0 ||
            !GetServerPid(pipe.SafePipeHandle, out uint actual) ||
            actual != expectedServicePid)
            throw new AuthenticationException("Service pipe owner does not match the launching parent.");
    }

    internal static async Task RunAsync(M3AgentArguments options, CancellationToken stop)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("M3 agent requires Windows.");
        using var keyHandle = new SafePipeHandle(options.KeyReadHandle, ownsHandle: true);
        using var keyReader = new AnonymousPipeClientStream(PipeDirection.In, keyHandle);
        byte[] key = new byte[M3Wire.KeyBytes];
        // Bootstrap is bounded; the authenticated session has no arbitrary 12-hour cut-off.
        using var bootstrap = CancellationTokenSource.CreateLinkedTokenSource(stop);
        bootstrap.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Inherited from the exact CreateProcessAsUser child only. Never sent via
            // arguments, environment, disk, log or network. Fail closed on short reads.
            await keyReader.ReadExactlyAsync(key, bootstrap.Token).ConfigureAwait(false);
            keyReader.Dispose();
            using var pipe = M3WindowsPipe.CreateLocalClient(options.PipeInstance);
            await pipe.ConnectAsync(5000, bootstrap.Token).ConfigureAwait(false);
            ValidateParent(pipe, options.ServicePid);
            uint pid = (uint)Environment.ProcessId;
            uint session = checked((uint)Process.GetCurrentProcess().SessionId);
            if (session == 0)
                throw new AuthenticationException("Session 0 cannot host an interactive agent.");
            await M3AgentProof.AuthenticateChildAsync(pipe, key, pid, session, bootstrap.Token)
                .ConfigureAwait(false);
            // The desktop is only started with BOTH a service-owned operator
            // policy and an enrolled CURRENT USER. Its visible GUI runs in
            // this user's session; the child belongs to a kill-on-close Job.
            using M3SupervisedDesktopHost? supervised = options.SuperviseDesktop
                ? M3SupervisedDesktopHost.StartIfEnrolled() : null;
            if (options.SuperviseDesktop)
                await M3Wire.WriteAsync(pipe, M3MessageType.AgentStatus,
                    new byte[] { supervised is null ? (byte)0 : (byte)1 }, stop)
                    .ConfigureAwait(false);
            while (!stop.IsCancellationRequested)
            {
                if (supervised is not null &&
                    (!supervised.IsRunning ||
                     !M3DesktopEnrollmentStore.IsEnabledForCurrentUser() ||
                     !M3InteractiveDesktopGuard.IsNormalInteractiveDesktop()))
                    throw new IOException("Supervised Server exited or owner revoked consent.");
                await M3Wire.WriteAsync(pipe, M3MessageType.Heartbeat,
                    ReadOnlyMemory<byte>.Empty, stop).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromSeconds(3), stop).ConfigureAwait(false);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

#if !TJRA_TEST
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = M3AgentArguments.Parse(args);
            // Service cancellation tears down the owned child process. The
            // Agent itself fails closed if service pipe disconnects.
            await RunAsync(options, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or AuthenticationException or
            IOException or OperationCanceledException or InvalidDataException or
            UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Avoid printing path/handle/token/key values to shared process logs.
            Console.Error.WriteLine("TJRA M3 agent stopped: bootstrap/authentication failure.");
            return 1;
        }
    }
#endif
}