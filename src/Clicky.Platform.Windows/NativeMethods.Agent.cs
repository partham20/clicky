using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

// Computer use (M13): window hit-testing, the pointer, and mouse/Unicode input injection.
internal static partial class NativeMethods
{
    internal const uint InputMouse = 0;

    internal const uint MouseeventfLeftDown = 0x0002;
    internal const uint MouseeventfLeftUp = 0x0004;
    internal const uint MouseeventfRightDown = 0x0008;
    internal const uint MouseeventfRightUp = 0x0010;
    internal const uint MouseeventfMiddleDown = 0x0020;
    internal const uint MouseeventfMiddleUp = 0x0040;
    internal const uint MouseeventfWheel = 0x0800;
    internal const uint MouseeventfHWheel = 0x1000;

    internal const int WheelDelta = 120;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hWnd, out Rect lpRect);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDoubleClickTime();

    internal static unsafe string ClassName(nint hwnd)
    {
        const int capacity = 128;
        var buffer = stackalloc char[capacity];
        var length = GetClassName(hwnd, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    internal static uint ProcessIdOf(nint hwnd) =>
        hwnd != 0 && GetWindowThreadProcessId(hwnd, out var pid) != 0 ? pid : 0;
}
