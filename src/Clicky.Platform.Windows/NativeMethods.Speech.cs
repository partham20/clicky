using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

internal static partial class NativeMethods
{
    // SHQueryUserNotificationState results (QUERY_USER_NOTIFICATION_STATE).
    internal const int QunsNotPresent = 1;
    internal const int QunsBusy = 2;
    internal const int QunsRunningD3dFullScreen = 3;
    internal const int QunsPresentationMode = 4;
    internal const int QunsAcceptsNotifications = 5;
    internal const int QunsQuietTime = 6;
    internal const int QunsApp = 7;

    internal const int DwmwaCloaked = 14;

    /// <summary>
    /// WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED: the Focus Assist profile (0 off, 1 priority
    /// only, 2 alarms only). Undocumented, but it is what the shell itself publishes.
    /// </summary>
    internal const ulong WnfShelQuietHoursActiveProfileChanged = 0x0D83063EA3BF1C75;

    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out int state);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryWnfStateData(in ulong stateName, nint typeId, nint explicitScope, out uint changeStamp, out uint buffer, ref uint bufferSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    internal static unsafe partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);
}
