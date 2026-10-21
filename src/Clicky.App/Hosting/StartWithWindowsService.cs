using Clicky.Core.Settings;
using Clicky.Platform.Windows.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>
/// settings.general.startWithWindows is the source of truth, except that a user who disabled
/// Clicky in Task Manager wins: we pull that back into settings instead of silently re-enabling.
/// </summary>
internal sealed class StartWithWindowsService(
    ISettingsStore settings,
    StartupRegistration registration,
    ILogger<StartWithWindowsService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        settings.Changed += OnChanged;

        var wanted = settings.Current.General.StartWithWindows;
        var state = Safe(registration.GetState, StartupState.NotRegistered);

        if (wanted && state == StartupState.DisabledByUser)
        {
            logger.LogInformation("Start with Windows was disabled in Task Manager; updating settings to match");
            await settings.UpdateAsync(s => s with { General = s.General with { StartWithWindows = false } }, cancellationToken);
            return;
        }

        Apply(wanted);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.General.StartWithWindows != e.Current.General.StartWithWindows)
        {
            Apply(e.Current.General.StartWithWindows);
        }
    }

    private void Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                if (registration.GetState() != StartupState.Enabled || registration.IsRegisteredWithDifferentCommand())
                {
                    registration.Enable();
                    logger.LogInformation("Registered start with Windows: {Command}", registration.LaunchCommand);
                }
            }
            else if (registration.GetState() != StartupState.NotRegistered)
            {
                registration.Disable();
                logger.LogInformation("Removed start with Windows");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Group Policy can lock HKCU\...\Run. Log it; the toggle will show the setting, not the effect.
            logger.LogWarning(ex, "Could not update start with Windows registration");
        }
    }

    private T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            logger.LogWarning(ex, "Could not read start with Windows registration");
            return fallback;
        }
    }
}
