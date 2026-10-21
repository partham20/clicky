using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Geometry;
using Clicky.Core.Walkthroughs;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Walkthroughs;

/// <summary>
/// The walkthrough's step card: "Step 2 of 5", what to do, and Back / Skip / Done / Stop. Sits at the
/// bottom centre of the monitor the walkthrough started on, never takes focus from the app being
/// taught, and stays out of screenshots so Claude never sees it.
/// </summary>
/// <remarks>Create and use on the UI thread.</remarks>
internal sealed class WalkthroughCard : Window
{
    private const double CardWidth = 380;
    private const double BottomGap = 28;

    private static readonly Brush Ink = Frozen(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush Problem = Frozen(Color.FromRgb(0xB9, 0x1C, 0x1C));

    private readonly IWalkthroughs _walkthroughs;
    private readonly ILogger _logger;
    private readonly PixelPoint _anchor;
    private readonly TextBlock _progress = new() { Foreground = Accent, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11.5, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _instruction = new() { Foreground = Ink, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 19, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _problem = new() { Foreground = Problem, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Button _back;
    private readonly Button _skip;
    private readonly Button _done;
    private readonly Button _retry;
    private readonly Button _stop;
    private nint _hwnd;

    public WalkthroughCard(Guid id, IWalkthroughs walkthroughs, ILogger logger)
    {
        Id = id;
        _walkthroughs = walkthroughs;
        _logger = logger;
        _anchor = CursorReader.TryGet(out var pointer, out _) ? pointer : default;

        Title = "Clicky walkthrough";
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

        _back = CardButton("‹ Back", "Show the previous step again", () => _walkthroughs.Back());
        _skip = CardButton("Skip", "Skip this step", () => _walkthroughs.Skip());
        _done = CardButton("Done ✓", "I did this step", () => _walkthroughs.MarkDone(), primary: true);
        _retry = CardButton("Retry", "Ask for the next step again", () => _walkthroughs.Retry(), primary: true);
        _stop = new Button
        {
            Content = "✕",
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            Padding = new Thickness(6, 0, 6, 0),
            ToolTip = "Stop the walkthrough",
        };
        _stop.Click += (_, _) => _walkthroughs.Cancel();

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_stop, Dock.Right);
        DockPanel.SetDock(_progress, Dock.Left);
        header.Children.Add(_stop);
        header.Children.Add(_progress);
        header.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(_back);
        buttons.Children.Add(_skip);
        buttons.Children.Add(_retry);
        buttons.Children.Add(_done);

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_instruction);
        panel.Children.Add(_problem);
        panel.Children.Add(buttons);

        Content = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 10, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect { BlurRadius = 14, Opacity = 0.25, ShadowDepth = 2, Direction = 300 },
            Child = panel,
        };

        SizeChanged += (_, _) => Place();
    }

    public Guid Id { get; }

    public void Update(WalkthroughSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var current = snapshot.Current;
        var last = snapshot.Steps.Count > 0 ? snapshot.Steps[^1] : null;
        _progress.Text = snapshot.Progress;
        _instruction.Text = (current ?? last)?.Instruction ?? string.Empty;
        _problem.Text = snapshot.Problem ?? string.Empty;
        _problem.Visibility = snapshot.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = Status(snapshot);

        var active = snapshot.IsActive;
        var waiting = active && snapshot.Phase == WalkthroughPhase.Waiting;
        _back.Visibility = Show(active && snapshot.CanGoBack);
        _skip.Visibility = Show(waiting && current is not null);
        _done.Visibility = Show(waiting && current is not null);
        _retry.Visibility = Show(waiting && snapshot.Problem is not null);
        _stop.Visibility = Show(active);
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

    private static string Status(WalkthroughSnapshot snapshot) => snapshot.Phase switch
    {
        WalkthroughPhase.Waiting when snapshot.Problem is not null => "Couldn't get the next step",
        WalkthroughPhase.Waiting when snapshot.Current is { HasTarget: true } => "Go ahead, I'm watching",
        WalkthroughPhase.Waiting => "Press Done when you've done it",
        WalkthroughPhase.Settling => "Nice. One moment…",
        WalkthroughPhase.Thinking => "Looking at your screen…",
        _ => snapshot.End switch
        {
            WalkthroughEnd.Completed => "All done ✓",
            WalkthroughEnd.LimitReached => "That's the most steps for one walkthrough",
            WalkthroughEnd.TimedOut => "Walkthrough ended",
            _ => "Stopped",
        },
    };

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private static Button CardButton(string text, string tip, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            ToolTip = tip,
            Focusable = false,
            Padding = new Thickness(12, 3, 12, 3),
            Margin = new Thickness(6, 0, 0, 0),
            FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = primary ? Accent : Ink,
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>Bottom centre of the work area of the monitor the pointer was on when the walkthrough started.</summary>
    private void Place()
    {
        if (_hwnd == 0 || !IsLoaded)
        {
            return;
        }

        var monitors = DisplayMonitors.GetAll();
        var monitor = CoordinateMapper.NearestMonitor(monitors, _anchor);
        if (monitor is null)
        {
            return;
        }

        var scale = monitor.Scale;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var work = monitor.WorkArea;
        var x = work.Left + ((work.Width - width) / 2);
        var y = work.Bottom - height - (int)(BottomGap * scale);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        FloatingToolWindow.MoveTo(_hwnd, new PixelPoint(x, y));
    }
}
