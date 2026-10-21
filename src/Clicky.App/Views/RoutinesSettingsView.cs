using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Routines;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Routines section of Settings (and of Home once it exists): each routine with its schedule
/// and live status, Run now, Pause/Resume, Edit, History and Delete, plus the settings.routines
/// switches. The routines themselves live in routines.db, owned by <see cref="RoutineEngine"/>.
/// </summary>
internal sealed class RoutinesSettingsView : ContentControl
{
    private static readonly Brush Green = Frozen(0x2E, 0xA0, 0x43);
    private static readonly Brush Amber = Frozen(0xD2, 0x99, 0x22);
    private static readonly Brush Red = Frozen(0xCF, 0x22, 0x2E);
    private static readonly Brush Blue = Frozen(0x2F, 0x81, 0xF7);
    private static readonly Brush Gray = Frozen(0x8C, 0x95, 0x9F);

    private readonly RoutineEngine _engine;
    private readonly IClickyRunner _runner;
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<RoutinesSettingsView> _logger;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _clock;
    private readonly StackPanel _cards = new();
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _enabled = new() { Content = "Run routines on schedule" };
    private readonly CheckBox _hold = new() { Content = "Wait while I'm in a call, sharing my screen or presenting" };
    private readonly CheckBox _notify = new() { Content = "Tell me when a routine is paused after repeated failures" };
    private readonly ComboBox _catchUp = new() { Width = 150, Margin = new Thickness(6, 0, 0, 0) };
    private bool _syncing;

    public RoutinesSettingsView(RoutineEngine engine, IClickyRunner runner, ISettingsStore settings, Dispatcher dispatcher, ILogger<RoutinesSettingsView> logger)
    {
        _engine = engine;
        _runner = runner;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        // Engine events come in bursts (a run starting saves twice); redraw at most 5x a second.
        _refresh = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(200) };
        _refresh.Tick += (_, _) =>
        {
            _refresh.Stop();
            Rebuild();
        };

        // "Next: today 8:00 AM" turns into "tomorrow" at midnight; a slow redraw keeps it honest while open.
        _clock = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => Rebuild();

        foreach (var (hours, text) in new[] { (0, "never"), (1, "1 hour"), (3, "3 hours"), (6, "6 hours"), (12, "12 hours"), (24, "24 hours"), (72, "3 days") })
        {
            _catchUp.Items.Add(new ComboBoxItem { Content = text, Tag = hours });
        }

        _enabled.Click += (_, _) => SaveSettings(r => r with { Enabled = _enabled.IsChecked == true });
        _hold.Click += (_, _) => SaveSettings(r => r with { HoldDuringCalls = _hold.IsChecked == true });
        _notify.Click += (_, _) => SaveSettings(r => r with { NotifyWhenPaused = _notify.IsChecked == true });
        _catchUp.SelectionChanged += (_, _) =>
        {
            if (_catchUp.SelectedItem is ComboBoxItem { Tag: int hours })
            {
                SaveSettings(r => r with { CatchUpHours = hours });
            }
        };

