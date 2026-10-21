using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Tray;

public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IAppShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TrayViewModel> _logger;

    // Set while copying a settings value into the VM so the change handler does not write it back.
    private bool _syncing;

    public TrayViewModel(ISettingsStore settings, IAppShell shell, Dispatcher dispatcher, ILogger<TrayViewModel> logger)
    {
        _settings = settings;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;

        SyncFrom(settings.Current);
        settings.Changed += OnSettingsChanged;
    }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    [RelayCommand]
    private void OpenHome() => _shell.ShowHome();

    [RelayCommand]
    private void OpenSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void Quit() => _shell.Quit();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        _ = PersistStartWithWindowsAsync(value);
    }

    private async Task PersistStartWithWindowsAsync(bool value)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { General = s.General with { StartWithWindows = value } }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save start with Windows = {Value}", value);
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
        }
        finally
        {
            _syncing = false;
        }
    }
}
