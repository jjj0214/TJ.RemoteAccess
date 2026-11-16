// SPDX-License-Identifier: GPL-2.0-or-later
// Fail closed on locked Windows desktops, Winlogon and secure UAC desktop.
// No SetThreadDesktop, privilege escalation or secure-desktop switching.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TJ.RemoteAccess.M3;

internal static class M3InteractiveDesktopGuard
{
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr desktop, int info,
        StringBuilder buffer, uint length, out uint required);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    internal static bool AllowedDesktopName(string? name) =>
        string.Equals(name, "Default", StringComparison.Ordinal);

    internal static bool IsNormalInteractiveDesktop()
    {
        if (!OperatingSystem.IsWindows() || !Environment.UserInteractive ||
            Process.GetCurrentProcess().SessionId == 0)
            return false;
        IntPtr desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new StringBuilder(capacity: 96);
            if (!GetUserObjectInformation(desktop, UoiName, name,
                                          (uint)(name.Capacity * sizeof(char)), out _))
                return false;
            return AllowedDesktopName(name.ToString());
        }
        finally { CloseDesktop(desktop); }
    }
}
