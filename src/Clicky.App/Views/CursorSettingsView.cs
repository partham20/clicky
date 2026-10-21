using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.App.Buddy;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Cursor and character page: the buddy's look, whether it follows the pointer or docks to
/// Home and how loosely it trails, and the pointer trail recorded while you talk.
/// </summary>
internal sealed class CursorSettingsView : ContentControl
{
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<CursorSettingsView> _logger;

    private readonly ComboBox _character = new() { MinWidth = 240, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _follow = new() { Content = "Show the buddy beside the mouse pointer (off: it waits in Home)" };
    private readonly Slider _smoothing = new() { Minimum = 0, Maximum = OverlaySettings.MaxFollowSmoothingMs, Width = 240, TickFrequency = 20, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _smoothingValue = new() { Width = 120, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly CheckBox _inkEnabled = new() { Content = "Send what I circle or point at while talking" };
    private readonly CheckBox _inkShow = new() { Content = "Draw the trail on screen while I hold Talk", Margin = new Thickness(20, 4, 0, 0) };
    private readonly CheckBox _inkPaint = new() { Content = "Paint it onto the screenshot too (Claude sees exactly what I drew)", Margin = new Thickness(20, 4, 0, 0) };
    private bool _syncing;

    public CursorSettingsView(ISettingsStore settings, IEnumerable<IBuddySkin> skins, Dispatcher dispatcher, ILogger<CursorSettingsView> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        // One character for now; Clickys (M14) bring more. The setting lands when there is a choice.
        foreach (var skin in skins)
        {
            _character.Items.Add(skin.Name);
        }

        _character.SelectedIndex = 0;
        _character.IsEnabled = _character.Items.Count > 1;

        _follow.Click += (_, _) => Save(s => s with { Overlay = s.Overlay with { HideFloatingCursor = _follow.IsChecked != true } });
        _smoothing.ValueChanged += (_, e) =>
        {
            ShowSmoothing((int)e.NewValue);
            Save(s => s with { Overlay = s.Overlay with { FollowSmoothingMs = (int)e.NewValue } });
        };
        _inkEnabled.Click += (_, _) => Save(s => s with { Ink = s.Ink with { Enabled = _inkEnabled.IsChecked == true } });
        _inkShow.Click += (_, _) => Save(s => s with { Ink = s.Ink with { ShowTrail = _inkShow.IsChecked == true } });
        _inkPaint.Click += (_, _) => Save(s => s with { Ink = s.Ink with { DrawOnScreenshot = _inkPaint.IsChecked == true } });

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            Sync();
        };
        Unloaded += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static TextBlock Note(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0) };

    private StackPanel Build()
    {
        var smoothingRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        smoothingRow.Children.Add(new TextBlock { Text = "Follow", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        smoothingRow.Children.Add(_smoothing);
        smoothingRow.Children.Add(_smoothingValue);

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Cursor and character", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Note("The buddy rides next to your pointer: it pulses while listening, thinks with orbiting dots and glows while it speaks."));

        panel.Children.Add(Label("Character"));
        panel.Children.Add(_character);

        panel.Children.Add(Label("Where it lives"));
        panel.Children.Add(_follow);
        panel.Children.Add(smoothingRow);

        panel.Children.Add(Label("Pointing while you talk"));
        panel.Children.Add(_inkEnabled);
        panel.Children.Add(_inkShow);
        panel.Children.Add(_inkPaint);
        panel.Children.Add(Note("Hold Talk and circle, underline or rest on something: \"what's this?\" then means the thing you circled."));
        return panel;
    }

    private void ShowSmoothing(int ms) => _smoothingValue.Text = ms == 0 ? "pinned to the pointer" : ms <= 60 ? $"snappy ({ms} ms)" : $"floaty ({ms} ms)";

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Overlay != e.Current.Overlay || e.Previous.Ink != e.Current.Ink)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void Sync()
    {
        var s = _settings.Current;
        _syncing = true;
        try
        {
            _follow.IsChecked = !s.Overlay.HideFloatingCursor;
            _smoothing.Value = Math.Clamp(s.Overlay.FollowSmoothingMs, 0, OverlaySettings.MaxFollowSmoothingMs);
            ShowSmoothing((int)_smoothing.Value);
            _smoothing.IsEnabled = !s.Overlay.HideFloatingCursor;
            _inkEnabled.IsChecked = s.Ink.Enabled;
            _inkShow.IsChecked = s.Ink.ShowTrail;
            _inkPaint.IsChecked = s.Ink.DrawOnScreenshot;
            _inkShow.IsEnabled = _inkPaint.IsEnabled = s.Ink.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Save(Func<ClickySettings, ClickySettings> change)
    {
        if (!_syncing)
        {
            _ = SaveAsync(change);
        }
    }

    private async Task SaveAsync(Func<ClickySettings, ClickySettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save cursor settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
