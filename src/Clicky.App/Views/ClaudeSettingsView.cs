using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Views;

/// <summary>
/// The Claude section of Settings: which backend answers (API key or the user's Claude
/// subscription through Claude Code), the key, the fast/deep models, and a Test button that asks a
/// real question about the screen.
/// </summary>
internal sealed class ClaudeSettingsView : ContentControl
{
    private const string TestQuestion = "This is a quick test from Clicky's settings. In one short sentence, say hi and name the app in front. Then point at that app's title bar.";

    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly ClaudeAssistant _assistant;
    private readonly ICaptureService _capture;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<ClaudeSettingsView> _logger;

    private readonly RadioButton _api = new() { Content = "Anthropic API key: fastest answers, billed per use", Margin = new Thickness(0, 2, 0, 2) };
    private readonly RadioButton _subscription = new() { Content = "My Claude subscription (Claude Code): no API cost, a little slower", Margin = new Thickness(0, 2, 0, 2) };
    private readonly TextBlock _backendStatus = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly PasswordBox _key = new() { Width = 320 };
    private readonly TextBlock _keyStatus = new() { Opacity = 0.7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _routing = new() { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBox _apiFast = new() { Width = 200 };
    private readonly TextBox _apiDeep = new() { Width = 200 };
    private readonly TextBox _subscriptionFast = new() { Width = 200 };
    private readonly TextBox _subscriptionDeep = new() { Width = 200 };
    private readonly Button _test = new() { Content = "Test", Padding = new Thickness(10, 2, 10, 2) };
    private readonly TextBlock _testStatus = new() { Opacity = 0.7, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 480 };
    private bool _syncing;

    public ClaudeSettingsView(ISettingsStore settings, ISecretStore secrets, ClaudeAssistant assistant, ICaptureService capture, Dispatcher dispatcher, ILogger<ClaudeSettingsView> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _assistant = assistant;
        _capture = capture;
        _dispatcher = dispatcher;
        _logger = logger;

        _routing.Items.Add(new ComboBoxItem { Content = "Automatic: quick questions fast, explanations deep", Tag = ModelRouting.Auto });
        _routing.Items.Add(new ComboBoxItem { Content = "Always the fast model", Tag = ModelRouting.AlwaysFast });
        _routing.Items.Add(new ComboBoxItem { Content = "Always the deep model", Tag = ModelRouting.AlwaysDeep });

        _api.Checked += (_, _) => Save(c => c with { Backend = ClaudeBackendKind.Api });
        _subscription.Checked += (_, _) => Save(c => c with { Backend = ClaudeBackendKind.Subscription });
        _routing.SelectionChanged += (_, _) =>
        {
            if (_routing.SelectedItem is ComboBoxItem { Tag: ModelRouting routing })
            {
                Save(c => c with { Routing = routing });
            }
        };
        _apiFast.LostFocus += (_, _) => Save(c => c with { ApiFastModel = _apiFast.Text });
        _apiDeep.LostFocus += (_, _) => Save(c => c with { ApiDeepModel = _apiDeep.Text });
        _subscriptionFast.LostFocus += (_, _) => Save(c => c with { SubscriptionFastModel = _subscriptionFast.Text });
        _subscriptionDeep.LostFocus += (_, _) => Save(c => c with { SubscriptionDeepModel = _subscriptionDeep.Text });
        _test.Click += (_, _) => _ = TestAsync();

        Content = Build();
        Loaded += (_, _) =>
        {
            _settings.Changed += OnSettingsChanged;
            Sync();
        };
        Unloaded += (_, _) => _settings.Changed -= OnSettingsChanged;
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Width = 120, VerticalAlignment = VerticalAlignment.Center };

    private StackPanel Build()
    {
        var saveKey = new Button { Content = "Save key", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        saveKey.Click += (_, _) => SaveKey();
        var removeKey = new Button { Content = "Remove", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
        removeKey.Click += (_, _) => RemoveKey();

        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Claude", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock
        {
            Text = "Who answers. Quick questions go to the fast model and explanations, walkthroughs and long questions to the deep one.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
        });

        panel.Children.Add(Label("Answer with"));
        panel.Children.Add(_api);
        panel.Children.Add(_subscription);
        panel.Children.Add(_backendStatus);

        panel.Children.Add(Label("Anthropic API key (API only; kept in Windows Credential Manager)"));
        panel.Children.Add(Row(_key, saveKey, removeKey, _keyStatus));

        panel.Children.Add(Label("Models"));
        panel.Children.Add(_routing);
        panel.Children.Add(new Border { Height = 6 });
        panel.Children.Add(Row(Caption("API fast / deep"), _apiFast, new Border { Width = 8 }, _apiDeep));
        panel.Children.Add(Row(Caption("Subscription"), _subscriptionFast, new Border { Width = 8 }, _subscriptionDeep));

        panel.Children.Add(new Border { Height = 12 });
        panel.Children.Add(Row(_test, _testStatus));
        return panel;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Claude != e.Current.Claude)
        {
            _dispatcher.InvokeAsync(Sync);
        }
    }

    private void Sync()
    {
        var claude = _settings.Current.Claude;
        _syncing = true;
        try
        {
            _api.IsChecked = claude.Backend == ClaudeBackendKind.Api;
            _subscription.IsChecked = claude.Backend == ClaudeBackendKind.Subscription;
            _routing.SelectedItem = _routing.Items.Cast<ComboBoxItem>().First(i => (ModelRouting)i.Tag == claude.Routing);
            SyncText(_apiFast, claude.ApiFastModel);
            SyncText(_apiDeep, claude.ApiDeepModel);
            SyncText(_subscriptionFast, claude.SubscriptionFastModel);
            SyncText(_subscriptionDeep, claude.SubscriptionDeepModel);
        }
        finally
        {
            _syncing = false;
        }

        ShowKeyStatus();
        _ = ShowBackendStatusAsync();
    }

    private static void SyncText(TextBox box, string value)
    {
        if (!box.IsKeyboardFocused)
        {
            box.Text = value;
        }
    }

    private async Task ShowBackendStatusAsync()
    {
        var check = await _assistant.Active.CheckAsync().ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            _backendStatus.Text = check.Message;
            _backendStatus.Foreground = check.Ready ? SystemColors.ControlTextBrush : Brushes.Firebrick;
        });
    }

