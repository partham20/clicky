namespace Clicky.Platform.Windows;

public static class ForegroundRights
{
    /// <summary>
    /// The foreground lock stops a background process from stealing focus, so the primary instance
    /// cannot bring its window forward on its own. The process the user just launched does hold
    /// foreground rights; calling this before signalling lets the primary use them.
    /// </summary>
    public static void GrantToAnyProcess() => NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);

    /// <summary>The window in front right now (0 if none), e.g. to remember where the user was before Clicky's box opened.</summary>
    public static nint Foreground => NativeMethods.GetForegroundWindow();

    /// <summary>True if <paramref name="hwnd"/> belongs to this process (one of Clicky's own windows).</summary>
    public static bool IsOwnWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var process);
        return process == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// Brings one of our windows to the front with keyboard focus, from a hotkey. The low-level
    /// hook doesn't count as the user's input for the foreground lock, so if a plain
    /// SetForegroundWindow is refused, attach to the foreground thread's input for the call (the
    /// usual way around it, and harmless: it is detached again at once).
    /// </summary>
    /// <returns>True if the window is now in front.</returns>
    public static bool Activate(nint hwnd)
    {
        if (hwnd == 0)
        {
            return false;
        }

        if (NativeMethods.SetForegroundWindow(hwnd) && NativeMethods.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var ourThread = NativeMethods.GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != ourThread && NativeMethods.AttachThreadInput(ourThread, foregroundThread, true);
        try
        {
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(ourThread, foregroundThread, false);
            }
        }

        return NativeMethods.GetForegroundWindow() == hwnd;
    }

    /// <summary>Gives focus back to the window that had it before ours (we are in front, so this is allowed).</summary>
    public static void Restore(nint hwnd)
    {
        if (hwnd != 0 && NativeMethods.IsWindow(hwnd))
        {
            NativeMethods.SetForegroundWindow(hwnd);
        }
    }
}
