using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Buddy;
using Clicky.Core.Settings;
using Clicky.Core.Talk;
using Clicky.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Tray;

public sealed partial class TrayViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly IAppShell _shell;
    private readonly IBuddyPresence _presence;
    private readonly TalkLoop _talk;
    private readonly UpdateService _updates;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<TrayViewModel> _logger;

    // Set while copying a settings value into the VM so the change handler does not write it back.
    private bool _syncing;

    public TrayViewModel(
        ISettingsStore settings,
        IAppShell shell,
        IBuddyPresence presence,
        TalkLoop talk,
        UpdateService updates,
        Dispatcher dispatcher,
        ILogger<TrayViewModel> logger)
    {
        _settings = settings;
        _shell = shell;
        _presence = presence;
        _talk = talk;
        _updates = updates;
        _dispatcher = dispatcher;
        _logger = logger;

        SyncFrom(settings.Current);
        settings.Changed += OnSettingsChanged;
        ShowUpdate(updates.Status);
        updates.StatusChanged += OnUpdateStatusChanged;
    }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool HideFloatingCursor { get; set; }

    [ObservableProperty]
    public partial bool QuietMode { get; set; }

    /// <summary>The Update item is shown only once a new version is downloaded.</summary>
    [ObservableProperty]
    public partial bool UpdateReady { get; set; }

    [ObservableProperty]
    public partial string UpdateHeader { get; set; } = "Update and restart";

    public BuddyState BuddyState => _presence.State;

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _updates.StatusChanged -= OnUpdateStatusChanged;
    }

    [RelayCommand]
    private void OpenHome() => _shell.ShowHome();

    [RelayCommand]
    private void OpenSettings() => _shell.ShowSettings();

    [RelayCommand]
    private void Quit() => _shell.Quit();

    /// <summary>Forgets the conversation so far (memory gets its pass over it); the next question starts fresh.</summary>
    [RelayCommand]
    private void NewConversation()
    {
        _logger.LogInformation("Tray: new conversation");
        _talk.NewConversation();
    }

    [RelayCommand]
    private void Update() => _shell.RestartToUpdate();

    [RelayCommand]
    private void ReportBug() => _shell.ShowBugReport();

    // Preview only: lets each state be checked by eye. Talk drives them for real.
    [RelayCommand]
    private void SetBuddyState(BuddyState state) => _presence.SetState(state);

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_syncing)
        {
            _ = PersistAsync("start with Windows", value, s => s with { General = s.General with { StartWithWindows = value } });
        }
    }

    partial void OnQuietModeChanged(bool value)
    {
        if (!_syncing)
        {
            _ = PersistAsync("quiet mode", value, s => s with { Speech = s.Speech with { QuietMode = value } });
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

    private void OnUpdateStatusChanged(object? sender, UpdateStatus status) =>
        _dispatcher.InvokeAsync(() => ShowUpdate(status));

    private void ShowUpdate(UpdateStatus status)
    {
        UpdateReady = status.Phase == UpdatePhase.ReadyToRestart;
        UpdateHeader = $"Update to {status.AvailableVersion} and restart";
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
            QuietMode = settings.Speech.QuietMode;
        }
        finally
        {
            _syncing = false;
        }
    }
}
