using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clicky.App.Hosting;
using Clicky.Core.Assistant;
using Clicky.Core.Hotkeys;
using Clicky.Core.Onboarding;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Path = System.Windows.Shapes.Path;

namespace Clicky.App.Views;

/// <summary>
/// First-run onboarding: welcome, connect Claude, microphone, then the hands-on tutorial (say hello,
/// watch it draw, circle something, dictate) and a finish page with the shortcuts. Practice steps
/// complete when the real feature is seen working: Talk answers arrive through
/// <see cref="TalkAnswerer.Answered"/>, dictation lands in the practice box. Finishing or closing
/// the window marks onboarding done; Settings › General › Take the tour again reopens it.
/// </summary>
internal sealed class OnboardingWindow : Window
{
    private const string TryQuestion = "This is Clicky's setup checking the connection. Reply with one short, friendly sentence and no actions.";

    private static readonly Brush SuccessBrush = Frozen(Color.FromRgb(0x1E, 0x7D, 0x3A));
    private static readonly Brush AccentBrush = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));

    private readonly OnboardingFlow _flow = new();
    private readonly IServiceProvider _services;
    private readonly ISettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly ClaudeAssistant _assistant;
    private readonly TalkAnswerer _answerer;
    private readonly IAppShell _shell;
    private readonly Dispatcher _dispatcher;
    private readonly ILogger<OnboardingWindow> _logger;

    private readonly TextBlock _position = new() { Opacity = 0.6 };
    private readonly ProgressBar _progress = new() { Height = 4, Maximum = OnboardingFlow.StepCount, Margin = new Thickness(0, 6, 0, 14), BorderThickness = new Thickness(0), Foreground = AccentBrush };
    private readonly TextBlock _title = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl _page = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), FontWeight = FontWeights.SemiBold };
    private readonly Button _back = new() { Content = "Back", Padding = new Thickness(14, 4, 14, 4), MinWidth = 90 };
    private readonly Button _next = new() { Padding = new Thickness(14, 4, 14, 4), MinWidth = 110, IsDefault = true, Margin = new Thickness(8, 0, 0, 0) };
    private readonly CheckBox _startWithWindows = new() { Content = "Start Clicky when I sign in to Windows", IsChecked = true, Margin = new Thickness(0, 16, 0, 0) };

    // Built once so the Claude and voice pages keep their state when going Back.
    private readonly Dictionary<OnboardingStep, FrameworkElement> _pages = [];
    private bool _saved;

    public OnboardingWindow(
        IServiceProvider services,
        ISettingsStore settings,
        ISecretStore secrets,
        ClaudeAssistant assistant,
        TalkAnswerer answerer,
        IAppShell shell,
        Dispatcher dispatcher,
        ILogger<OnboardingWindow> logger)
    {
        _services = services;
        _settings = settings;
        _secrets = secrets;
        _assistant = assistant;
        _answerer = answerer;
        _shell = shell;
        _dispatcher = dispatcher;
        _logger = logger;

        Title = "Welcome to Clicky";
        Width = 760;
        Height = 640;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/clicky.ico", UriKind.Absolute));
        Content = Build();

        _back.Click += (_, _) => _flow.Back();
        _next.Click += (_, _) => OnNext();
        _flow.Changed += (_, _) => Render();

        Loaded += (_, _) =>
        {
            _answerer.Answered += OnAnswered;
            _logger.LogInformation("Onboarding opened");
            Render();
        };
        Closed += (_, _) =>
        {
            _answerer.Answered -= OnAnswered;
            _logger.LogInformation("Onboarding closed at {Step} with {Done}/{Count} practice steps done", _flow.Step, _flow.PracticeDone, OnboardingFlow.PracticeCount);
            _ = SaveDoneAsync(startWithWindows: null);
        };
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static TextBlock Text(string text, double opacity = 1) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity, Margin = new Thickness(0, 0, 0, 8) };

    private static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.Talk => "Ask about your screen",
        HotkeyAction.TextMode => "Type a question",
        HotkeyAction.Dictation => "Dictate into any app",
        HotkeyAction.DictationHandsFree => "Dictate hands-free (again to stop)",
        HotkeyAction.OpenHome => "Open Home",
        HotkeyAction.AlwaysOnVoice => "Always-on voice",
        _ => action.ToString(),
    };

    private Grid Build()
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);

        var header = new StackPanel();
        header.Children.Add(_position);
        header.Children.Add(_progress);
        header.Children.Add(_title);

        var scroll = new ScrollViewer { Content = _page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var grid = new Grid { Margin = new Thickness(28, 20, 28, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(header);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        Grid.SetRow(_feedback, 2);
        grid.Children.Add(_feedback);
        Grid.SetRow(buttons, 3);
        buttons.Margin = new Thickness(0, 16, 0, 0);
        grid.Children.Add(buttons);
        return grid;
    }

    private void Render()
    {
        var step = _flow.Step;
        _position.Text = OnboardingFlow.IsPractice(step)
            ? $"Step {_flow.Position} of {OnboardingFlow.StepCount} · practice {_flow.PracticeDone} of {OnboardingFlow.PracticeCount} done"
            : $"Step {_flow.Position} of {OnboardingFlow.StepCount}";
        _progress.Value = _flow.Position;
        _title.Text = TitleOf(step);

        if (!_pages.TryGetValue(step, out var page))
        {
            page = BuildPage(step);
            _pages[step] = page;
        }

        if (!ReferenceEquals(_page.Content, page))
        {
            _page.Content = page;
            if (page.Tag is TextBox practiceBox)
            {
                _dispatcher.InvokeAsync(() => practiceBox.Focus(), DispatcherPriority.Input);
            }
        }

        _feedback.Text = _flow.Feedback;
        _feedback.Foreground = _flow.FeedbackKind switch
        {
            FeedbackKind.Success => SuccessBrush,
            FeedbackKind.Problem => Brushes.Firebrick,
            _ => SystemColors.ControlTextBrush,
        };
        _feedback.Visibility = _flow.Feedback.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        _back.Visibility = _flow.CanGoBack ? Visibility.Visible : Visibility.Hidden;
        _next.Content = _flow.IsLast ? "Done" : step == OnboardingStep.Welcome ? "Let's go" : OnboardingFlow.IsPractice(step) && !_flow.IsStepDone ? "Skip for now" : "Next";
        _next.FontWeight = _flow.IsLast || !OnboardingFlow.IsPractice(step) || _flow.IsStepDone ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private static string TitleOf(OnboardingStep step) => step switch
    {
        OnboardingStep.Welcome => "Hi, I'm Clicky",
        OnboardingStep.ConnectClaude => "Connect Claude",
        OnboardingStep.Voice => "Check your microphone",
        OnboardingStep.SayHello => "Say hello",
        OnboardingStep.WatchItDraw => "Watch it draw",
        OnboardingStep.CircleSomething => "Circle something",
        OnboardingStep.Dictate => "Dictate",
        OnboardingStep.Finish => "You're all set",
        _ => step.ToString(),
    };

    private FrameworkElement BuildPage(OnboardingStep step) => step switch
    {
        OnboardingStep.Welcome => WelcomePage(),
        OnboardingStep.ConnectClaude => new ConnectClaudePage(_settings, _secrets, _assistant, _dispatcher, _logger),
        OnboardingStep.Voice => VoicePage(),
        OnboardingStep.SayHello => PracticePage(
            $"{Gesture(HotkeyAction.Talk)}, say “Hello Clicky”, then let go.",
            "The buddy by your pointer pulses while it listens, thinks, then answers out loud."),
        OnboardingStep.WatchItDraw => PracticePage(
            $"{Gesture(HotkeyAction.Talk)} and say “Point at the blue star.”",
            "Clicky looks at your screen and draws on it to show you where things are.",
            Shapes()),
        OnboardingStep.CircleSomething => PracticePage(
            $"{Gesture(HotkeyAction.Talk)}, loop the mouse pointer once around the red heart, and ask “What's this?”",
            "While you hold the keys, your pointer leaves a pink trail. Clicky sees what you circled, so “this” means that thing.",
            Shapes()),
        OnboardingStep.Dictate => DictatePage(),
        OnboardingStep.Finish => FinishPage(),
        _ => new TextBlock(),
    };

    private StackPanel WelcomePage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("I sit next to your mouse pointer. Hold a shortcut, ask about anything on your screen, and I answer out loud and point at things. I can also type what you say into any app."));
        panel.Children.Add(Text("This takes about two minutes: connect Claude, check the mic, then four quick practice steps. You can skip anything.", 0.7));
        panel.Children.Add(Shortcuts());
        return panel;
    }

    private StackPanel VoicePage()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Pick the microphone you talk into. Speech-to-text runs on this PC by default; its model downloads in the background the first time, and the status below shows how far it is."));
        try
        {
            panel.Children.Add(_services.GetRequiredService<VoiceSettingsView>());
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Onboarding: could not show the voice settings");
            panel.Children.Add(Text("Voice settings couldn't load here; you can set them later in Settings › Voice.", 0.7));
        }

        return panel;
    }

    private static StackPanel PracticePage(string instruction, string explanation, UIElement? extra = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = instruction, FontSize = 16, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(Text(explanation, 0.7));
        if (extra is not null)
        {
            panel.Children.Add(extra);
        }

        return panel;
    }

    private StackPanel DictatePage()
    {
        var box = new TextBox
        {
            MinHeight = 120,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6),
            FontSize = 14,
        };
        box.TextChanged += (_, _) => _flow.ObserveDictation(box.Text);

        var panel = PracticePage(
            $"Click in the box, {Lower(Gesture(HotkeyAction.Dictation))} and say a sentence. Let go and it types itself.",
            $"Dictation works in any app with a text box. {Gesture(HotkeyAction.DictationHandsFree)} to keep dictating hands-free.",
            box);
        panel.Tag = box;
        return panel;
    }

    private StackPanel FinishPage()
    {
        var openSettings = new Button { Content = "Open Settings", Padding = new Thickness(10, 2, 10, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        openSettings.Click += (_, _) => _shell.ShowSettings();

        var panel = new StackPanel();
        panel.Children.Add(Text("Clicky lives in the notification area: right-click its icon for Settings, a new conversation or quiet mode. You can take this tour again from Settings › General."));
        panel.Children.Add(Shortcuts());
        panel.Children.Add(_startWithWindows);
        panel.Children.Add(openSettings);
        return panel;
    }

    private Grid Shortcuts()
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var action in new[] { HotkeyAction.Talk, HotkeyAction.TextMode, HotkeyAction.Dictation, HotkeyAction.DictationHandsFree, HotkeyAction.OpenHome })
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = Gesture(action), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 12, 3) };
            var what = new TextBlock { Text = Describe(action), Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(key, row);
            Grid.SetRow(what, row);
            Grid.SetColumn(what, 1);
            grid.Children.Add(key);
            grid.Children.Add(what);
            row++;
        }

        return grid;
    }

    /// <summary>The user's own shortcut for an action ("Hold Ctrl+Alt"), so the tutorial matches after rebinding.</summary>
    private string Gesture(HotkeyAction action)
    {
        var binding = _settings.Current.Hotkeys.Bindings.FirstOrDefault(b => b.Action == action);
        return binding is { Enabled: true } && HotkeyGesture.TryParse(binding.Gesture, out var gesture)
            ? gesture.ToDisplayString()
            : $"(no shortcut set for {Describe(action).ToLowerInvariant()}; set one in Settings › Shortcuts)";
    }

    private static string Lower(string text) => text.Length > 0 && char.IsUpper(text[0]) ? char.ToLowerInvariant(text[0]) + text[1..] : text;

    /// <summary>Things to point at and circle: a blue star, a red heart and a green triangle.</summary>
    private static Canvas Shapes()
    {
        var canvas = new Canvas { Height = 170, Margin = new Thickness(0, 12, 0, 0) };
        Add(canvas, StarGeometry(), Color.FromRgb(0x25, 0x63, 0xEB), 40, 20);
        Add(canvas, Geometry.Parse("M 60,110 C 60,110 0,70 0,35 C 0,10 30,-5 60,25 C 90,-5 120,10 120,35 C 120,70 60,110 60,110 Z"), Color.FromRgb(0xDC, 0x26, 0x26), 250, 25);
        Add(canvas, Geometry.Parse("M 60,0 L 120,110 L 0,110 Z"), Color.FromRgb(0x16, 0xA3, 0x4A), 460, 20);
        return canvas;

        static void Add(Canvas canvas, Geometry geometry, Color color, double left, double top)
        {
            var shape = new Path { Data = geometry, Fill = new SolidColorBrush(color), Stroke = Brushes.Black, StrokeThickness = 1.5 };
            Canvas.SetLeft(shape, left);
            Canvas.SetTop(shape, top);
            canvas.Children.Add(shape);
        }
    }

    private static StreamGeometry StarGeometry()
    {
        const double cx = 60, cy = 60, outer = 60, inner = 25;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < 10; i++)
            {
                var r = i % 2 == 0 ? outer : inner;
                var angle = (-Math.PI / 2) + (i * Math.PI / 5);
                var point = new Point(cx + (r * Math.Cos(angle)), cy + (r * Math.Sin(angle)));
                if (i == 0)
                {
                    ctx.BeginFigure(point, isFilled: true, isClosed: true);
                }
                else
                {
                    ctx.LineTo(point, isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void OnAnswered(object? sender, TalkAnswered e)
    {
        var talk = TutorialTalk.From(e.Question, e.Result);
        _dispatcher.InvokeAsync(() => _flow.Observe(talk));
    }

    private void OnNext()
    {
        if (!_flow.IsLast)
        {
            _flow.Next();
            return;
        }

        _ = SaveDoneAsync(_startWithWindows.IsChecked == true);
        Close();
    }

    /// <param name="startWithWindows">Null when the window was closed without finishing: leave the setting alone.</param>
    private async Task SaveDoneAsync(bool? startWithWindows)
    {
        if (_saved)
        {
            return;
        }

        _saved = true;
        try
        {
            await _settings.UpdateAsync(s => s with
            {
                Onboarding = OnboardingSettings.Done,
                General = startWithWindows is { } start ? s.General with { StartWithWindows = start } : s.General,
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not save that onboarding is done");
        }
    }

    /// <summary>Pick the backend, add a key if it is the API, and try a real question.</summary>
    private sealed class ConnectClaudePage : StackPanel
    {
        private readonly ISettingsStore _settings;
        private readonly ISecretStore _secrets;
        private readonly ClaudeAssistant _assistant;
        private readonly Dispatcher _dispatcher;
        private readonly ILogger _logger;

        private readonly RadioButton _subscription = new() { Content = "My Claude subscription, through Claude Code (no API cost)", Margin = new Thickness(0, 2, 0, 2) };
        private readonly RadioButton _api = new() { Content = "An Anthropic API key (fastest answers, billed per use)", Margin = new Thickness(0, 2, 0, 2) };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private readonly StackPanel _keyRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        private readonly PasswordBox _key = new() { Width = 320 };
        private readonly Button _try = new() { Content = "Try it", Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0) };
        private readonly TextBlock _tryResult = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        private bool _syncing;

        public ConnectClaudePage(ISettingsStore settings, ISecretStore secrets, ClaudeAssistant assistant, Dispatcher dispatcher, ILogger logger)
        {
            _settings = settings;
            _secrets = secrets;
            _assistant = assistant;
            _dispatcher = dispatcher;
            _logger = logger;

            var saveKey = new Button { Content = "Save key", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(8, 0, 0, 0) };
            saveKey.Click += (_, _) => SaveKey();
            _keyRow.Children.Add(_key);
            _keyRow.Children.Add(saveKey);

            _subscription.Checked += (_, _) => SaveBackend(ClaudeBackendKind.Subscription);
            _api.Checked += (_, _) => SaveBackend(ClaudeBackendKind.Api);
            _try.Click += (_, _) => _ = TryAsync();

            Children.Add(Text("Clicky's answers come from Claude. Use your Claude subscription through Claude Code, or an API key from console.anthropic.com. You can switch any time in Settings › Claude."));
            Children.Add(_subscription);
            Children.Add(_api);
            Children.Add(_keyRow);
            Children.Add(_status);
            Children.Add(_try);
            Children.Add(_tryResult);

            Loaded += (_, _) => Sync();
        }

        private void Sync()
        {
            var backend = _settings.Current.Claude.Backend;
            _syncing = true;
            try
            {
                _subscription.IsChecked = backend == ClaudeBackendKind.Subscription;
                _api.IsChecked = backend == ClaudeBackendKind.Api;
                _keyRow.Visibility = backend == ClaudeBackendKind.Api ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _syncing = false;
            }

            _ = CheckAsync();
        }

        private async Task CheckAsync()
        {
            var check = await _assistant.Active.CheckAsync().ConfigureAwait(false);
            var hint = _settings.Current.Claude.Backend == ClaudeBackendKind.Subscription && check.Ready
                ? " If Try it says you're not signed in, run claude in a terminal once and sign in."
                : string.Empty;
            await _dispatcher.InvokeAsync(() =>
            {
                _status.Text = check.Message + hint;
                _status.Foreground = check.Ready ? SystemColors.ControlTextBrush : Brushes.Firebrick;
            });
        }

        private void SaveBackend(ClaudeBackendKind backend)
        {
            if (!_syncing)
            {
                _ = SaveBackendAsync(backend);
            }
        }

        private async Task SaveBackendAsync(ClaudeBackendKind backend)
        {
            try
            {
                await _settings.UpdateAsync(s => s with { Claude = s.Claude with { Backend = backend } }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Onboarding: could not save the Claude backend");
            }

            await _dispatcher.InvokeAsync(Sync);
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
                _logger.LogInformation("Onboarding: Anthropic API key saved to Credential Manager");
            }
            catch (Exception ex) when (ex is Win32Exception or ArgumentException)
            {
                _logger.LogError(ex, "Onboarding: could not save the Anthropic key");
            }

            Sync();
        }

        /// <summary>A real round trip on the fast model, in a throwaway conversation, without a screenshot.</summary>
        private async Task TryAsync()
        {
            _try.IsEnabled = false;
            _tryResult.Foreground = SystemColors.ControlTextBrush;
            _tryResult.Text = "Asking Claude…";
            try
            {
                var request = new ClaudeRequest(TryQuestion, RequestSource.Typed, Tier: ModelTier.Fast);
                ReplyCompleted? completed = null;
                await foreach (var replyEvent in _assistant.ReplyAsync(new Conversation(), request).ConfigureAwait(false))
                {
                    if (replyEvent is ReplyCompleted done)
                    {
                        completed = done;
                    }
                }

                var said = completed?.SpokenText.Trim() ?? string.Empty;
                _logger.LogInformation("Onboarding: Claude answered in {Seconds:0.0} s", completed?.Elapsed.TotalSeconds ?? 0);
                await _dispatcher.InvokeAsync(() =>
                {
                    _tryResult.Text = said.Length == 0 ? "Connected, but the reply was empty. Try again." : $"Connected. Claude says: “{said}”";
                    _tryResult.Foreground = said.Length == 0 ? SystemColors.ControlTextBrush : SuccessBrush;
                });
            }
            catch (ClaudeBackendException ex)
            {
                _logger.LogWarning(ex, "Onboarding: Claude test failed");
                await _dispatcher.InvokeAsync(() =>
                {
                    _tryResult.Text = ex.Message;
                    _tryResult.Foreground = Brushes.Firebrick;
                });
            }
            finally
            {
                await _dispatcher.InvokeAsync(() => _try.IsEnabled = true);
            }
        }
    }
}
