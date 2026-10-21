using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Input;

/// <summary>Spots a drag and drop in progress anywhere on the desktop, so the buddy can offer to take the files.</summary>
public static class DragWatcher
{
    private const int VkRButton = 0x02;

    /// <summary>
    /// True while the user drags with the shell's drag image: Explorer, the desktop, browsers'
    /// downloads, Outlook attachments and most apps that drag files. The image is a visible window
    /// of class SysDragImage that exists only during such a drag; a mouse button must also be down
    /// so a stray leftover window doesn't count. Cheap (two calls per window), fine to poll a few
    /// times a second.
    /// </summary>
    public static bool IsShellDragInProgress()
    {
        // Physical buttons, so swapped buttons don't matter: a right-drag works in Explorer too.
        if (GetAsyncKeyState(VkLButton) >= 0 && GetAsyncKeyState(VkRButton) >= 0)
        {
            return false;
        }

        nint window = 0;
        while ((window = FindWindowEx(0, window, "SysDragImage", null)) != 0)
        {
            if (IsWindowVisible(window))
            {
                return true;
            }
        }

        return false;
    }
}
