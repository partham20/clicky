using System.Runtime.InteropServices;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Input;

/// <summary>
/// UIPI: a non-elevated process gets no low-level keyboard events (and cannot inject input) while
/// an elevated window has focus. Used to explain hotkeys that "stop working" there.
/// </summary>
public static class ProcessElevation
{
    public static bool IsCurrentProcessElevated { get; } = IsElevated((uint)Environment.ProcessId, out _);

    /// <summary>
    /// The process that owns <paramref name="hwnd"/>: whether it runs at a higher integrity level than
    /// we can talk to, and its executable name for logs. Access denied counts as elevated, since
    /// that is what a medium-integrity process sees for admin and system processes.
    /// </summary>
    public static bool IsWindowElevated(nint hwnd, out string processName)
    {
        processName = "?";
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid == 0)
        {
            return false;
        }

        return IsElevated(pid, out processName);
    }

    public static unsafe string GetWindowTitle(nint hwnd)
    {
        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        var length = GetWindowText(hwnd, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static unsafe bool IsElevated(uint pid, out string processName)
    {
        processName = $"pid {pid}";
        var process = OpenProcess(ProcessQueryLimitedInformation, bInheritHandle: false, pid);
        if (process == 0)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
        }

        try
        {
            var path = stackalloc char[1024];
            var size = 1024u;
            if (QueryFullProcessImageName(process, 0, path, ref size))
            {
                processName = Path.GetFileName(new string(path, 0, (int)size));
            }

            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied;
            }

            try
            {
                uint elevated = 0;
                return GetTokenInformation(token, TokenElevationClass, &elevated, sizeof(uint), out _) && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
