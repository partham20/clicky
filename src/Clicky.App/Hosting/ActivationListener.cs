using System.Windows.Threading;
using Clicky.Platform.Windows.SingleInstance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>Brings Home forward when the user launches Clicky again while it is already running.</summary>
internal sealed class ActivationListener(
    SingleInstanceGuard instance,
    IAppShell shell,
    Dispatcher dispatcher,
    ILogger<ActivationListener> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        instance.ListenAsync(OnMessageAsync, logger, stoppingToken);

    private async Task OnMessageAsync(string message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Second launch sent {Message}", message);
        if (string.Equals(message, SingleInstanceGuard.ShowMessage, StringComparison.Ordinal))
        {
            await dispatcher.InvokeAsync(shell.ShowHome, DispatcherPriority.Normal, cancellationToken);
        }
    }
}
