using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.App.Controls;
using Clicky.Core.Hotkeys;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Hotkeys section of Settings: one row per binding with an enable box and a shortcut recorder.
/// Writes straight to settings.json; edits made to the file while it is open show up here.
/// </summary>
internal sealed class HotkeysSettingsView : ContentControl
{
    private readonly ISettingsStore _settings;
    private readonly IHotkeyService _hotkeys;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<HotkeysSettingsView> _logger;
    private readonly List<Row> _rows = [];
    private readonly TextBlock _problems;
    private bool _syncing;

    public HotkeysSettingsView(ISettingsStore settings, IHotkeyService hotkeys, Dispatcher dispatcher, ILogger<HotkeysSettingsView> logger)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _dispatcher = dispatcher;
        _logger = logger;
        _problems = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            Build(_settings.Current.Hotkeys);
        };
        Unloaded += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.Talk => "Talk",
        HotkeyAction.TextMode => "Type a question",
        HotkeyAction.Dictation => "Dictation",
        HotkeyAction.DictationHandsFree => "Hands-free dictation",
        HotkeyAction.OpenHome => "Open Home",
        HotkeyAction.AlwaysOnVoice => "Always-on voice",
        _ => action.ToString(),
    };

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Hotkeys != e.Current.Hotkeys)
        {
            _dispatcher.InvokeAsync(() => Sync(e.Current.Hotkeys));
        }
    }

    private void Build(HotkeySettings hotkeys)
    {
        _rows.Clear();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < hotkeys.Bindings.Count; i++)
        {
            var index = i;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var enabled = new CheckBox { Content = Describe(hotkeys.Bindings[i].Action), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 16, 4) };
            enabled.Checked += (_, _) => OnEnabledChanged(index, true);
            enabled.Unchecked += (_, _) => OnEnabledChanged(index, false);
            Grid.SetRow(enabled, i);
            grid.Children.Add(enabled);

            var recorder = new ShortcutRecorder(_hotkeys) { Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
            recorder.GestureCommitted += (_, gesture) => OnGestureCommitted(index, gesture);
            Grid.SetRow(recorder, i);
            Grid.SetColumn(recorder, 1);
            grid.Children.Add(recorder);

            _rows.Add(new Row(enabled, recorder));
        }

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Hotkeys", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Click a shortcut and perform the new one: hold modifiers, press a key with modifiers, or tap a modifier twice or three times. Esc cancels, Backspace clears.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(grid);
        panel.Children.Add(_problems);
        Content = panel;

        Sync(hotkeys);
    }

    private void Sync(HotkeySettings hotkeys)
    {
        if (hotkeys.Bindings.Count != _rows.Count)
        {
            Build(hotkeys);
            return;
        }

        _syncing = true;
        try
        {
            for (var i = 0; i < _rows.Count; i++)
            {
                var setting = hotkeys.Bindings[i];
                var row = _rows[i];
                row.Enabled.IsChecked = setting.Enabled;
                if (!row.Recorder.IsRecording)
                {
                    row.Recorder.Gesture = HotkeyGesture.TryParse(setting.Gesture, out var gesture) ? gesture : null;
                }
            }
        }
        finally
        {
            _syncing = false;
        }

        hotkeys.ToBindings(out var problems);
        _problems.Text = string.Join(Environment.NewLine, problems);
        _problems.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnEnabledChanged(int index, bool enabled)
    {
        if (!_syncing)
        {
            _ = UpdateBindingAsync(index, b => b with { Enabled = enabled });
        }
    }

    private void OnGestureCommitted(int index, HotkeyGesture? gesture) =>
        _ = UpdateBindingAsync(index, b => b with { Gesture = gesture?.ToString() ?? string.Empty });

    private async Task UpdateBindingAsync(int index, Func<HotkeyBindingSettings, HotkeyBindingSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s =>
            {
                if (index >= s.Hotkeys.Bindings.Count)
                {
                    return s;
                }

                var bindings = s.Hotkeys.Bindings.ToArray();
                bindings[index] = change(bindings[index]);
                return s with { Hotkeys = s.Hotkeys with { Bindings = bindings } };
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save hotkey change");
        }

        // Re-sync even on success: the store skips no-op changes, so no event would put a refused value back.
        await _dispatcher.InvokeAsync(() => Sync(_settings.Current.Hotkeys));
    }

    private sealed record Row(CheckBox Enabled, ShortcutRecorder Recorder);
}
