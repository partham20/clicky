using Clicky.Core.Connectors;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// Connects the configured MCP connectors in the background at startup and closes them (stopping
/// stdio server processes) on shutdown.
/// </summary>
internal sealed class ConnectorStartup(ConnectorHub hub, ILogger<ConnectorStartup> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Connectors: {Count} configured", hub.Connectors.Count);
        _ = Task.Run(() => StartLoggedAsync(), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await hub.DisposeAsync();

    private async Task StartLoggedAsync()
    {
        try
        {
            await hub.StartAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Could not start connectors");
        }
    }
}