        Content = Build();
        Loaded += (_, _) =>
        {
            _engine.Changed += OnChanged;
            _settings.Changed += OnSettingsChanged;
            SyncSettings();
            Rebuild();
            _clock.Start();
        };
        Unloaded += (_, _) =>
        {
            _engine.Changed -= OnChanged;
            _settings.Changed -= OnSettingsChanged;
            _refresh.Stop();
            _clock.Stop();
        };
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static Button SmallButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        button.Click += click;
        return button;
    }

    private static Brush ColorOf(RoutineState state, Routine routine) => state switch
    {
        RoutineState.Running => Blue,
        RoutineState.Waiting => Amber,
        RoutineState.Paused when routine.PausedReason is not null => Red,
        RoutineState.Scheduled when routine.LastStatus == RoutineRunStatus.Failed => Red,
        RoutineState.Scheduled => Green,
        _ => Gray,
    };

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Routines", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Things Clicky does on a schedule while it's open, like a morning briefing. Ask for one (\"do this every morning\") " +
                   "or add it here. Results go to the Clicky's chat quietly, never out loud. A run missed while the PC slept is caught up once.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_cards);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var add = new Button { Content = "New routine…", Padding = new Thickness(10, 2, 10, 2) };
        add.Click += (_, _) => _ = EditAsync(null);
        buttons.Children.Add(add);
        buttons.Children.Add(_status);
        panel.Children.Add(buttons);

        panel.Children.Add(new Border { Height = 10 });
        panel.Children.Add(_enabled);
        panel.Children.Add(_hold);
        panel.Children.Add(_notify);
        var catchUp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        catchUp.Children.Add(new TextBlock { Text = "Catch up a missed run if it was due within", VerticalAlignment = VerticalAlignment.Center });
        catchUp.Children.Add(_catchUp);
        panel.Children.Add(catchUp);
        return panel;
    }

    private void OnChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(ScheduleRefresh);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Routines != e.Current.Routines)
        {
            _dispatcher.InvokeAsync(() =>
            {
                SyncSettings();
                ScheduleRefresh();
            });
        }
    }

    private void ScheduleRefresh()
    {
        if (!_refresh.IsEnabled)
        {
            _refresh.Start();
        }
    }

    private void SyncSettings()
    {
        var routines = _settings.Current.Routines;
        _syncing = true;
        try
        {
            _enabled.IsChecked = routines.Enabled;
            _hold.IsChecked = routines.HoldDuringCalls;
            _notify.IsChecked = routines.NotifyWhenPaused;
            var items = _catchUp.Items.Cast<ComboBoxItem>().ToList();
            _catchUp.SelectedItem = items.FirstOrDefault(i => (int)i.Tag == routines.CatchUpHours)
                ?? items.LastOrDefault(i => (int)i.Tag <= routines.CatchUpHours)
                ?? items[0];
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SaveSettings(Func<RoutineSettings, RoutineSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _ = Run(() => _settings.UpdateAsync(s => s with { Routines = change(s.Routines).Normalize() }));
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        var routines = _engine.Routines;
        if (routines.Count == 0)
        {
            _cards.Children.Add(new TextBlock
            {
                Text = "No routines yet. Try \"brief me on my calendar and the news every weekday at 8\".",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var routine in routines)
        {
            _cards.Children.Add(Card(routine));
        }
    }

    private Border Card(Routine routine)
    {
        var status = _engine.GetStatus(routine.Id);
        var body = new StackPanel();

        var header = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(status.State == RoutineState.Running
            ? SmallButton("Stop", (_, _) => _ = Run(() => _engine.StopRunAsync(routine.Id)))
            : SmallButton("Run now", (_, _) => _ = Run(() => _engine.RunNowAsync(routine.Id))));
        if (!routine.IsFinished)
        {
            actions.Children.Add(routine.Enabled
                ? SmallButton("Pause", (_, _) => _ = Run(() => _engine.SetEnabledAsync(routine.Id, false)))
                : SmallButton("Resume", (_, _) => _ = Run(() => _engine.SetEnabledAsync(routine.Id, true))));
        }

        actions.Children.Add(SmallButton("Edit…", (_, _) => _ = EditAsync(routine)));
        actions.Children.Add(SmallButton("History…", (_, _) => new RoutineHistoryWindow(_engine, routine) { Owner = Window.GetWindow(this) }.Show()));
        actions.Children.Add(SmallButton("Delete", (_, _) => _ = DeleteAsync(routine)));
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = ColorOf(status.State, routine), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        title.Children.Add(new TextBlock { Text = routine.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 220 });
        header.Children.Add(title);
        body.Children.Add(header);

        body.Children.Add(new TextBlock
        {
            Text = $"{routine.Schedule.Describe()} · {status.Text}",
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 2, 0, 0),
        });

        if (routine.LastRunAt is { } last && routine.LastStatus is { } lastStatus && lastStatus != RoutineRunStatus.Running)
        {
            var text = $"Last run {RoutineFormat.When(last, DateTimeOffset.UtcNow, TimeZoneInfo.Local)}: {LastStatusText(lastStatus)}";
            if (!string.IsNullOrWhiteSpace(routine.LastMessage) && routine.PausedReason is null)
            {
                text += $" · {routine.LastMessage}";
            }

            body.Children.Add(new TextBlock
            {
                Text = text,
                Opacity = 0.6,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = routine.LastMessage,
                Margin = new Thickness(14, 2, 0, 0),
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = routine.Prompt.ReplaceLineEndings(" "),
            Opacity = 0.6,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = routine.Prompt,
            Margin = new Thickness(14, 2, 0, 0),
        });

        return new Border
        {
            BorderBrush = SystemColors.ActiveBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    private static string LastStatusText(RoutineRunStatus status) => status switch
    {
        RoutineRunStatus.Succeeded => "done",
        RoutineRunStatus.Failed => "failed",
        RoutineRunStatus.Skipped => "skipped",
        RoutineRunStatus.Interrupted => "interrupted",
        RoutineRunStatus.Cancelled => "stopped",
        _ => status.ToString().ToLowerInvariant(),
    };

    private async Task EditAsync(Routine? routine)
    {
        IReadOnlyList<ClickyRef> clickys;
        try
        {
            clickys = await _runner.ListClickysAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not list Clickys for the routine editor");
            clickys = [];
        }

        var editor = new RoutineEditorWindow(routine, clickys) { Owner = Window.GetWindow(this) };
        if (editor.ShowDialog() != true || editor.Result is not { } draft)
        {
            return;
        }

        await Run(async () =>
        {
            if (routine is null)
            {
                var created = await _engine.CreateAsync(draft);
                _status.Text = $"Added \"{created.Name}\". {_engine.GetStatus(created.Id).Text}.";
            }
            else
            {
                await _engine.UpdateAsync(routine.Id, draft);
                _status.Text = $"Saved \"{draft.Name.Trim()}\".";
            }
        });
    }

    private async Task DeleteAsync(Routine routine)
    {
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Delete the routine \"{routine.Name}\" and its history?",
            "Delete routine",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.OK)
        {
            await Run(() => _engine.DeleteAsync(routine.Id));
        }
    }

    private async Task Run(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            _status.Text = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Routines action failed");
            _status.Text = $"That didn't work: {ex.Message}";
        }
    }
}
