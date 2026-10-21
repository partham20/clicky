using System.Runtime.InteropServices;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Input;

/// <summary>How long since the user last touched the keyboard or mouse, in this session.</summary>
public static class UserIdle
{
    /// <summary>Zero if it can't be read, so a failure never hides the user.</summary>
    public static TimeSpan Get()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Unsigned subtraction in 32 bits handles the tick count wrapping around.
        var idleMs = unchecked((uint)Environment.TickCount64 - info.Time);
        return TimeSpan.FromMilliseconds(idleMs);
    }
}
