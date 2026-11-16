// SPDX-License-Identifier: GPL-2.0-or-later
// Exercises the real Agent entry workflow using Win32 named + anonymous pipes.
// This is IN-PROCESS; CreateProcessAsUser and LocalSystem require a separate VM gate.
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3AgentPipeEndToEndTests
{
    private const uint DuplicateSameAccess = 2;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source,
        IntPtr targetProcess, out IntPtr duplicate, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

    [Fact]
    public async Task PrivateAnonymousKeyPipeAndRestrictedNamedPipeAuthenticateAgentEndToEnd()
    {
        if (!OperatingSystem.IsWindows() || Process.GetCurrentProcess().SessionId == 0)
            return;
        using var ownIdentity = WindowsIdentity.GetCurrent();
        var logonSid = M3WindowsPipe.GetTokenLogonSid(ownIdentity.AccessToken);
        string instance = Guid.NewGuid().ToString("N");
        using var restrictedServer = M3WindowsPipe.CreateRestrictedServer(instance, logonSid);
        using var secretSource = new AnonymousPipeServerStream(
            PipeDirection.Out, HandleInheritability.None);
        // Duplicate the child's read handle even for this same-process test:
        // each owner closes only its own kernel handle, preventing alias bugs.
        IntPtr current = GetCurrentProcess();
        if (!DuplicateHandle(current,
            secretSource.ClientSafePipeHandle.DangerousGetHandle(),
            current, out IntPtr childHandle, 0, false, DuplicateSameAccess))
            throw new InvalidOperationException("Test could not duplicate key read handle.");
        var args = new M3AgentArguments(instance, childHandle, (uint)Environment.ProcessId);
        byte[] ephemeral = RandomNumberGenerator.GetBytes(M3Wire.KeyBytes);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Task connected = restrictedServer.WaitForConnectionAsync(stop.Token);
            Task child = M3SessionAgentProgram.RunAsync(args, stop.Token);
            await secretSource.WriteAsync(ephemeral, stop.Token);
            await secretSource.FlushAsync(stop.Token);
            await connected;
            uint pid = (uint)Environment.ProcessId;
            uint session = checked((uint)Process.GetCurrentProcess().SessionId);
            var actual = M3WindowsPipe.GetVerifiedKernelPeer(restrictedServer, pid, session);
            await M3AgentProof.AuthenticateParentAsync(restrictedServer, ephemeral,
                actual.Pid, actual.SessionId, pid, session, stop.Token);
            var beat = await M3Wire.ReadAsync(restrictedServer, stop.Token);
            Assert.Equal(M3MessageType.Heartbeat, beat.Type);
            stop.Cancel();
            try { await child; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ephemeral);
        }
    }
}
