// SPDX-License-Identifier: GPL-2.0-or-later
// Win32 current-user SUBPROCESS acceptance for M3 inherited-key Agent IPC.
// No SCM, LocalSystem, remote listener, screen capture, input or installer.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.CompilerServices;
using System.Text;
using TJ.RemoteAccess.M3;
using Xunit;

public sealed class M3AgentExternalProcessTests
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint HandleFlagInherit = 1;
    private const int HandleListAttribute = 0x00020002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write,
        ref SecurityAttributes attributes, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list,
        int count, uint flags, ref IntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags,
        IntPtr attribute, IntPtr value, IntPtr bytes, IntPtr previous, IntPtr output);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine,
        IntPtr processAttrs, IntPtr threadAttrs,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
        string? cwd, ref StartupInfoEx startup, out ProcessInfo process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr handle, uint code);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string FindCompleteAgentRuntime([CallerFilePath] string sourceFile = "")
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, ".."));
        string projectOutput = Path.Combine(root, "src", "TJRemoteAccess.Core", "bin",
            "Release", "net9.0-windows");
        foreach (string folder in new[] { projectOutput, AppContext.BaseDirectory })
        {
            string prefix = Path.Combine(folder, "TJ.RemoteAccess.M3.Agent");
            if (File.Exists(prefix + ".exe") && File.Exists(prefix + ".dll") &&
                File.Exists(prefix + ".runtimeconfig.json") &&
                File.Exists(prefix + ".deps.json"))
                return prefix + ".exe";
        }
        throw new FileNotFoundException(
            "The Agent's complete Release runtime (.exe, .dll, .deps.json, " +
            ".runtimeconfig.json) was not found. Build TJ.RemoteAccess.sln before testing.");
    }

    // Requires an actual unlocked, interactive user console. GitHub-hosted
    // Windows runners do not promise the desktop/logon-token environment
    // required by a real child Agent; never treat headless CI as acceptance.
    // The dedicated JJJ Dev interactive Windows test runs this test unchanged.
    [Fact]
    [Trait("Category", "InteractiveWindowsDesktop")]
    public async Task LaunchedAgentAuthenticatesUsingOnlyExplicitlyInheritedKeyHandle()
    {
        if (!OperatingSystem.IsWindows() || Process.GetCurrentProcess().SessionId == 0)
            return;
        // The test project references the Agent with ReferenceOutputAssembly=false.
        // Some dotnet test runners copy only its apphost into the TEST bin,
        // omitting .runtimeconfig.json/.deps.json. That executable exits
        // before joining the private pipe. Prefer the Agent project's
        // COMPLETE runtime folder, then any fully self-contained test copy.
        string agentExe = FindCompleteAgentRuntime();
        Assert.True(File.Exists(agentExe), "Agent project must build before IPC acceptance.");

        string instance = Guid.NewGuid().ToString("N");
        using var own = WindowsIdentity.GetCurrent();
        var sid = M3WindowsPipe.GetTokenLogonSid(own.AccessToken);
        using var server = M3WindowsPipe.CreateRestrictedServer(instance, sid);
        var sec = new SecurityAttributes {
            Size = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1
        };
        if (!CreatePipe(out var read, out var write, ref sec, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Test key pipe creation failed.");
        using (read)
        using (write)
        {
            Assert.True(SetHandleInformation(write, HandleFlagInherit, 0));
            byte[] key = RandomNumberGenerator.GetBytes(M3Wire.KeyBytes);
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            Assert.True(bytes != IntPtr.Zero);
            IntPtr list = Marshal.AllocHGlobal(bytes);
            IntPtr handleList = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr process = IntPtr.Zero;
            try
            {
                Marshal.WriteIntPtr(handleList, read.DangerousGetHandle());
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Test attributes failed.");
                try
                {
                    Assert.True(UpdateProcThreadAttribute(list, 0,
                        new IntPtr(HandleListAttribute), handleList,
                        new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero));
                    var startup = new StartupInfoEx {
                        Startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>() },
                        AttributeList = list
                    };
                    string cmd = "\"" + agentExe + "\" --pipe " + instance +
                        " --key-handle " + read.DangerousGetHandle().ToInt64().ToString("X") +
                        " --service-pid " + Environment.ProcessId;
                    if (!CreateProcess(agentExe, new StringBuilder(cmd),
                        IntPtr.Zero, IntPtr.Zero, true,
                        ExtendedStartupInfoPresent | CreateNoWindow,
                        IntPtr.Zero, Path.GetDirectoryName(agentExe),
                        ref startup, out ProcessInfo info))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Test Agent launch failed.");
                    process = info.Process;
                    CloseHandle(info.Thread);
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    Task connected = server.WaitForConnectionAsync(stop.Token);
                    using (var sender = new FileStream(write, FileAccess.Write, 32, isAsync: false))
                    {
                        sender.Write(key);
                        sender.Flush();
                    }
                    await connected;
                    uint pid = info.ProcessId;
                    uint session = (uint)Process.GetCurrentProcess().SessionId;
                    var peer = M3WindowsPipe.GetVerifiedKernelPeer(server, pid, session);
                    await M3AgentProof.AuthenticateParentAsync(server, key,
                        peer.Pid, peer.SessionId, pid, session, stop.Token);
                    var beat = await M3Wire.ReadAsync(server, stop.Token);
                    Assert.Equal(M3MessageType.Heartbeat, beat.Type);
                }
                finally { DeleteProcThreadAttributeList(list); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                if (process != IntPtr.Zero)
                {
                    TerminateProcess(process, 1);
                    CloseHandle(process);
                }
                Marshal.FreeHGlobal(handleList);
                Marshal.FreeHGlobal(list);
            }
        }
    }
}
