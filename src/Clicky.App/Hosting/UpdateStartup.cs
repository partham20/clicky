using Clicky.Core.Updates;
using Microsoft.Extensions.Hosting;

namespace Clicky.App.Hosting;

/// <summary>Starts the update schedule and says once per version when an update is ready.</summary>
internal sealed class UpdateStartup(UpdateService updates, IUserNotifier notifier) : IHostedService
{
    private string? _announced;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        updates.StatusChanged += OnStatusChanged;
        updates.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        updates.StatusChanged -= OnStatusChanged;
        return Task.CompletedTask;
    }

    private void OnStatusChanged(object? sender, UpdateStatus status)
    {
        if (status.Phase != UpdatePhase.ReadyToRestart || status.AvailableVersion == _announced)
        {
            return;
        }

        _announced = status.AvailableVersion;
        notifier.Notify(
            "Clicky update ready",
            $"Version {status.AvailableVersion} is downloaded. Choose Update in the tray menu to restart into it, or it installs the next time Clicky starts.");
    }
}
