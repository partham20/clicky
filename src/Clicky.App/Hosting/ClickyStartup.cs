using Clicky.App.Views.Home;
using Clicky.Core.Clickys;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Opens clickys.db in the background at startup (migrating it and marking runs cut off by the last
/// exit as interrupted), tells the user when a Clicky finishes while they're not looking at it, and
/// stops running Clickys on shutdown.
/// </summary>
internal sealed class ClickyStartup(ClickyService clickys, HomeViewState home, IUserNotifier notifier, ILogger<ClickyStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        clickys.RunFinished += OnRunFinished;
        _ = Task.Run(InitializeLoggedAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        clickys.RunFinished -= OnRunFinished;
        await clickys.DisposeAsync();
    }

    private async Task InitializeLoggedAsync()
    {
        try
        {
            await clickys.InitializeAsync();
            logger.LogInformation("Clickys database ready at {Path}", clickys.DatabasePath);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Home shows the same error when opened.
            logger.LogError(ex, "Could not open the Clickys database");
        }
    }

    private void OnRunFinished(object? sender, ClickyRunFinishedEventArgs e)
    {
        if (e.Outcome.Status is not (ClickyRunStatus.Completed or ClickyRunStatus.Failed) || home.Watching == e.Clicky.Id)
        {
            return;
        }

        var failed = e.Outcome.Status == ClickyRunStatus.Failed || !e.Outcome.Succeeded;
        notifier.Notify(e.Clicky.Name, failed ? "Couldn't finish: " + e.Outcome.Summary : e.Outcome.Summary, warning: e.Outcome.Status == ClickyRunStatus.Failed);
    }
}
