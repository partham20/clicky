using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

// Waitable timers for the pointer sampler.
internal static partial class NativeMethods
{
    /// <summary>Windows 10 1803+: wakes on time instead of the next 15.6 ms scheduler tick.</summary>
    internal const uint CreateWaitableTimerHighResolution = 0x00000002;
    internal const uint TimerAllAccess = 0x001F0003;
    internal const uint Infinite = 0xFFFFFFFF;
    internal const uint WaitObject0 = 0;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWaitableTimerEx(nint timerAttributes, string? timerName, uint flags, uint desiredAccess);

    /// <param name="dueTime">Negative: relative, in 100 ns units.</param>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWaitableTimer(nint timer, in long dueTime, int period, nint completionRoutine, nint completionArg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint handle, uint milliseconds);
}
