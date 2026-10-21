using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Audio;
using Clicky.Platform.Windows.Speech;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Speech section of Settings: which voice says replies, how fast and loud, on which device,
/// and when Clicky stays quiet and shows replies as text instead. Everything goes to settings.json.
/// </summary>
internal sealed class SpeechSettingsView : ContentControl
{
    private const string DefaultLabel = "Windows default";

    private const string TestText =
        "Hi, I'm Clicky. This is how I sound when I answer you. " +
        "Press the Talk keys while I'm talking, and I'll stop and listen.";

    private static readonly string[] CloudVoices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer", "verse"];

    private readonly ISettingsStore _settings;
    private readonly ReplyPresenter _replies;
    private readonly LocalTextToSpeech _localTts;
    private readonly LocalVoiceDownloader _localModels;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<SpeechSettingsView> _logger;
    private readonly DispatcherTimer _sliderSave;

    private readonly CheckBox _quietMode = new() { Content = "Quiet mode: never speak, show replies as text (also in the tray menu)" };
    private readonly RadioButton _windows = new() { Content = "Windows voices: free, offline, quick", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _local = new() { Content = "Natural voice on this PC (Kokoro): free, offline, reply text stays here; one-time download", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _cloud = new() { Content = "Cloud (OpenAI): natural voices; uses the OpenAI key above and sends reply text to OpenAI", Margin = new Thickness(0, 2, 0, 2) };
    private readonly ComboBox _windowsVoice = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _cloudVoice = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _localVoice = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _localStatus = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
    private readonly Slider _rate = new() { Minimum = SpeechSettings.MinRate, Maximum = SpeechSettings.MaxRate, TickFrequency = 0.1, IsSnapToTickEnabled = true, Width = 240 };
    private readonly Slider _volume = new() { Minimum = 0, Maximum = 100, TickFrequency = 5, IsSnapToTickEnabled = true, Width = 240 };
    private readonly TextBlock _rateLabel = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _volumeLabel = new() { Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _device = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly CheckBox _inCalls = new() { Content = "In a call (another app is using the microphone)" };
    private readonly CheckBox _sharing = new() { Content = "While sharing my screen (Teams, Zoom, Meet, Slack; best effort)" };
    private readonly CheckBox _focus = new() { Content = "During Focus Assist / Do not disturb, presentations and full-screen apps" };
    private readonly TextBlock _status = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private bool _syncing;

    public SpeechSettingsView(
        ISettingsStore settings,
        ReplyPresenter replies,
        LocalTextToSpeech localTts,
        LocalVoiceDownloader localModels,
        Dispatcher dispatcher,
        ILogger<SpeechSettingsView> logger)
    {
        _settings = settings;
        _replies = replies;
        _localTts = localTts;
        _localModels = localModels;
        _dispatcher = dispatcher;
        _logger = logger;
        _sliderSave = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _sliderSave.Tick += (_, _) =>
        {
            _sliderSave.Stop();
            Save(s => s with { Rate = Math.Round(_rate.Value, 1), Volume = Math.Round(_volume.Value) / 100 });
        };

        _quietMode.Click += (_, _) => Save(s => s with { QuietMode = _quietMode.IsChecked == true });
        _windows.Checked += (_, _) => Save(s => s with { Engine = VoiceOutputEngine.Windows });
        _cloud.Checked += (_, _) => Save(s => s with { Engine = VoiceOutputEngine.Cloud });
        _local.Checked += (_, _) => Save(s => s with { Engine = VoiceOutputEngine.Local });
        _localVoice.SelectionChanged += (_, _) =>
        {
            if (_localVoice.SelectedItem is ComboBoxItem { Tag: string id })
            {
                Save(s => s with { LocalVoice = id });
            }
        };
        _windowsVoice.SelectionChanged += (_, _) =>
        {
            if (_windowsVoice.SelectedItem is ComboBoxItem { Tag: string id })
            {
                Save(s => s with { WindowsVoice = id });
            }
        };
        _cloudVoice.SelectionChanged += (_, _) =>
        {
            if (_cloudVoice.SelectedItem is string name)
            {
                Save(s => s with { CloudVoice = name });
            }
        };
        _device.SelectionChanged += (_, _) =>
        {
            if (_device.SelectedItem is ComboBoxItem { Tag: string id })
            {
                Save(s => s with { OutputDevice = id });
            }
        };
        _inCalls.Click += (_, _) => Save(s => s with { QuietInCalls = _inCalls.IsChecked == true });
        _sharing.Click += (_, _) => Save(s => s with { QuietWhenSharing = _sharing.IsChecked == true });
        _focus.Click += (_, _) => Save(s => s with { QuietInFocusAssist = _focus.IsChecked == true });
        _rate.ValueChanged += OnSliderChanged;
        _volume.ValueChanged += OnSliderChanged;
        foreach (var name in CloudVoices)
        {
            _cloudVoice.Items.Add(name);
        }

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            _localTts.DownloadProgressChanged += OnLocalDownload;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            _settings.Changed -= OnSettingsChanged;
            _localTts.DownloadProgressChanged -= OnLocalDownload;
            _sliderSave.Stop();
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private StackPanel Build()
    {
        var test = new Button { Content = "Test voice", Padding = new Thickness(10, 2, 10, 2) };
        test.Click += (_, _) => TestVoice();

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Speech", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "How Clicky answers out loud. Press Talk while it speaks to stop it. When it has to stay quiet, the answer shows as a card by the pointer with a Copy button.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(Label("Voice"));
        panel.Children.Add(_quietMode);
        panel.Children.Add(_windows);
        panel.Children.Add(_windowsVoice);
        panel.Children.Add(_local);
        panel.Children.Add(_localVoice);
        panel.Children.Add(_localStatus);
        panel.Children.Add(_cloud);
        panel.Children.Add(_cloudVoice);

        panel.Children.Add(Label("Speed and volume"));
        panel.Children.Add(Row(_rate, _rateLabel));
        panel.Children.Add(Row(_volume, _volumeLabel));

        panel.Children.Add(Label("Speakers"));
        panel.Children.Add(_device);

        panel.Children.Add(Label("Stay quiet and show text instead"));
        panel.Children.Add(new TextBlock { Text = "Always when the sound is muted or no speakers are connected, and also:", Opacity = 0.7, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(_inCalls);
        panel.Children.Add(_sharing);
        panel.Children.Add(_focus);

        panel.Children.Add(new Border { Height = 12 });
        panel.Children.Add(Row(test, _status));
        return panel;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowSliderLabels();
        if (!_syncing)
        {
            _sliderSave.Stop();
            _sliderSave.Start();
        }
    }

    private void ShowSliderLabels()
    {
        _rateLabel.Text = $"{_rate.Value:0.0}×";
        _volumeLabel.Text = $"{_volume.Value:0}%";
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Speech != e.Current.Speech)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void Sync()
    {
        var speech = _settings.Current.Speech;
        _syncing = true;
        try
        {
            _quietMode.IsChecked = speech.QuietMode;
            _windows.IsChecked = speech.Engine == VoiceOutputEngine.Windows;
            _cloud.IsChecked = speech.Engine == VoiceOutputEngine.Cloud;
            _local.IsChecked = speech.Engine == VoiceOutputEngine.Local;
            _localVoice.IsEnabled = speech.Engine == VoiceOutputEngine.Local;
            _windowsVoice.IsEnabled = speech.Engine == VoiceOutputEngine.Windows;
            _cloudVoice.IsEnabled = speech.Engine == VoiceOutputEngine.Cloud;
            _cloudVoice.SelectedItem = CloudVoices.Contains(speech.CloudVoice) ? speech.CloudVoice : null;
            if (!_rate.IsMouseCaptureWithin && !_volume.IsMouseCaptureWithin && !_sliderSave.IsEnabled)
            {
                _rate.Value = speech.Rate;
                _volume.Value = Math.Round(speech.Volume * 100);
            }

            _inCalls.IsChecked = speech.QuietInCalls;
            _sharing.IsChecked = speech.QuietWhenSharing;
            _focus.IsChecked = speech.QuietInFocusAssist;
            SyncVoices(speech.WindowsVoice);
            SyncLocalVoices(speech);
            SyncDevices(speech.OutputDevice);
        }
        finally
        {
            _syncing = false;
        }

        ShowSliderLabels();
    }

    private void SyncVoices(string selectedId)
    {
        IReadOnlyList<VoiceInfo> voices;
        try
        {
            voices = WindowsTextToSpeech.ListVoices();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not list Windows voices");
            voices = [];
        }

        _windowsVoice.Items.Clear();
        _windowsVoice.Items.Add(new ComboBoxItem { Content = DefaultLabel, Tag = string.Empty });
        foreach (var voice in voices)
        {
            _windowsVoice.Items.Add(new ComboBoxItem { Content = $"{voice.Name} ({voice.Language}){(voice.IsDefault ? ", default" : string.Empty)}", Tag = voice.Id });
        }

        _windowsVoice.SelectedItem = _windowsVoice.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selectedId) ?? _windowsVoice.Items[0];
    }

    private void SyncLocalVoices(SpeechSettings speech)
    {
        var model = LocalVoiceModels.Find(speech.LocalModel) ?? LocalVoiceModels.Kokoro;
        _localVoice.Items.Clear();
        foreach (var voice in model.Voices)
        {
            _localVoice.Items.Add(new ComboBoxItem { Content = voice.Label, Tag = voice.Id });
        }

        // A voice set by hand in settings.json (another language, a Piper speaker number) stays selected.
        var selected = string.IsNullOrEmpty(speech.LocalVoice) ? LocalVoiceModels.DefaultVoice : speech.LocalVoice;
        if (model.FindVoice(selected) is null)
        {
            _localVoice.Items.Add(new ComboBoxItem { Content = selected, Tag = selected });
        }

        _localVoice.SelectedItem = _localVoice.Items.Cast<ComboBoxItem>().First(i => string.Equals((string)i.Tag, selected, StringComparison.OrdinalIgnoreCase));
        ShowLocalStatus(speech, _localTts.Download);
    }

    private void ShowLocalStatus(SpeechSettings speech, LocalVoiceDownloadProgress? download)
    {
        var model = LocalVoiceModels.Find(speech.LocalModel);
        _localStatus.Text = (speech.LocalModelPath, download, model) switch
        {
            ({ Length: > 0 } path, _, _) => $"Using the model in {path}.",
            (_, { Unpacking: true }, _) => "Unpacking the voice…",
            (_, { } d, _) => $"Downloading {d.Model.Title}: {d.Fraction:P0} of {d.Model.SizeText}…",
            (_, _, null) => $"Unknown model \"{speech.LocalModel}\" in settings.json.",
            (_, _, { } m) when _localModels.IsAvailable(m) => $"{m.Title} is on this PC ({m.ExtractedSize / 1_000_000:0} MB). Uses about 700 MB of memory while Clicky runs.",
            (_, _, { } m) => $"Downloads {m.Title} ({m.SizeText}) when you pick it; replies show as text until it's ready.",
        };
    }

    private void OnLocalDownload(object? sender, LocalVoiceDownloadProgress? progress) =>
        _dispatcher.InvokeAsync(() => ShowLocalStatus(_settings.Current.Speech, progress));

    private void SyncDevices(string selectedId)
    {
        IReadOnlyList<OutputDevice> devices;
        try
        {
            devices = WasapiSpeaker.ListOutputDevices();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not list output devices");
            devices = [];
        }

        _device.Items.Clear();
        _device.Items.Add(new ComboBoxItem { Content = DefaultLabel, Tag = string.Empty });
        foreach (var device in devices)
        {
            _device.Items.Add(new ComboBoxItem { Content = device.IsDefault ? $"{device.Name} (default)" : device.Name, Tag = device.Id });
        }

        _device.SelectedItem = _device.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selectedId) ?? _device.Items[0];
    }

    private void TestVoice()
    {
        _status.Text = "Speaking…";
        var reply = _replies.Speak(TestText);
        _ = ShowOutcomeAsync(reply);
    }

    private async Task ShowOutcomeAsync(SpokenReply reply)
    {
        var result = await reply.Completion.ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() => _status.Text = result.Outcome switch
        {
            ReplyOutcome.Spoken => $"Spoken; first sound {result.FirstAudio.TotalMilliseconds:0} ms after the first sentence.",
            ReplyOutcome.ShownAsText => $"Shown as text: {result.Problem}",
            ReplyOutcome.Interrupted => "Stopped.",
            _ => result.Problem ?? "The voice failed.",
        });
    }

    private void Save(Func<SpeechSettings, SpeechSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveAsync(change);
    }

    private async Task SaveAsync(Func<SpeechSettings, SpeechSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Speech = change(s.Speech) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save speech settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
