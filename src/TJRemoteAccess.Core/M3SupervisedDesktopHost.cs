// SPDX-License-Identifier: GPL-2.0-or-later
// Interactive, VISIBLE desktop Server child after BOTH service-operator policy
// and local-user enrollment. No UAC, Winlogon, Session-0 desktop or RFB bypass.
// The child is born SUSPENDED, assigned to a KILL_ON_JOB_CLOSE Windows Job,
// then resumed. Unexpected Agent termination therefore kills the Server too.
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TJ.RemoteAccess.M3;

internal sealed class M3SupervisedDesktopHost : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint KillOnJobClose = 0x00002000;
    private const uint StillActive = 259;
    private const int JobObjectExtendedLimitInformation = 9;

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
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job,
        int informationClass, ref ExtendedLimit info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string app, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags,
        IntPtr environment, string? currentDirectory,
        ref StartupInfo startup, out ProcessInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);

    private readonly SafeFileHandle _job;
    private readonly SafeFileHandle _process;
    private bool _disposed;

    private M3SupervisedDesktopHost(SafeFileHandle job, SafeFileHandle process)
    {
        _job = job;
        _process = process;
    }

    internal bool IsRunning =>
        !_disposed &&
        GetExitCodeProcess(_process, out uint status) &&
        status == StillActive;

    internal static bool IsExecutablePathEligible(string agentDirectory, string exePath)
    {
        if (string.IsNullOrWhiteSpace(agentDirectory) || string.IsNullOrWhiteSpace(exePath))
            return false;
        try
        {
            string expected = Path.GetFullPath(Path.Combine(agentDirectory,
                                                       "TJ.RemoteAccess.Server.exe"));
            return string.Equals(expected, Path.GetFullPath(exePath),
                                 StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Path.GetDirectoryName(expected)?.TrimEnd(Path.DirectorySeparatorChar),
                                 Path.GetFullPath(agentDirectory).TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
            PathTooLongException) { return false; }
    }

    // Compact Setup installs Server under the signed-in owner's LocalAppData,
    // while the privileged Service/Agent must stay in an admin-owned directory.
    // Never load the Service DLL/EXE from this user-writable application path.
    internal static bool IsInstalledProductServerPathEligible(string userLocalAppData,
                                                               string exePath)
    {
        if (string.IsNullOrWhiteSpace(userLocalAppData) ||
            string.IsNullOrWhiteSpace(exePath)) return false;
        try
        {
            string expected = Path.GetFullPath(Path.Combine(userLocalAppData,
                "Programs", "TJ.RemoteAccess", "Server", "TJ.RemoteAccess.Server.exe"));
            return string.Equals(expected, Path.GetFullPath(exePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
            PathTooLongException) { return false; }
    }

    internal static M3SupervisedDesktopHost? StartIfEnrolled()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive ||
            Process.GetCurrentProcess().SessionId == 0 ||
            !M3DesktopEnrollmentStore.IsEnabledForCurrentUser() ||
            !M3InteractiveDesktopGuard.IsNormalInteractiveDesktop())
            return null;

        string baseDirectory = AppContext.BaseDirectory;
        string exe = Path.GetFullPath(Path.Combine(baseDirectory, "TJ.RemoteAccess.Server.exe"));
        if (!File.Exists(exe))
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            exe = Path.GetFullPath(Path.Combine(local, "Programs", "TJ.RemoteAccess",
                "Server", "TJ.RemoteAccess.Server.exe"));
            if (!IsInstalledProductServerPathEligible(local, exe))
                throw new FileNotFoundException("Untrusted installed Server path.");
        }
        else if (!IsExecutablePathEligible(baseDirectory, exe))
            throw new FileNotFoundException("Server must be in the Agent directory.");
        if (!File.Exists(exe) ||
            (File.GetAttributes(exe) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException("Approved local Server executable missing.");
        SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Server Job creation failed.");
        }
        try
        {
            var limits = new ExtendedLimit {
                BasicLimitInformation = new BasicLimit { LimitFlags = KillOnJobClose }
            };
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                ref limits, (uint)Marshal.SizeOf<ExtendedLimit>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Job kill-on-close failed.");

            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var commandLine = new StringBuilder(
                "\"" + exe + "\" --allow-lan --auto-start");
            if (!CreateProcess(exe, commandLine, IntPtr.Zero, IntPtr.Zero,
                               false, CreateSuspended, IntPtr.Zero,
                               Path.GetDirectoryName(exe), ref startup, out ProcessInfo result))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Desktop Server start failed.");

            using var thread = new SafeFileHandle(result.Thread, ownsHandle: true);
            var child = new SafeFileHandle(result.Process, ownsHandle: true);
            try
            {
                if (!AssignProcessToJobObject(job, child))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                                             "Cannot protect Server child with Job.");
                if (ResumeThread(thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                                             "Desktop Server resume failed.");
                return new M3SupervisedDesktopHost(job, child);
            }
            catch
            {
                // If Job assignment failed, the process was created SUSPENDED
                // and would otherwise survive without an owner. Terminate it
                // even if the job was never assigned.
                TerminateProcess(child, 1);
                child.Dispose();
                throw;
            }
        }
        catch { job.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _job.Dispose(); // KILL_ON_JOB_CLOSE releases keys/screen/port even on Agent crash
        _process.Dispose();
    }
}
