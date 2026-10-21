using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The computer use section of the Agents page (M13): whether Clicky may act, how far one request
/// may go, the cancel countdown, the API computer toolset, and Clicky's own browser.
/// </summary>
internal sealed class ComputerUseSettingsView : ContentControl
{
    private readonly ISettingsStore _settings;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ComputerUseSettingsView> _logger;

    private readonly CheckBox _enabled = new() { Content = "Let Clicky click, type and open pages when I ask it to do something" };
    private readonly Slider _maxSteps = new() { Minimum = 1, Maximum = AgentSettings.MaxMaxSteps, Width = 240, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _maxStepsValue = new() { Width = 80, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly Slider _cancelWindow = new() { Minimum = 0, Maximum = AgentSettings.MaxCancelWindowSeconds, Width = 240, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _cancelWindowValue = new() { Width = 80, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly CheckBox _toolset = new() { Content = "Use Anthropic's computer toolset on the API backend (recommended)" };
    private readonly CheckBox _browserProfile = new() { Content = "Do web tasks in Clicky's own browser window, not my everyday profile" };
    private readonly TextBox _browserPath = new() { Width = 360 };
    private bool _syncing;

    public ComputerUseSettingsView(ISettingsStore settings, Dispatcher dispatcher, ILogger<ComputerUseSettingsView> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        _enabled.Click += (_, _) => Save(a => a with { Enabled = _enabled.IsChecked == true });
        _maxSteps.ValueChanged += (_, e) =>
        {
            _maxStepsValue.Text = $"{(int)e.NewValue} steps";
            Save(a => a with { MaxSteps = (int)e.NewValue });
        };
        _cancelWindow.ValueChanged += (_, e) =>
        {
            _cancelWindowValue.Text = (int)e.NewValue == 0 ? "off" : $"{(int)e.NewValue} s";
            Save(a => a with { CancelWindowSeconds = (int)e.NewValue });
        };
        _toolset.Click += (_, _) => Save(a => a with { ComputerToolset = _toolset.IsChecked == true });
        _browserProfile.Click += (_, _) => Save(a => a with { UseBrowserProfile = _browserProfile.IsChecked == true });
        _browserPath.LostFocus += (_, _) => Save(a => a with { BrowserPath = _browserPath.Text.Trim().Trim('"') });

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

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Width = 160, VerticalAlignment = VerticalAlignment.Center };

    private StackPanel Build()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Computer use", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(Note("Ask Clicky to do something (\"save this to my Desktop\") and it works through the app, showing each step on a card you can stop. It asks before anything it can't undo, like sending, deleting or buying, every time."));
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_enabled);

        panel.Children.Add(Label("Limits"));
        panel.Children.Add(Row(Caption("Most steps per request"), _maxSteps, _maxStepsValue));
        panel.Children.Add(Row(Caption("Countdown before acting"), _cancelWindow, _cancelWindowValue));
        panel.Children.Add(Note("The countdown runs when you've said \"Always for this conversation\", so you can still cancel before Clicky starts."));

        panel.Children.Add(Label("How it works"));
        panel.Children.Add(_toolset);
        panel.Children.Add(Note("The toolset is what Claude is trained on; Clicky falls back to its own tools by itself if the API refuses it. The subscription backend always uses Clicky's tools."));
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(_browserProfile);
        panel.Children.Add(Row(Caption("Browser (chrome.exe or msedge.exe)"), _browserPath));
        panel.Children.Add(Note("Empty finds Chrome, then Edge. Chrome doesn't allow automation of your default profile, so Clicky keeps its own in %LocalAppData%\\Clicky\\browser; sign in there once for sites it should use."));
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Agent != e.Current.Agent)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void Sync()
    {
        var agent = _settings.Current.Agent;
        _syncing = true;
        try
        {
            _enabled.IsChecked = agent.Enabled;
            _maxSteps.Value = agent.MaxSteps;
            _maxStepsValue.Text = $"{agent.MaxSteps} steps";
            _cancelWindow.Value = agent.CancelWindowSeconds;
            _cancelWindowValue.Text = agent.CancelWindowSeconds == 0 ? "off" : $"{agent.CancelWindowSeconds} s";
            _toolset.IsChecked = agent.ComputerToolset;
            _browserProfile.IsChecked = agent.UseBrowserProfile;
            if (!_browserPath.IsKeyboardFocused)
            {
                _browserPath.Text = agent.BrowserPath;
            }

            _maxSteps.IsEnabled = _cancelWindow.IsEnabled = _toolset.IsEnabled = _browserProfile.IsEnabled = _browserPath.IsEnabled = agent.Enabled;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Save(Func<AgentSettings, AgentSettings> change)
    {
        if (!_syncing)
        {
            _ = SaveAsync(change);
        }
    }

    private async Task SaveAsync(Func<AgentSettings, AgentSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Agent = change(s.Agent) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save computer use settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
