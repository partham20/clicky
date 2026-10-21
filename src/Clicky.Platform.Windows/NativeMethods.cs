using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

internal static partial class NativeMethods
{
    internal const int AsfwAny = -1;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int dwProcessId);
}
