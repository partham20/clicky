using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Buddy;
using Clicky.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Tray;

public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IAppShell _shell;
    private readonly IBuddyPresence _presence;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TrayViewModel> _logger;

    // Set while copying a settings value into the VM so the change handler does not write it back.
    private bool _syncing;

    public TrayViewModel(ISettingsStore settings, IAppShell shell, IBuddyPresence presence, Dispatcher dispatcher, ILogger<TrayViewModel> logger)
    {
        _settings = settings;
        _shell = shell;
        _presence = presence;
        _dispatcher = dispatcher;
        _logger = logger;

        SyncFrom(settings.Current);
        settings.Changed += OnSettingsChanged;
    }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool HideFloatingCursor { get; set; }

    public BuddyState BuddyState => _presence.State;

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    [RelayCommand]
    private void OpenHome() => _shell.ShowHome();

    [RelayCommand]
    private void OpenSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void Quit() => _shell.Quit();

    // Preview only: lets the states be checked before voice exists. M6 drives them for real.
    [RelayCommand]
    private void SetBuddyState(BuddyState state) => _presence.SetState(state);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_syncing)
        {
            _ = PersistAsync("start with Windows", value, s => s with { General = s.General with { StartWithWindows = value } });
        }
    }

    partial void OnHideFloatingCursorChanged(bool value)
    {
        if (!_syncing)
        {
            _ = PersistAsync("hide floating cursor", value, s => s with { Overlay = s.Overlay with { HideFloatingCursor = value } });
        }
    }

    private async Task PersistAsync(string setting, bool value, Func<ClickySettings, ClickySettings> mutate)
    {
        try
        {
            await _settings.UpdateAsync(mutate).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save {Setting} = {Value}", setting, value);
            await _dispatcher.InvokeAsync(() => SyncFrom(_settings.Current));
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) =>
        _dispatcher.InvokeAsync(() => SyncFrom(e.Current));

    private void SyncFrom(ClickySettings settings)
    {
        _syncing = true;
        try
        {
            StartWithWindows = settings.General.StartWithWindows;
            HideFloatingCursor = settings.Overlay.HideFloatingCursor;
        }
        finally
        {
            _syncing = false;
        }
    }
}
