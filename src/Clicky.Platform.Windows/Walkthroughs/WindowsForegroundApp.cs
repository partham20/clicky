using Clicky.Core.Skills;
using Clicky.Platform.Windows.Input;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Walkthroughs;

/// <summary>The app that owns the foreground window, by executable name and title.</summary>
public sealed class WindowsForegroundApp : IForegroundApp
{
    public ForegroundApp? Current()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == 0 ? null : Describe(hwnd);
    }

    /// <summary>The app that owns <paramref name="hwnd"/>; the name is "?" when its process can't be opened (elevated).</summary>
    internal static ForegroundApp Describe(nint hwnd)
    {
        try
        {
            ProcessElevation.IsWindowElevated(hwnd, out var name);
            if (name.StartsWith("pid ", StringComparison.Ordinal) || name == "?")
            {
                name = "?";
            }

            return new ForegroundApp(name, ProcessElevation.GetWindowTitle(hwnd));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new ForegroundApp("?", string.Empty);
        }
    }
}
