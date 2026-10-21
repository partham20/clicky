using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Suggestions page (M17): whether Clicky researches each morning, when the card may appear,
/// how far back and how many ideas, web search, and backing off when the card is ignored.
/// </summary>
internal sealed class SuggestionsSettingsView : ContentControl
{
    private readonly ISettingsStore _settings;
    private readonly ITodaysIdeas _ideas;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SuggestionsSettingsView> _logger;

    private readonly CheckBox _enabled = new() { Content = "Suggest a few things to do each morning, from my apps and the web" };
    private readonly ComboBox _from = HourPicker(0, 23);
    private readonly ComboBox _until = HourPicker(1, 24);
    private readonly Slider _lookback = new() { Minimum = 24, Maximum = 168, Width = 240, TickFrequency = 24, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _lookbackValue = new() { Width = 80, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly Slider _maxIdeas = new() { Minimum = 1, Maximum = 5, Width = 240, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _maxIdeasValue = new() { Width = 80, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly CheckBox _web = new() { Content = "Also search the web for news and releases that touch my work" };
    private readonly CheckBox _backOff = new() { Content = "Come less often when I ignore the card three mornings in a row" };
    private readonly Button _showNow = new() { Content = "Show today's ideas", Padding = new Thickness(12, 3, 12, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
    private bool _syncing;

    public SuggestionsSettingsView(ISettingsStore settings, ITodaysIdeas ideas, Dispatcher dispatcher, ILogger<SuggestionsSettingsView> logger)
    {
        _settings = settings;
        _ideas = ideas;
        _dispatcher = dispatcher;
        _logger = logger;

        _enabled.Click += (_, _) => Save(s => s with { Enabled = _enabled.IsChecked == true });
        _from.SelectionChanged += (_, _) =>
        {
            if (_from.SelectedItem is ComboBoxItem { Tag: int hour })
            {
                Save(s => s with { ShowAfterHour = hour, LastHour = Math.Max(s.LastHour, hour + 1) });
            }
        };
        _until.SelectionChanged += (_, _) =>
        {
            if (_until.SelectedItem is ComboBoxItem { Tag: int hour })
            {
                Save(s => s with { LastHour = hour, ShowAfterHour = Math.Min(s.ShowAfterHour, hour - 1) });
            }
        };
        _lookback.ValueChanged += (_, e) =>
        {
            _lookbackValue.Text = $"{(int)e.NewValue} hours";
            Save(s => s with { LookbackHours = (int)e.NewValue });
        };
        _maxIdeas.ValueChanged += (_, e) =>
        {
            _maxIdeasValue.Text = (int)e.NewValue == 1 ? "1 idea" : $"{(int)e.NewValue} ideas";
            Save(s => s with { MaxIdeas = (int)e.NewValue });
        };
        _web.Click += (_, _) => Save(s => s with { SearchWeb = _web.IsChecked == true });
        _backOff.Click += (_, _) => Save(s => s with
        {
            IgnoredMorningsBeforeBackOff = _backOff.IsChecked == true ? SuggestionSettings.DefaultIgnoredMorningsBeforeBackOff : 0,
        });
        _showNow.Click += (_, _) => _ideas.ShowToday();

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

    private static TextBlock Caption(string text) => new() { Text = text, Width = 160, VerticalAlignment = VerticalAlignment.Center };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static ComboBox HourPicker(int first, int last)
    {
        var box = new ComboBox { Width = 100 };
        for (var hour = first; hour <= last; hour++)
        {
            box.Items.Add(new ComboBoxItem { Content = hour == 24 ? "midnight" : new TimeOnly(hour, 0).ToString("HH:mm", CultureInfo.CurrentCulture), Tag = hour });
        }

        return box;
    }

    private StackPanel Build()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Morning suggestions", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Note("Each morning Clicky reads your connected apps (only with tools that can't change anything) and, if you like, the web, then shows a few ideas one at a time: Do it, Next or Adjust. It remembers what you did with them and never suggests the same thing twice in two weeks."));
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_enabled);

        panel.Children.Add(Label("When"));
        panel.Children.Add(Row(Caption("Show the card from"), _from));
        panel.Children.Add(Row(Caption("Not after"), _until));
        panel.Children.Add(Note("The card waits until you're at the PC, and stays away during calls, screen sharing, Focus Assist and full-screen apps. Research starts an hour earlier so it's ready."));
        panel.Children.Add(_backOff);

        panel.Children.Add(Label("What"));
        panel.Children.Add(Row(Caption("Look back"), _lookback, _lookbackValue));
        panel.Children.Add(Row(Caption("At most"), _maxIdeas, _maxIdeasValue));
        panel.Children.Add(_web);
        panel.Children.Add(Note("One research pass and one editor request a day, on the backend chosen on the Claude page. With no connectors and web search off there is nothing to research, so no card."));
        panel.Children.Add(_showNow);
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Suggestions != e.Current.Suggestions)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void Sync()
    {
        var s = _settings.Current.Suggestions;
        _syncing = true;
        try
        {
            _enabled.IsChecked = s.Enabled;
            Select(_from, s.ShowAfterHour);
            Select(_until, s.LastHour);
            _lookback.Value = s.LookbackHours;
            _lookbackValue.Text = $"{s.LookbackHours} hours";
            _maxIdeas.Value = s.MaxIdeas;
            _maxIdeasValue.Text = s.MaxIdeas == 1 ? "1 idea" : $"{s.MaxIdeas} ideas";
            _web.IsChecked = s.SearchWeb;
            _backOff.IsChecked = s.IgnoredMorningsBeforeBackOff > 0;
            _from.IsEnabled = _until.IsEnabled = _lookback.IsEnabled = _maxIdeas.IsEnabled = _web.IsEnabled = _backOff.IsEnabled = s.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void Select(ComboBox box, int hour) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int h && h == hour);

    private void Save(Func<SuggestionSettings, SuggestionSettings> change)
    {
        if (!_syncing)
        {
            _ = SaveAsync(change);
        }
    }

    private async Task SaveAsync(Func<SuggestionSettings, SuggestionSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Suggestions = change(s.Suggestions) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save suggestion settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
