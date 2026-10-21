using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Agent;
using Clicky.Core.Geometry;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Agent;

/// <summary>
/// The agent card: what Clicky is doing on the computer, a Stop button, the 5 s "About to…"
/// countdown and the permission question (Allow once / Always for this conversation / Not now). It
/// sits in a bottom corner of the monitor being worked on, never takes focus (so typing still goes
/// to the user's app) and is left out of screenshots, so the model never sees or clicks it.
/// </summary>
/// <remarks>Create and use on the UI thread; <see cref="AgentPresenter"/> marshals for the agent.</remarks>
internal sealed class AgentCard : Window
{
    private const double CardWidth = 360;
    private const int EdgeGap = 16;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush WarningBack = new SolidColorBrush(Color.FromRgb(0xFE, 0xF2, 0xF2));

    private readonly ILogger _logger;
    private readonly PixelRect? _near;
    private readonly TextBlock _step;
    private readonly StackPanel _countdown;
    private readonly TextBlock _countdownText;
    private readonly ProgressBar _countdownBar;
    private readonly StackPanel _question;
    private readonly TextBlock _questionText;
    private readonly TextBlock _questionDetail;
    private readonly Border _irreversible;
    private readonly Button _allow;
    private readonly Button _always;
    private readonly Button _stop;
    private readonly DispatcherTimer _tick;
    private TaskCompletionSource<bool>? _countdownResult;
    private TaskCompletionSource<PermissionAnswer>? _answer;
    private DateTime _countdownEnds;
    private TimeSpan _countdownLength;
    private bool _onLeft;
    private nint _hwnd;

    public AgentCard(string task, PixelRect? near, ILogger logger)
    {
        _logger = logger;
        _near = near;

        Title = "Clicky is working";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;

        _stop = MakeButton("Stop", Danger, bold: true);
        _stop.Click += (_, _) => StopClicked?.Invoke();
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(_stop, Dock.Right);
        header.Children.Add(_stop);
        header.Children.Add(new TextBlock
        {
            Text = $"Clicky is doing: {task}",
            Foreground = Muted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 32,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _step = new TextBlock { Text = "Getting started…", Foreground = Ink, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };

        _countdownText = new TextBlock { Foreground = Ink, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        _countdownBar = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Foreground = Accent, BorderThickness = new Thickness(0) };
        var cancel = MakeButton("Cancel", Ink);
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 8, 0, 0);
        cancel.Click += (_, _) => EndCountdown(goAhead: false);
        _countdown = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _countdown.Children.Add(_countdownText);
        _countdown.Children.Add(_countdownBar);
        _countdown.Children.Add(cancel);

        _questionText = new TextBlock { Foreground = Ink, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _questionDetail = new TextBlock { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxHeight = 120, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        _irreversible = new Border
        {
            Background = WarningBack,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(0, 6, 0, 0),
            Child = new TextBlock { Text = "This can't be undone.", Foreground = Danger, FontSize = 12 },
        };
        _allow = MakeButton("Allow once", Accent, bold: true);
        _allow.Click += (_, _) => Answer(PermissionAnswer.AllowOnce);
        _always = MakeButton("Always for this conversation", Ink);
        _always.Click += (_, _) => Answer(PermissionAnswer.AlwaysThisConversation);
        var notNow = MakeButton("Not now", Ink);
        notNow.Click += (_, _) => Answer(PermissionAnswer.NotNow);
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var button in new[] { _allow, _always, notNow })
        {
            button.Margin = new Thickness(0, 0, 6, 4);
            buttons.Children.Add(button);
        }

        _question = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _question.Children.Add(_questionText);
        _question.Children.Add(_questionDetail);
        _question.Children.Add(_irreversible);
        _question.Children.Add(buttons);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_step);
        panel.Children.Add(_countdown);
        panel.Children.Add(_question);

        Content = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 10, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect { BlurRadius = 14, Opacity = 0.25, ShadowDepth = 2, Direction = 300 },
            Child = panel,
        };

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += (_, _) => OnTick();
        SizeChanged += (_, _) => Place();
        Closed += (_, _) =>
        {
            _tick.Stop();
            EndCountdown(goAhead: false);
            Answer(PermissionAnswer.NotNow);
        };
    }

    /// <summary>Stop was pressed.</summary>
    public event Action? StopClicked;

