using System.Runtime.InteropServices;

namespace Clicky.Platform.Windows;

// Mouse hook, accessibility events and MSAA for the walkthrough step watcher (M8).
internal static partial class NativeMethods
{
    internal const int WhMouseLl = 14;

    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;

    internal const uint EventSystemMenuPopupStart = 0x0006;
    internal const uint EventObjectFocus = 0x8005;
    internal const uint EventObjectSelection = 0x8006;
    internal const uint EventObjectInvoked = 0x8013;

    internal const int ObjidWindow = 0;
    internal const int ObjidCaret = -8;
    internal const int ObjidCursor = -9;
    internal const int ChildidSelf = 0;

    internal const uint GaRoot = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MsLlHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    /// <summary>
    /// MSAA's IAccessible, declared in vtable order (a dual interface: IDispatch's four methods come
    /// first, from the runtime). Only <see cref="get_accName"/> and <see cref="accLocation"/> are called;
    /// the rest are here to keep the slots right.
    /// </summary>
    [ComImport]
    [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    internal interface IAccessible
    {
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accParent();

        int get_accChildCount();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object get_accChild([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accName([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accValue([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDescription([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accRole([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accState([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accHelp([MarshalAs(UnmanagedType.Struct)] object varChild);

        int get_accHelpTopic([MarshalAs(UnmanagedType.BStr)] out string pszHelpFile, [MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accKeyboardShortcut([MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accFocus();

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_accSelection();

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_accDefaultAction([MarshalAs(UnmanagedType.Struct)] object varChild);

        void accSelect(int flagsSelect, [MarshalAs(UnmanagedType.Struct)] object varChild);

        void accLocation(out int pxLeft, out int pyTop, out int pcxWidth, out int pcyHeight, [MarshalAs(UnmanagedType.Struct)] object varChild);

        [return: MarshalAs(UnmanagedType.Struct)]
        object accNavigate(int navDir, [MarshalAs(UnmanagedType.Struct)] object varStart);

        [return: MarshalAs(UnmanagedType.Struct)]
        object accHitTest(int xLeft, int yTop);

        void accDoDefaultAction([MarshalAs(UnmanagedType.Struct)] object varChild);

        void set_accName([MarshalAs(UnmanagedType.Struct)] object varChild, [MarshalAs(UnmanagedType.BStr)] string pszName);

        void set_accValue([MarshalAs(UnmanagedType.Struct)] object varChild, [MarshalAs(UnmanagedType.BStr)] string pszValue);
    }

    [LibraryImport("user32.dll")]
    internal static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint gaFlags);

    // IAccessible needs the built-in COM marshaller, which LibraryImport doesn't use.
#pragma warning disable SYSLIB1054
    [DllImport("oleacc.dll", PreserveSig = true)]
    internal static extern int AccessibleObjectFromEvent(
        nint hwnd,
        int dwObjectId,
        int dwChildId,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? ppacc,
        [MarshalAs(UnmanagedType.Struct)] out object? pvarChild);

    [DllImport("oleacc.dll", PreserveSig = true)]
    internal static extern int AccessibleObjectFromPoint(
        Point ptScreen,
        [MarshalAs(UnmanagedType.Interface)] out IAccessible? ppacc,
        [MarshalAs(UnmanagedType.Struct)] out object? pvarChild);
#pragma warning restore SYSLIB1054
}
