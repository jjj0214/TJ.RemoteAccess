// SPDX-License-Identifier: GPL-2.0-or-later
// Windows-only local named-pipe ACL and kernel peer identity checks.
// Uses Win32 CreateNamedPipeW to enforce PIPE_REJECT_REMOTE_CLIENTS.
// No fallback to permissive named-pipe constructors or Everyone/Users ACL.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Principal;

namespace TJ.RemoteAccess.M3;

internal static class M3WindowsPipe
{
    private const uint PIPE_ACCESS_DUPLEX = 0x00000003;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_READMODE_BYTE = 0x00000000;
    private const uint PIPE_WAIT = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const uint SDDL_REVISION_1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptor(string sddl, uint revision,
                                            out IntPtr descriptor, out uint descriptorSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreatePipeNative(string name,
                       uint openMode, uint pipeMode, uint maxInstances,
                       uint outputBufferSize, uint inputBufferSize,
                       uint defaultTimeOut, ref SecurityAttributes attrs);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientProcessId",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPeerPid(SafePipeHandle handle, out uint pid);

    [DllImport("kernel32.dll", EntryPoint = "GetNamedPipeClientSessionId",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPeerSessionId(SafePipeHandle handle, out uint sessionId);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr hMemory);

    private const int TokenGroupsClass = 2;
    private const uint GroupLogonId = 0xC0000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidWithAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenGroupsOne
    {
        public uint Count;
        public SidWithAttributes First;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token, int informationClass, IntPtr buffer,
        int bufferLength, out int returnLength);

    // Read the SE_GROUP_LOGON_ID SID from the actual Windows access token.
    // WindowsIdentity.Groups is insufficient on some Windows environments.
    // The future privileged launcher must use its OWN target-agent token.
    internal static SecurityIdentifier GetTokenLogonSid(SafeAccessTokenHandle token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!GetTokenInformation(token, TokenGroupsClass, IntPtr.Zero, 0, out int size)
            && size <= 0)
            throw new AuthenticationException("Windows token groups query unavailable.");
        if (size < Marshal.SizeOf<TokenGroupsOne>() || size > 1024 * 1024)
            throw new AuthenticationException("Windows token group list invalid.");
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetTokenInformation(token, TokenGroupsClass, buffer, size, out int read) ||
                read > size)
                throw new AuthenticationException("Windows token group query failed.");
            uint count = (uint)Marshal.ReadInt32(buffer);
            int offset = (int)Marshal.OffsetOf<TokenGroupsOne>(nameof(TokenGroupsOne.First));
            int step = Marshal.SizeOf<SidWithAttributes>();
            if (count == 0 || count > (size - offset) / step)
                throw new AuthenticationException("Windows token groups malformed.");
            for (int i = 0; i < count; i++)
            {
                var group = Marshal.PtrToStructure<SidWithAttributes>(
                    IntPtr.Add(buffer, offset + i * step));
                if ((group.Attributes & GroupLogonId) == GroupLogonId)
                    return new SecurityIdentifier(group.Sid);
            }
            throw new AuthenticationException("Token has no interactive logon SID.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string PipeName(string instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName) || instanceName.Length > 60 ||
            instanceName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')))
            throw new ArgumentException("Invalid local M3 pipe instance name.", nameof(instanceName));
        return "TJ.RemoteAccess.M3." + instanceName;
    }

    internal static NamedPipeServerStream CreateRestrictedServer(
        string instanceName, SecurityIdentifier specificLogonSid)
    {
        ArgumentNullException.ThrowIfNull(specificLogonSid);
        string sid = specificLogonSid.Value;
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                sid, @"^S-1-5-5-[0-9]+-[0-9]+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new AuthenticationException(
                "Service must select an exact interactive LOGON SID, not a user account SID.");
        // LocalSystem has full access; only this exact interactive logon
        // session gets read/write. Same-account parallel logons are excluded.
        // No Everyone, BUILTIN Users, Authenticated Users or network identities.
        string dacl = "D:P(A;;GA;;;SY)(A;;GRGW;;;" + sid + ")";
        if (!ConvertSecurityDescriptor(dacl, SDDL_REVISION_1,
                                       out var sd, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Unable to create explicit pipe DACL.");
        try
        {
            var attrs = new SecurityAttributes {
                nLength = Marshal.SizeOf<SecurityAttributes>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0
            };
            var name = @"\\.\pipe\" + PipeName(instanceName);
            SafePipeHandle handle = CreatePipeNative(name,
                PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED,
                PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT |
                PIPE_REJECT_REMOTE_CLIENTS,
                1, 65536, 65536, 0, ref attrs);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, "Secure local pipe creation failed.");
            }
            try
            {
                return new NamedPipeServerStream(
                    PipeDirection.InOut, isAsync: true, isConnected: false, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(sd); }
    }

    internal static NamedPipeClientStream CreateLocalClient(string instanceName)
    {
        return new NamedPipeClientStream(".", PipeName(instanceName),
            PipeDirection.InOut, PipeOptions.Asynchronous);
    }

    internal static (uint Pid, uint SessionId) GetVerifiedKernelPeer(
        NamedPipeServerStream server, uint launchedAgentPid, uint expectedSessionId)
    {
        if (server is null || !server.IsConnected || launchedAgentPid == 0)
            throw new AuthenticationException("Agent pipe is not connected to a launched process.");
        if (!GetPeerPid(server.SafePipeHandle, out uint pid) ||
            !GetPeerSessionId(server.SafePipeHandle, out uint session) ||
            pid != launchedAgentPid || session != expectedSessionId)
            throw new AuthenticationException("Kernel-reported client PID/session does not match launched agent.");
        return (pid, session);
    }
}
