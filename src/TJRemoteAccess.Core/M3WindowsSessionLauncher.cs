// SPDX-License-Identifier: GPL-2.0-or-later
// LocalSystem-only Windows session agent launcher. STATUS ONLY.
// Inherits ONLY one anonymous-pipe read handle via PROC_THREAD_ATTRIBUTE_HANDLE_LIST.
// The session HMAC key never appears on command line, disk, environment or network.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Principal;

namespace TJ.RemoteAccess.M3;

internal static class M3WindowsSessionLauncher
{
    private const uint TokenQuery = 0x0008;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars;
        public int FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved2Pointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read,
        out SafeFileHandle write, ref SecurityAttributes security, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count,
        uint flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags,
        IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token,
        string? app, string commandLine, IntPtr processAttrs, IntPtr threadAttrs,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags, IntPtr environment, string? workingDirectory,
        ref StartupInfoEx startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // Keeps kernel process identity pinned for the life of IPC.
    internal sealed class LaunchedAgent : IDisposable
    {
        private IntPtr _process;
        internal uint ProcessId { get; }
        internal uint SessionId { get; }

        internal LaunchedAgent(IntPtr process, uint pid, uint session)
        {
            _process = process;
            ProcessId = pid;
            SessionId = session;
        }

        public void Dispose()
        {
            var process = Interlocked.Exchange(ref _process, IntPtr.Zero);
            if (process == IntPtr.Zero) return;
            // The service owns this child; stopping service always stops its agent.
            if (GetExitCodeProcess(process, out uint code) && code == 259)
                TerminateProcess(process, 1);
            CloseHandle(process);
        }
    }

    private static string ResolveAgentPath()
    {
        string baseFolder = AppContext.BaseDirectory;
        string agent = Path.GetFullPath(Path.Combine(baseFolder, "TJ.RemoteAccess.M3.Agent.exe"));
        if (!string.Equals(Path.GetDirectoryName(agent)?.TrimEnd(Path.DirectorySeparatorChar),
                           baseFolder.TrimEnd(Path.DirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase) || !File.Exists(agent))
            throw new FileNotFoundException("Matching M3 Agent executable is missing.");
        return agent;
    }

    internal static uint ActiveSession()
    {
        uint id = WTSGetActiveConsoleSessionId();
        if (id is 0 or uint.MaxValue)
            throw new AuthenticationException("No interactive console session available.");
        return id;
    }

    internal static async Task RunSessionOnceAsync(CancellationToken stop)
    {
        M3ControlService.ValidateServiceIdentity();
        string executable = ResolveAgentPath();
        uint session = ActiveSession();
        if (!WTSQueryUserToken(session, out var userToken))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "No interactive user token.");
        using (userToken)
        {
            SecurityIdentifier logonSid = M3WindowsPipe.GetTokenLogonSid(userToken);
            string instance = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            using var server = M3WindowsPipe.CreateRestrictedServer(instance, logonSid);
            byte[] key = RandomNumberGenerator.GetBytes(M3Wire.KeyBytes);
            try
            {
                using var launched = Launch(executable, userToken, session, instance, key);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await server.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
                var peer = M3WindowsPipe.GetVerifiedKernelPeer(
                    server, launched.ProcessId, launched.SessionId);
                await M3AgentProof.AuthenticateParentAsync(server, key,
                    peer.Pid, peer.SessionId, launched.ProcessId,
                    launched.SessionId, timeout.Token).ConfigureAwait(false);
                // Subsequent messages remain heartbeat-only. A session change
                // or service stop cancels this loop and kills the agent.
                using var heartbeatDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                while (!stop.IsCancellationRequested)
                {
                    heartbeatDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                    var beat = await M3Wire.ReadAsync(server, heartbeatDeadline.Token)
                        .ConfigureAwait(false);
                    if (beat.Type != M3MessageType.Heartbeat ||
                        ActiveSession() != session)
                        throw new AuthenticationException("Agent session lost or unexpected command.");
                    heartbeatDeadline.CancelAfter(TimeSpan.FromSeconds(8));
                }
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    private static LaunchedAgent Launch(string executable, SafeAccessTokenHandle userToken,
        uint session, string instance, byte[] key)
    {
        var attrs = new SecurityAttributes {
            Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1
        };
        if (!CreatePipe(out var reader, out var writer, ref attrs, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Private key pipe creation failed.");
        using (reader)
        using (writer)
        {
            if (!SetHandleInformation(writer, HandleFlagInherit, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Pipe handle isolation failed.");
            IntPtr listBytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listBytes);
            if (listBytes == IntPtr.Zero)
                throw new Win32Exception("Attribute list size unavailable.");
            IntPtr list = Marshal.AllocHGlobal(listBytes);
            IntPtr handleArray = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(handleArray, reader.DangerousGetHandle());
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref listBytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Attribute list init failed.");
                try
                {
                    if (!UpdateProcThreadAttribute(list, 0,
                        new IntPtr(ProcThreadAttributeHandleList), handleArray,
                        new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Inherited handle whitelist failed.");
                    var startup = new StartupInfoEx {
                        StartupInfo = new StartupInfo {
                            Size = Marshal.SizeOf<StartupInfoEx>(),
                            Desktop = @"winsta0\default"
                        },
                        AttributeList = list
                    };
                    string cmd = "\"" + executable + "\" --pipe " + instance +
                                 " --key-handle " +
                                 unchecked((ulong)reader.DangerousGetHandle().ToInt64())
                                    .ToString("X", CultureInfo.InvariantCulture) +
                                 " --service-pid " +
                                 ((uint)Environment.ProcessId).ToString(CultureInfo.InvariantCulture);
                    if (!CreateProcessAsUser(userToken, executable, cmd, IntPtr.Zero,
                        IntPtr.Zero, true, ExtendedStartupInfoPresent | CreateNoWindow,
                        IntPtr.Zero, Path.GetDirectoryName(executable), ref startup,
                        out ProcessInformation proc))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Agent process launch failed.");
                    CloseHandle(proc.Thread);
                    var launched = new LaunchedAgent(proc.Process, proc.ProcessId, session);
                    try
                    {
                        using var outbound = new FileStream(writer, FileAccess.Write, 32,
                            isAsync: false);
                        outbound.Write(key, 0, key.Length);
                        outbound.Flush();
                        return launched;
                    }
                    catch { launched.Dispose(); throw; }
                }
                finally { DeleteProcThreadAttributeList(list); }
            }
            finally
            {
                Marshal.FreeHGlobal(handleArray);
                Marshal.FreeHGlobal(list);
            }
        }
    }
}
