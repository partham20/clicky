using System.Windows.Threading;
using Clicky.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Opens onboarding at startup until it has been finished or closed once (settings.onboarding),
/// after the tray icon and buddy are up so the tutorial has something to point at.
/// </summary>
internal sealed class OnboardingStartup(ISettingsStore settings, IAppShell shell, Dispatcher dispatcher, ILogger<OnboardingStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (settings.Current.Onboarding.ShouldShow)
        {
            logger.LogInformation("First run (or a new tutorial): opening onboarding");

            // Queued now, run once the dispatcher loop starts and the startup work has settled.
            dispatcher.InvokeAsync(shell.ShowOnboarding, DispatcherPriority.ApplicationIdle, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
