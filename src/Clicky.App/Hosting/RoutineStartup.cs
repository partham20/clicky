using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Starts the routine schedule in the background at startup and stops it on shutdown (runs in
/// progress are cancelled and run again next time). Tells the user when a routine pauses itself.
/// </summary>
internal sealed class RoutineStartup(RoutineEngine engine, ISettingsStore settings, IUserNotifier notifier, ILogger<RoutineStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.Paused += OnPaused;
        _ = Task.Run(StartLoggedAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        engine.Paused -= OnPaused;
        await engine.StopAsync();
    }

    private async Task StartLoggedAsync()
    {
        try
        {
            await engine.StartAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Could not start routines");
            notifier.Notify("Routines couldn't start", ex.Message, warning: true);
        }
    }

    // Results land quietly in the Clicky's chat; only a routine that has stopped working is worth a notification.
    private void OnPaused(object? sender, RoutinePausedEventArgs e)
    {
        if (settings.Current.Routines.NotifyWhenPaused)
        {
            notifier.Notify($"Routine paused: {e.Routine.Name}", e.Reason, warning: true);
        }
    }
}
