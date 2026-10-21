using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core;
using Clicky.Core.Settings;
using Clicky.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Updates and support section of Settings: version and update state, Check for updates, the
/// Update button once a new version is downloaded, the settings.updates switches, and Report a bug.
/// </summary>
internal sealed class UpdatesSettingsView : ContentControl
{
    private readonly UpdateService _updates;
    private readonly ISettingsStore _settings;
    private readonly IAppShell _shell;
    private readonly ClickyPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<UpdatesSettingsView> _logger;

    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Height = 6, Maximum = 100, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _check = new() { Content = "Check for updates", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _update = new() { Content = "Update and restart", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), FontWeight = FontWeights.SemiBold };
    private readonly CheckBox _autoCheck = new() { Content = "Check for updates automatically" };
    private readonly CheckBox _autoDownload = new() { Content = "Download updates in the background (Update only restarts)" };
    private bool _syncing;

    public UpdatesSettingsView(
        UpdateService updates,
        ISettingsStore settings,
        IAppShell shell,
        ClickyPaths paths,
        Dispatcher dispatcher,
        ILogger<UpdatesSettingsView> logger)
    {
        _updates = updates;
        _settings = settings;
        _shell = shell;
        _paths = paths;
        _dispatcher = dispatcher;
        _logger = logger;

        _check.Click += async (_, _) => await _updates.CheckNowAsync();
        _update.Click += (_, _) => _shell.RestartToUpdate();
        _autoCheck.Click += (_, _) => SaveSettings(u => u with { AutoCheck = _autoCheck.IsChecked == true });
        _autoDownload.Click += (_, _) => SaveSettings(u => u with { AutoDownload = _autoDownload.IsChecked == true });

        Content = Build();
        Loaded += (_, _) =>
        {
            _updates.StatusChanged += OnStatusChanged;
            _settings.Changed += OnSettingsChanged;
            Show(_updates.Status);
            SyncSettings();
        };
        Unloaded += (_, _) =>
        {
            _updates.StatusChanged -= OnStatusChanged;
            _settings.Changed -= OnSettingsChanged;
        };
    }

    private StackPanel Build()
    {
        var reportBug = new Button { Content = "Report a bug…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };
        reportBug.Click += (_, _) => _shell.ShowBugReport();
        var openLogs = new Button { Content = "Open logs folder", Padding = new Thickness(10, 2, 10, 2) };
        openLogs.Click += (_, _) => OpenLogs();

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Updates and support", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(_state);
        panel.Children.Add(_progress);

        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        updateRow.Children.Add(_update);
        updateRow.Children.Add(_check);
        panel.Children.Add(updateRow);
        panel.Children.Add(_autoCheck);
        panel.Children.Add(_autoDownload);

        var supportRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        supportRow.Children.Add(reportBug);
        supportRow.Children.Add(openLogs);
        panel.Children.Add(supportRow);
        return panel;
    }

    private void OnStatusChanged(object? sender, UpdateStatus status) => _dispatcher.InvokeAsync(() => Show(status));

    private void Show(UpdateStatus status)
    {
        _state.Text = status.Describe();
        _progress.Visibility = status.Phase == UpdatePhase.Downloading ? Visibility.Visible : Visibility.Collapsed;
        _progress.Value = status.Progress;
        _update.Visibility = status.Phase == UpdatePhase.ReadyToRestart ? Visibility.Visible : Visibility.Collapsed;
        _update.Content = $"Update to {status.AvailableVersion} and restart";
        _check.Content = status.Phase == UpdatePhase.Available ? "Download update" : "Check for updates";
        _check.IsEnabled = status.Phase is not (UpdatePhase.NotInstalled or UpdatePhase.ReadyToRestart) && !status.IsBusy;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Updates != e.Current.Updates)
        {
            _dispatcher.InvokeAsync(SyncSettings);
        }
    }

    private void SyncSettings()
    {
        var updates = _settings.Current.Updates;
        _syncing = true;
        try
        {
            _autoCheck.IsChecked = updates.AutoCheck;
            _autoDownload.IsChecked = updates.AutoDownload;
            _autoDownload.IsEnabled = updates.AutoCheck;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SaveSettings(Func<UpdateSettings, UpdateSettings> change)
    {
        if (!_syncing)
        {
            _ = SaveSettingsAsync(change);
        }
    }

    private async Task SaveSettingsAsync(Func<UpdateSettings, UpdateSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Updates = change(s.Updates) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save update settings");
        }

        await _dispatcher.InvokeAsync(SyncSettings);
    }

    private void OpenLogs()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.LogsDirectory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open {Folder}", _paths.LogsDirectory);
        }
    }
}
