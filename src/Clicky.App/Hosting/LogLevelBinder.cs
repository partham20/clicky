using Clicky.Core.Settings;
using Microsoft.Extensions.Hosting;
using Serilog.Core;

namespace Clicky.App.Hosting;

/// <summary>Keeps the Serilog level in step with settings.logging.minimumLevel, live.</summary>
internal sealed class LogLevelBinder(ISettingsStore settings, LoggingLevelSwitch levelSwitch) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        levelSwitch.MinimumLevel = settings.Current.Logging.MinimumLevel.ToSerilog();
        settings.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, SettingsChangedEventArgs e) =>
        levelSwitch.MinimumLevel = e.Current.Logging.MinimumLevel.ToSerilog();
}
