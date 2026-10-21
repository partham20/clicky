namespace Clicky.Platform.Windows;

public static class ForegroundRights
{
    /// <summary>
    /// The foreground lock stops a background process from stealing focus, so the primary instance
    /// cannot bring its window forward on its own. The process the user just launched does hold
    /// foreground rights; calling this before signalling lets the primary use them.
    /// </summary>
    public static void GrantToAnyProcess() => NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);
}
