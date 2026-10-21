using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

// Time since the last input, for the suggestions card (M17).
internal static partial class NativeMethods
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LastInputInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct LastInputInfo
    {
        public uint Size;

        /// <summary>GetTickCount at the last input: 32 bits, so it wraps every 49.7 days.</summary>
        public uint Time;
    }
}
