using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Starts the routine schedule in the background at startup and stops it on shutdown (runs in
/// progress are cancelled and run again next time). Tells the user when a routine pauses itself, and
/// shows the result of a routine that belongs to no Clicky (one set up in Talk), which has no chat to land in.
/// </summary>
internal sealed class RoutineStartup(RoutineEngine engine, ISettingsStore settings, IUserNotifier notifier, ILogger<RoutineStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        engine.Paused += OnPaused;
        engine.RunFinished += OnRunFinished;
        _ = Task.Run(StartLoggedAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        engine.Paused -= OnPaused;
        engine.RunFinished -= OnRunFinished;
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

    // A Clicky's results land quietly in its chat; a routine that has stopped working is worth a notification.
    private void OnPaused(object? sender, RoutinePausedEventArgs e)
    {
        if (settings.Current.Routines.NotifyWhenPaused)
        {
            notifier.Notify($"Routine paused: {e.Routine.Name}", e.Reason, warning: true);
        }
    }

    // A notification, never spoken: the full answer is in the routine's History.
    private void OnRunFinished(object? sender, RoutineRunFinishedEventArgs e)
    {
        if (e.Routine.ClickyId is null && e.Status == RoutineRunStatus.Succeeded)
        {
            notifier.Notify(e.Routine.Name, Shorten(string.IsNullOrWhiteSpace(e.Output) ? e.Message : e.Output));
        }
    }

    private static string Shorten(string text)
    {
        const int MaxChars = 240;
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= MaxChars ? line : string.Concat(line.AsSpan(0, MaxChars - 1).TrimEnd(), "\u2026");
    }
}
