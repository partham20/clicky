using System.Diagnostics;
using Clicky.Core.Connectors;

namespace Clicky.Platform.Windows.Shell;

/// <summary>Opens web pages in the default browser through the shell.</summary>
public sealed class ShellBrowserLauncher : IBrowserLauncher
{
    public void Open(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Only web addresses are opened.", nameof(uri));
        }

        // A tray app has no foreground rights; let the browser take focus so the sign-in page
        // comes to the front instead of flashing in the taskbar.
        ForegroundRights.GrantToAnyProcess();
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }
}
