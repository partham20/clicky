using Clicky.Core.Hotkeys;
using Clicky.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Hosting;

/// <summary>Starts the keyboard hook and keeps its bindings in step with settings.hotkeys, live.</summary>
internal sealed class HotkeySettingsBinder(ISettingsStore settings, IHotkeyService hotkeys, ILogger<HotkeySettingsBinder> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            hotkeys.Start();
        }
        catch (InvalidOperationException ex)
        {
            // Rare (hook limit reached, broken session). Clicky stays usable from the tray.
            logger.LogError(ex, "Could not install the keyboard hook; hotkeys are disabled until restart");
            return Task.CompletedTask;
        }

        Apply(settings.Current.Hotkeys);
        settings.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Hotkeys != e.Current.Hotkeys)
        {
            Apply(e.Current.Hotkeys);
        }
    }

    private void Apply(HotkeySettings hotkeySettings)
    {
        var bindings = hotkeySettings.ToBindings(out var problems);
        foreach (var problem in problems)
        {
            logger.LogWarning("Hotkey setting skipped: {Problem}", problem);
        }

        hotkeys.UpdateBindings(bindings);
    }
}
