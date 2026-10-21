using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Geometry;
using Clicky.Core.Suggestions;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Suggestions;

/// <summary>What the card asks its owner to do; called on the UI thread.</summary>
internal interface ISuggestionCardHost
{
    void DoIt(Suggestion suggestion);

    void Next(Suggestion suggestion);

    /// <summary>The rewritten idea, or null to keep the old one (the host tells the user why).</summary>
    Task<Suggestion?> AdjustAsync(Suggestion suggestion, string request);

    /// <summary>Closed with ✕ on <paramref name="current"/>; <paramref name="unseen"/> were never shown.</summary>
    void Closed(Suggestion current, IReadOnlyList<Suggestion> unseen);

    /// <summary>Went away on its own with no reaction to <paramref name="remaining"/>.</summary>
    void TimedOut(IReadOnlyList<Suggestion> remaining);
}

/// <summary>
/// The morning card: today's ideas one at a time, bottom right above the tray, with Do it, Next
/// and Adjust. Like the reply card it never takes focus, stays out of screenshots and screen
/// shares, and goes away by itself after a while (not while the mouse is over it).
/// </summary>
/// <remarks>Create and use on the UI thread.</remarks>
internal sealed class SuggestionCard : Window
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    private const double CardWidth = 400;
    private const double EdgeGap = 12;

    private static readonly Brush Ink = Frozen(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));

    private readonly List<Suggestion> _ideas;
    private readonly ISuggestionCardHost _host;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _lifetime;
    private readonly TextBlock _header = new() { Foreground = Muted, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _title = new() { Foreground = Ink, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBlock _detail = new() { Foreground = Ink, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 };
    private readonly TextBlock _why = new() { Foreground = Muted, FontSize = 12.5, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _sources = new() { Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _doIt;
    private readonly Button _next;
    private readonly Button _adjust;
    private int _index;
    private bool _reacted;
    private bool _finished;
    private nint _hwnd;

    /// <param name="greeting">"Good morning" for the morning card, "Today's ideas" from the tray.</param>
    public SuggestionCard(IReadOnlyList<Suggestion> ideas, string greeting, ISuggestionCardHost host, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfZero(ideas.Count);
        _ideas = [.. ideas];
        _host = host;
        _logger = logger;
        Greeting = greeting;

        Title = "Clicky suggestions";
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

        _doIt = CardButton("Do it", primary: true);
        _doIt.Click += (_, _) => OnDoIt();
        _next = CardButton("Next", primary: false);
        _next.Click += (_, _) => OnNext();
        _adjust = CardButton("Adjust…", primary: false);
        _adjust.Click += async (_, _) => await OnAdjustAsync();
        var close = new Button
        {
            Content = "✕",
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Not now",
        };
        close.Click += (_, _) => OnClose();

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(_header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_adjust);
        buttons.Children.Add(_next);
        buttons.Children.Add(_doIt);

        var body = new StackPanel();
        body.Children.Add(_title);
        body.Children.Add(_detail);
        body.Children.Add(_why);
        body.Children.Add(_sources);

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(buttons);
        panel.Children.Add(new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Content = body });

        Content = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(12),
            Padding = new Thickness(16, 10, 10, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect { BlurRadius = 14, Opacity = 0.25, ShadowDepth = 2, Direction = 300 },
            Child = panel,
        };

        _lifetime = new DispatcherTimer { Interval = Lifetime };
        _lifetime.Tick += (_, _) => Finish(timedOut: true);
        MouseEnter += (_, _) => _lifetime.Stop();
        MouseLeave += (_, _) =>
        {
            if (!_finished)
            {
                _lifetime.Start();
            }
        };
        Closed += (_, _) => _lifetime.Stop();
        Render();
    }

    public string Greeting { get; }

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
        _lifetime.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // Anchored at the bottom: a longer idea grows the card upwards.
        Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    private Suggestion Current => _ideas[_index];

    private void OnDoIt()
    {
        var idea = Current;
        _reacted = true;
        _host.DoIt(idea);
        Advance();
    }

    private void OnNext()
    {
        var idea = Current;
        _reacted = true;
        _host.Next(idea);
        Advance();
    }

    private async Task OnAdjustAsync()
    {
        var idea = Current;
        _lifetime.Stop();
        var request = Views.TextPrompt.Show(null, "Adjust this idea", $"How should Clicky change “{idea.Title}”?", string.Empty);
        if (_finished || string.IsNullOrWhiteSpace(request))
        {
            _lifetime.Start();
            return;
        }

        _reacted = true;
        SetBusy(true);
        try
        {
            var revised = await _host.AdjustAsync(idea, request);
            if (!_finished && revised is not null && _index < _ideas.Count && _ideas[_index].Id == idea.Id)
            {
                _ideas[_index] = revised;
            }
        }
        finally
        {
            SetBusy(false);
            if (!_finished)
            {
                Render();
                _lifetime.Start();
            }
        }
    }

    private void OnClose()
    {
        _reacted = true;
        _host.Closed(Current, _ideas.Skip(_index + 1).ToList());
        Finish(timedOut: false);
    }

    private void Advance()
    {
        _index++;
        if (_index >= _ideas.Count)
        {
            Finish(timedOut: false);
            return;
        }

        Render();
    }

    private void Finish(bool timedOut)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _lifetime.Stop();
        if (timedOut)
        {
            // Unreacted ideas, from the one showing on; earlier ones already have a decision.
            _host.TimedOut(_ideas.Skip(_index).ToList());
            _logger.LogInformation("Suggestions: the card went away on its own{Reacted}", _reacted ? " after some reactions" : " with no reaction");
        }

        Close();
    }

    private void Render()
    {
        var idea = Current;
        _header.Text = _ideas.Count == 1 ? Greeting : $"{Greeting} · idea {_index + 1} of {_ideas.Count}";
        _title.Text = idea.Title;
        _detail.Text = idea.Detail;
        _detail.Visibility = string.IsNullOrWhiteSpace(idea.Detail) ? Visibility.Collapsed : Visibility.Visible;
        _why.Text = idea.Why;
        _why.Visibility = string.IsNullOrWhiteSpace(idea.Why) ? Visibility.Collapsed : Visibility.Visible;
        _sources.Text = idea.SourceLine;
        _sources.Visibility = string.IsNullOrWhiteSpace(idea.SourceLine) ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = _index == _ideas.Count - 1 ? "Not this" : "Next";
    }

    private void SetBusy(bool busy)
    {
        _doIt.IsEnabled = !busy;
        _next.IsEnabled = !busy;
        _adjust.IsEnabled = !busy;
        _adjust.Content = busy ? "Reworking…" : "Adjust…";
    }

    /// <summary>Bottom right of the primary monitor's work area, just above the tray.</summary>
    private void Place()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var monitors = DisplayMonitors.GetAll();
        var monitor = monitors.FirstOrDefault(m => m.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
        if (monitor is null)
        {
            return;
        }

        var scale = monitor.Scale;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var gap = (int)(EdgeGap * scale);
        var work = monitor.WorkArea;
        var x = Math.Max(work.Left, work.Right - width - gap);
        var y = Math.Max(work.Top, work.Bottom - height - gap);
        FloatingToolWindow.MoveTo(_hwnd, new PixelPoint(x, y));
    }

    private static Button CardButton(string text, bool primary)
    {
        var button = new Button { Content = text, Focusable = false, Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0) };
        if (primary)
        {
            button.Foreground = Brushes.White;
            button.Background = Accent;
            button.BorderBrush = Accent;
        }

        return button;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
