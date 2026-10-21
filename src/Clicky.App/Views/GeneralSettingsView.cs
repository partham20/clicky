using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core;
using Clicky.Core.Capture;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The General page: start with Windows, the tutorial, what screenshots contain, logging and the
/// debug switches, and the data folder.
/// </summary>
internal sealed class GeneralSettingsView : ContentControl
{
    private readonly ISettingsStore _settings;
    private readonly IAppShell _shell;
    private readonly ClickyPaths _paths;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<GeneralSettingsView> _logger;

    private readonly CheckBox _startWithWindows = new() { Content = "Start Clicky when I sign in to Windows" };
    private readonly ComboBox _captureMode = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _includeCursor = new() { Content = "Show the mouse pointer in screenshots", Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _logLevel = new() { MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _saveCaptures = new() { Content = "Save every screenshot to the debug folder" };
    private readonly CheckBox _saveAudio = new() { Content = "Save every Talk recording to the debug folder", Margin = new Thickness(0, 4, 0, 0) };
    private bool _syncing;

    public GeneralSettingsView(ISettingsStore settings, IAppShell shell, ClickyPaths paths, Dispatcher dispatcher, ILogger<GeneralSettingsView> logger)
    {
        _settings = settings;
        _shell = shell;
        _paths = paths;
        _dispatcher = dispatcher;
        _logger = logger;

        _captureMode.Items.Add(new ComboBoxItem { Content = "The monitor under the pointer", Tag = CaptureMode.MonitorUnderCursor });
        _captureMode.Items.Add(new ComboBoxItem { Content = "The window in front", Tag = CaptureMode.ForegroundWindow });
        _captureMode.Items.Add(new ComboBoxItem { Content = "All monitors", Tag = CaptureMode.AllMonitors });
        foreach (var level in Enum.GetValues<LogLevelSetting>())
        {
            _logLevel.Items.Add(new ComboBoxItem { Content = level.ToString(), Tag = level });
        }

        _startWithWindows.Click += (_, _) => Save(s => s with { General = s.General with { StartWithWindows = _startWithWindows.IsChecked == true } });
        _captureMode.SelectionChanged += (_, _) =>
        {
            if (_captureMode.SelectedItem is ComboBoxItem { Tag: CaptureMode mode })
            {
                Save(s => s with { Capture = s.Capture with { Mode = mode } });
            }
        };
        _includeCursor.Click += (_, _) => Save(s => s with { Capture = s.Capture with { IncludeCursor = _includeCursor.IsChecked == true } });
        _logLevel.SelectionChanged += (_, _) =>
        {
            if (_logLevel.SelectedItem is ComboBoxItem { Tag: LogLevelSetting level })
            {
                Save(s => s with { Logging = s.Logging with { MinimumLevel = level } });
            }
        };
        _saveCaptures.Click += (_, _) => Save(s => s with { Debug = s.Debug with { SaveCaptures = _saveCaptures.IsChecked == true } });
        _saveAudio.Click += (_, _) => Save(s => s with { Debug = s.Debug with { SaveAudio = _saveAudio.IsChecked == true } });

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            Sync();
        };
        Unloaded += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static Button SmallButton(string text) => new() { Content = text, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0) };

    private StackPanel Build()
    {
        var tutorial = SmallButton("Take the tour again");
        tutorial.Click += (_, _) => _shell.ShowOnboarding();
        var dataFolder = SmallButton("Open Clicky's folder");
        dataFolder.Click += (_, _) => OpenFolder(_paths.Root);
        var debugFolder = SmallButton("Open debug folder");
        debugFolder.Click += (_, _) => OpenFolder(Path.GetDirectoryName(_paths.DebugCapturesDirectory)!);

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "General", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(_startWithWindows);

        panel.Children.Add(Label("Getting started"));
        panel.Children.Add(new TextBlock
        {
            Text = "A two-minute hands-on tour: connect Claude, check the mic, then say hello, watch it draw, circle something and dictate.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(Row(tutorial));

        panel.Children.Add(Label("What Clicky sees when you ask"));
        panel.Children.Add(_captureMode);
        panel.Children.Add(_includeCursor);
        panel.Children.Add(new TextBlock
        {
            Text = "Screenshots go to Claude with your question and are never saved, unless the debug switch below is on. Clicky's own marks are left out.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            Margin = new Thickness(0, 6, 0, 0),
        });

        panel.Children.Add(Label("Troubleshooting"));
        panel.Children.Add(Row(new TextBlock { Text = "Log detail", Width = 120, VerticalAlignment = VerticalAlignment.Center }, _logLevel));
        panel.Children.Add(new Border { Height = 6 });
        panel.Children.Add(_saveCaptures);
        panel.Children.Add(_saveAudio);
        panel.Children.Add(new Border { Height = 8 });
        panel.Children.Add(Row(dataFolder, debugFolder));
        return panel;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        var (a, b) = (e.Previous, e.Current);
        if (a.General != b.General || a.Capture != b.Capture || a.Logging != b.Logging || a.Debug != b.Debug)
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
            _startWithWindows.IsChecked = s.General.StartWithWindows;
            _captureMode.SelectedItem = _captureMode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (CaptureMode)i.Tag == s.Capture.Mode);
            _includeCursor.IsChecked = s.Capture.IncludeCursor;
            _logLevel.SelectedItem = _logLevel.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (LogLevelSetting)i.Tag == s.Logging.MinimumLevel);
            _saveCaptures.IsChecked = s.Debug.SaveCaptures;
            _saveAudio.IsChecked = s.Debug.SaveAudio;
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
            _logger.LogError(ex, "Could not save general settings");
        }

        // Re-sync even on success: the store skips no-op changes, so no event would put a refused value back.
        await _dispatcher.InvokeAsync(Sync);
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Could not open {Folder}", folder);
        }
    }
}
