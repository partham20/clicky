using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

// Typing, pasting and the clipboard, for dictation.
internal static partial class NativeMethods
{
    internal const uint KeyeventfUnicode = 0x0004;

    internal const ushort VkShift = 0x10;
    internal const ushort VkControl = 0x11;
    internal const ushort VkMenu = 0x12;
    internal const ushort VkInsert = 0x2D;
    internal const ushort VkV = 0x56;
    internal const ushort VkLWin = 0x5B;
    internal const ushort VkRWin = 0x5C;

    internal const uint CfUnicodeText = 13;
    internal const uint GmemMoveable = 0x0002;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenClipboard(nint hWndNewOwner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint EnumClipboardFormats(uint format);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetClipboardData(uint uFormat);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetClipboardData(uint uFormat, nint hMem);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterClipboardFormat(string lpszFormat);

    [LibraryImport("user32.dll")]
    internal static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", EntryPoint = "VkKeyScanExW")]
    internal static partial short VkKeyScanEx(ushort ch, nint dwhkl);

    [LibraryImport("user32.dll")]
    internal static partial nint GetKeyboardLayout(uint idThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint hMem);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint hMem);
}