    private void ShowKeyStatus()
    {
        try
        {
            _keyStatus.Text = string.IsNullOrEmpty(_secrets.Read(SecretNames.AnthropicApiKey))
                ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is { Length: > 0 } ? "Using ANTHROPIC_API_KEY" : "No key saved"
                : "Key saved";
            _keyStatus.Foreground = SystemColors.ControlTextBrush;
        }
        catch (Win32Exception ex)
        {
            _keyStatus.Text = "Could not read Credential Manager";
            _keyStatus.Foreground = Brushes.Firebrick;
            _logger.LogWarning(ex, "Could not read the Anthropic key");
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
            _secrets.Write(SecretNames.AnthropicApiKey, key);
            _key.Clear();
            _logger.LogInformation("Anthropic API key saved to Credential Manager");
        }
        catch (Exception ex) when (ex is Win32Exception or ArgumentException)
        {
            _logger.LogError(ex, "Could not save the Anthropic key");
        }

        Sync();
    }

    private void RemoveKey()
    {
        try
        {
            _secrets.Delete(SecretNames.AnthropicApiKey);
            _logger.LogInformation("Anthropic API key removed from Credential Manager");
        }
        catch (Win32Exception ex)
        {
            _logger.LogError(ex, "Could not remove the Anthropic key");
        }

        Sync();
    }

    /// <summary>Asks the active backend's fast model about the screen, in a throwaway conversation.</summary>
    private async Task TestAsync()
    {
        _test.IsEnabled = false;
        _testStatus.Foreground = SystemColors.ControlTextBrush;
        _testStatus.Text = "Asking Claude…";
        try
        {
            var shot = await _capture.CaptureAsync(new CaptureTarget(CaptureMode.MonitorUnderCursor)).ConfigureAwait(false);
            var request = new ClaudeRequest(TestQuestion, RequestSource.Typed, new RequestScreenshot(shot.Png, shot.Geometry), ModelTier.Fast);
            var actions = new List<string>();
            ReplyCompleted? completed = null;
            string model = string.Empty;
            await foreach (var replyEvent in _assistant.ReplyAsync(new Conversation(), request).ConfigureAwait(false))
            {
                switch (replyEvent)
                {
                    case ReplyStarted started:
                        model = started.Model;
                        break;
                    case ReplyAction { Action: var action }:
                        actions.Add(action is PointAction p ? $"point at ({p.X:0}, {p.Y:0})" : action.Name);
                        break;
                    case ReplyActionRejected rejected:
                        actions.Add($"{rejected.Name} rejected: {rejected.Reason}");
                        break;
                    case ReplyCompleted done:
                        completed = done;
                        break;
                }
            }

            var summary = completed is null
                ? "No reply."
                : $"{completed.Model}: \"{completed.SpokenText.Trim()}\" {(actions.Count > 0 ? string.Join(", ", actions) : "no actions")}. First words after {completed.FirstText?.TotalMilliseconds ?? 0:0} ms, done in {completed.Elapsed.TotalSeconds:0.0} s.";
            _logger.LogInformation("Claude test with {Model}: {Summary}", model, summary);
            await _dispatcher.InvokeAsync(() => _testStatus.Text = summary);
        }
        catch (Exception ex) when (ex is ClaudeBackendException or CaptureException)
        {
            _logger.LogWarning(ex, "Claude test failed");
            await _dispatcher.InvokeAsync(() =>
            {
                _testStatus.Text = ex.Message;
                _testStatus.Foreground = Brushes.Firebrick;
            });
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => _test.IsEnabled = true);
        }
    }

    private void Save(Func<ClaudeSettings, ClaudeSettings> change)
    {
        if (_syncing)
        {
            return;
        }

        _ = SaveAsync(change);
    }

    private async Task SaveAsync(Func<ClaudeSettings, ClaudeSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { Claude = change(s.Claude) }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save Claude settings");
        }

        await _dispatcher.InvokeAsync(Sync);
    }
}
