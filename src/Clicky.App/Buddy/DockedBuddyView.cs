using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.Core.Buddy;
using Clicky.Core.Settings;

namespace Clicky.App.Buddy;

/// <summary>
/// Home's buddy slot. With "Hide floating cursor" on, the buddy lives here instead of following the
/// pointer. Lives in the M0 Home stub until the real Home window lands in M14.
/// </summary>
internal sealed class DockedBuddyView : ContentControl
{
    private readonly IBuddyVisual _visual;
    private readonly IBuddyPresence _presence;
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Viewbox _docked;
    private readonly TextBlock _floatingNote;

    public DockedBuddyView(IBuddySkin skin, IBuddyPresence presence, ISettingsStore settings, Dispatcher dispatcher)
    {
        _presence = presence;
        _settings = settings;
        _dispatcher = dispatcher;
        _visual = skin.Create();
        _docked = new Viewbox { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Child = _visual.Element };
        _floatingNote = new TextBlock
        {
            Text = "Your buddy is following the pointer. Turn on \"Hide floating cursor\" in the tray menu to dock it here.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _presence.StateChanged += OnPresenceChanged;
        _presence.LevelChanged += OnLevelChanged;
        _settings.Changed += OnSettingsChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _presence.StateChanged -= OnPresenceChanged;
        _presence.LevelChanged -= OnLevelChanged;
        _settings.Changed -= OnSettingsChanged;
        _visual.State = BuddyState.Idle;
    }

    private void OnPresenceChanged(object? sender, BuddyStateChangedEventArgs e) => _dispatcher.InvokeAsync(Refresh);

    private void OnLevelChanged(object? sender, EventArgs e) =>
        _dispatcher.InvokeAsync(() => _visual.Level = _presence.Level, DispatcherPriority.Render);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e) => _dispatcher.InvokeAsync(Refresh);

    private void Refresh()
    {
        var docked = _settings.Current.Overlay.HideFloatingCursor;
        Content = docked ? _docked : _floatingNote;

        // Only animate when shown, so a floating buddy and a hidden dock never both tick.
        _visual.State = docked && IsLoaded ? _presence.State : BuddyState.Idle;
    }
}