    /// <summary>The card's bounds in physical pixels, or null before it is placed.</summary>
    public PixelRect? Bounds { get; private set; }

    public void ShowStep(string text) => _step.Text = text;

    public Task<bool> StartCountdown(string upcoming, TimeSpan duration)
    {
        EndCountdown(goAhead: false);
        _countdownResult = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _countdownLength = duration;
        _countdownEnds = DateTime.UtcNow + duration;
        _countdownText.Text = $"About to: {upcoming}";
        _countdownBar.Value = 1;
        _countdown.Visibility = Visibility.Visible;
        _tick.Start();
        return _countdownResult.Task;
    }

    public void EndCountdown(bool goAhead)
    {
        _tick.Stop();
        _countdown.Visibility = Visibility.Collapsed;
        var result = _countdownResult;
        _countdownResult = null;
        result?.TrySetResult(goAhead);
    }

    public Task<PermissionAnswer> Ask(PermissionPrompt prompt)
    {
        Answer(PermissionAnswer.NotNow);
        _answer = new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _questionText.Text = prompt.Question;
        _questionDetail.Text = prompt.Detail ?? string.Empty;
        _questionDetail.Visibility = string.IsNullOrWhiteSpace(prompt.Detail) ? Visibility.Collapsed : Visibility.Visible;
        _irreversible.Visibility = prompt.Irreversible ? Visibility.Visible : Visibility.Collapsed;
        _allow.Content = prompt.AllowLabel ?? "Allow once";
        _always.Visibility = prompt.OffersAlways ? Visibility.Visible : Visibility.Collapsed;
        _question.Visibility = Visibility.Visible;
        return _answer.Task;
    }

    public void Answer(PermissionAnswer answer)
    {
        _question.Visibility = Visibility.Collapsed;
        var pending = _answer;
        _answer = null;
        pending?.TrySetResult(answer);
    }

    public void ShowFinished(string message, bool success)
    {
        EndCountdown(goAhead: false);
        Answer(PermissionAnswer.NotNow);
        _stop.Visibility = Visibility.Collapsed;
        _step.Text = message;
        _step.Foreground = success ? Ink : Danger;
    }

    /// <summary>Moves to the other bottom corner if the card covers <paramref name="point"/>.</summary>
    public void Avoid(PixelPoint point)
    {
        if (Bounds is { } bounds && bounds.DistanceSquaredTo(point) <= EdgeGap * EdgeGap)
        {
            _onLeft = !_onLeft;
            Place();
            _logger.LogDebug("Agent card moved out of the way of {Point}", point);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        FloatingToolWindow.Apply(_hwnd, _logger);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    private static Button MakeButton(string text, Brush foreground, bool bold = false) => new()
    {
        Content = text,
        Foreground = foreground,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Padding = new Thickness(12, 3, 12, 3),
        Focusable = false,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private void OnTick()
    {
        var left = _countdownEnds - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            EndCountdown(goAhead: true);
            return;
        }

        _countdownBar.Value = _countdownLength > TimeSpan.Zero ? left / _countdownLength : 0;
    }

    /// <summary>Bottom right (or left) of the work area of the monitor being worked on.</summary>
    private void Place()
    {
        if (_hwnd == 0 || ActualWidth <= 0)
        {
            return;
        }

        var monitors = DisplayMonitors.GetAll();
        var centre = _near is { } near ? new PixelPoint((near.Left + near.Right) / 2, (near.Top + near.Bottom) / 2) : (PixelPoint?)null;
        var monitor = (centre is { } c ? monitors.FirstOrDefault(m => m.Bounds.Contains(c)) : null)
            ?? monitors.FirstOrDefault(m => m.IsPrimary)
            ?? (monitors.Count > 0 ? monitors[0] : null);
        if (monitor is null)
        {
            return;
        }

        var width = (int)Math.Ceiling(ActualWidth * monitor.Scale);
        var height = (int)Math.Ceiling(ActualHeight * monitor.Scale);
        var work = monitor.WorkArea;
        var x = _onLeft ? work.Left : Math.Max(work.Left, work.Right - width);
        var y = Math.Max(work.Top, work.Bottom - height);
        FloatingToolWindow.MoveTo(_hwnd, new PixelPoint(x, y));
        Bounds = new PixelRect(x, y, x + width, y + height);
    }
}
