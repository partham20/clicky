using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Clicky.Core.Voice;
using Clicky.Platform.Windows.Audio;
using Clicky.Platform.Windows.Speech;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Voice section of Settings: microphone, speech-to-text engine, language and the OpenAI key
/// for the cloud engine. Settings go to settings.json; the key goes to Credential Manager.
/// </summary>
internal sealed class VoiceSettingsView : ContentControl
{
    private const string DefaultDeviceLabel = "Windows default";

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly WhisperSpeechToText _whisper;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<VoiceSettingsView> _logger;

    private readonly ComboBox _device = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly RadioButton _local = new() { Content = "On this PC (Whisper): free, offline, audio stays here", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _cloud = new() { Content = "Cloud (OpenAI): needs an API key, sends audio to OpenAI", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBox _language = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PasswordBox _key = new() { Width = 320 };
    private readonly TextBlock _keyStatus = new() { Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _engineStatus = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private bool _syncing;

    public VoiceSettingsView(ISettingsStore settings, ISecretStore secrets, WhisperSpeechToText whisper, Dispatcher dispatcher, ILogger<VoiceSettingsView> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _whisper = whisper;
        _dispatcher = dispatcher;
        _logger = logger;

        _local.Checked += (_, _) => Save(v => v with { Engine = SpeechEngine.Local });
        _cloud.Checked += (_, _) => Save(v => v with { Engine = SpeechEngine.Cloud });
        _device.SelectionChanged += (_, _) =>
        {
            if (_device.SelectedItem is ComboBoxItem { Tag: string id })
            {
                Save(v => v with { InputDevice = id });
            }
        };
        _language.LostFocus += (_, _) => Save(v => v with { Language = _language.Text });

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            _whisper.DownloadProgressChanged += OnDownloadProgress;
            Sync();
        };
        Unloaded += (_, _) =>
        {
            _settings.Changed -= OnSettingsChanged;
            _whisper.DownloadProgressChanged -= OnDownloadProgress;
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private StackPanel Build()
    {
        var saveKey = new Button { Content = "Save key", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        saveKey.Click += (_, _) => SaveKey();
        var removeKey = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        removeKey.Click += (_, _) => RemoveKey();

        var keyRow = new StackPanel { Orientation = Orientation.Horizontal };
        keyRow.Children.Add(_key);
        keyRow.Children.Add(saveKey);
        keyRow.Children.Add(removeKey);
        keyRow.Children.Add(_keyStatus);

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Voice", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Hold the Talk hotkey and speak; Clicky shows what it heard beside the buddy.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(Label("Microphone"));
        panel.Children.Add(_device);

        panel.Children.Add(Label("Speech-to-text"));
        panel.Children.Add(_local);
        panel.Children.Add(_cloud);
        panel.Children.Add(_engineStatus);

        panel.Children.Add(Label("Language (en, hi, ta, … or auto)"));
        panel.Children.Add(_language);

        panel.Children.Add(Label("OpenAI API key (cloud only)"));
        panel.Children.Add(keyRow);
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Voice != e.Current.Voice)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void OnDownloadProgress(object? sender, ModelDownloadProgress? progress) =>
        _dispatcher.InvokeAsync(() => ShowEngineStatus(progress));

    private void Sync()
    {
        var voice = _settings.Current.Voice;
        _syncing = true;
        try
        {
            _local.IsChecked = voice.Engine == SpeechEngine.Local;
            _cloud.IsChecked = voice.Engine == SpeechEngine.Cloud;
            if (!_language.IsKeyboardFocused)
            {
                _language.Text = voice.Language;
            }

            SyncDevices(voice.InputDevice);
        }
        finally
        {
            _syncing = false;
        }

        ShowEngineStatus(null);
        ShowKeyStatus();
    }

    private void SyncDevices(string selectedId)
    {
        IReadOnlyList<InputDevice> devices;
        try
        {
            devices = WasapiMicrophone.ListInputDevices();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not list input devices");
            devices = [];
        }

        _device.Items.Clear();
        _device.Items.Add(new ComboBoxItem { Content = DefaultDeviceLabel, Tag = string.Empty });
        foreach (var device in devices)
        {
            _device.Items.Add(new ComboBoxItem { Content = device.IsDefault ? $"{device.Name} (default)" : device.Name, Tag = device.Id });
        }

        _device.SelectedItem = _device.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selectedId) ?? _device.Items[0];
    }

    private void ShowEngineStatus(ModelDownloadProgress? download)
    {
        var voice = _settings.Current.Voice;
        _engineStatus.Text = voice.Engine == SpeechEngine.Cloud
            ? $"Using OpenAI {voice.CloudModel}."
            : download is { } d
                ? $"Downloading {d.Model.Name}: {d.Fraction:0%} of {d.Model.SizeText}."
                : $"Using {_whisper.Description}. GPU: {(voice.UseGpu ? "on when available" : "off")} (voice.useGpu, applies on restart).";
    }

    private void ShowKeyStatus()
    {
        try
        {
            _keyStatus.Text = string.IsNullOrEmpty(_secrets.Read(SecretNames.OpenAiApiKey)) ? "No key saved" : "Key saved";
            _keyStatus.Foreground = SystemColors.ControlTextBrush;
        }
        catch (Win32Exception ex)
        {
            _keyStatus.Text = "Could not read Credential Manager";
            _keyStatus.Foreground = Brushes.Firebrick;
            _logger.LogWarning(ex, "Could not read the OpenAI key");
        }
    }

    private void SaveKey()
    {
        var key = _key.Password.Trim();
        if (key.Length == 0)
        {
            return;
        }

        try
        {
            _secrets.Write(SecretNames.OpenAiApiKey, key);
            _key.Clear();
            _logger.LogInformation("OpenAI API key saved to Credential Manager");
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _logger.LogError(ex, "Could not save the OpenAI key");
        }

        ShowKeyStatus();
    }

    private void RemoveKey()
    {
        try
        {
            _secrets.Delete(SecretNames.OpenAiApiKey);
            _logger.LogInformation("OpenAI API key removed from Credential Manager");
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Could not remove the OpenAI key");
        }

        ShowKeyStatus();
    }

    private void Save(Func<VoiceSettings, VoiceSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveAsync(change);
    }

    private async Task SaveAsync(Func<VoiceSettings, VoiceSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Voice = change(s.Voice) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save voice settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
