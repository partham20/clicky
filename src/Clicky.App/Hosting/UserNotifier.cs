using System.Windows.Threading;
using Clicky.App.Tray;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>Tells the user about something that needs them, as a Windows notification from the tray icon.</summary>
public interface IUserNotifier
{
    /// <summary>
    /// Shows a notification. Safe from any thread. The same message is not repeated within
    /// <see cref="UserNotifier.RepeatInterval"/>, so a broken mic does not notify on every Talk.
    /// </summary>
    void Notify(string title, string message, bool warning = false);
}

internal sealed class UserNotifier(TrayIconService tray, Dispatcher dispatcher, ILogger<UserNotifier> logger) : IUserNotifier
{
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _lastShown = new(StringComparer.Ordinal);

    public void Notify(string title, string message, bool warning = false)
    {
        var key = title + "\n" + message;
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastShown.TryGetValue(key, out var last) && now - last < RepeatInterval.TotalMilliseconds)
            {
                return;
            }

            _lastShown[key] = now;
        }

        logger.LogInformation("Notification: {Title}: {Message}", title, message);
        dispatcher.InvokeAsync(() => tray.Notify(title, message, warning));
    }
}
