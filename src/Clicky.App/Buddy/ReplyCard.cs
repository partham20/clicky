using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Geometry;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Buddy;

/// <summary>
/// A reply Clicky did not say out loud (muted, in a call, Quiet mode, …), shown as a card by the
/// pointer with a Copy button. It never takes focus from the app you are in and stays out of
/// screenshots and screen shares. It goes away by itself after a while, but not while the mouse is
/// over it.
/// </summary>
/// <remarks>Create and use on the UI thread.</remarks>
internal sealed class ReplyCard : Window
{
    private const double CardWidth = 380;
    private const double PointerGap = 20;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

    private readonly string _text;
    private readonly ILogger _logger;
    private readonly DispatcherTimer _lifetime;
    private readonly Button _copy;
    private readonly PixelPoint _anchor;
    private nint _hwnd;

    public ReplyCard(string text, string? reason, ILogger logger)
    {
        _text = text;
        _logger = logger;
        _anchor = CursorReader.TryGet(out var pointer, out _) ? pointer : default;

        Title = "Clicky reply";
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

        // Off screen until it knows its size and can be placed by the pointer.
        Left = -32000;
        Top = -32000;

        _copy = new Button { Content = "Copy", Padding = new Thickness(14, 3, 14, 3), Focusable = false, HorizontalAlignment = HorizontalAlignment.Right };
        _copy.Click += (_, _) => Copy();
        var close = new Button
        {
            Content = "✕",
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Dismiss",
        };
        close.Click += (_, _) => Close();

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(reason) ? "Clicky" : $"Not spoken: {reason}",
            Foreground = Muted,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var body = new ScrollViewer
        {
            MaxHeight = 280,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = false,
            Content = new TextBlock { Text = text, Foreground = Ink, FontSize = 13.5, TextWrapping = TextWrapping.Wrap, LineHeight = 19 },
        };

        var panel = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_copy, Dock.Bottom);
        _copy.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(header);
        panel.Children.Add(_copy);
        panel.Children.Add(body);

        Content = new Border
        {
            Width = CardWidth,
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 10, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect { BlurRadius = 14, Opacity = 0.25, ShadowDepth = 2, Direction = 300 },
            Child = panel,
        };

        // Long enough to read: 8 s plus 50 ms a character, at most 45 s.
        _lifetime = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(8 + (text.Length * 0.05), 8, 45)) };
        _lifetime.Tick += (_, _) => Close();
        MouseEnter += (_, _) => _lifetime.Stop();
        MouseLeave += (_, _) => _lifetime.Start();
        Closed += (_, _) => _lifetime.Stop();
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
        _lifetime.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    /// <summary>Below and right of the pointer, flipped and clamped to stay inside the monitor's work area.</summary>
    private void Place()
    {
        if (_hwnd == 0)
        {
            return;
        }

        var monitors = DisplayMonitors.GetAll();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(_anchor)) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
        if (monitor is null)
        {
            return;
        }

        var scale = monitor.Scale;
        var width = (int)Math.Ceiling(ActualWidth * scale);
        var height = (int)Math.Ceiling(ActualHeight * scale);
        var gap = (int)(PointerGap * scale);
        var work = monitor.WorkArea;

        var x = _anchor.X + gap + width <= work.Right ? _anchor.X + gap : _anchor.X - gap - width;
        var y = _anchor.Y + gap + height <= work.Bottom ? _anchor.Y + gap : _anchor.Y - gap - height;
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        FloatingToolWindow.MoveTo(_hwnd, new PixelPoint(x, y));
    }

    private void Copy()
    {
        // The clipboard can be held open by another app for a moment (clipboard managers, RDP).
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(_text, copy: true);
                _copy.Content = "Copied ✓";
                _copy.Foreground = Accent;
                _logger.LogInformation("Reply card: copied {Chars} chars to the clipboard", _text.Length);
                return;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                Thread.Sleep(30);
            }
        }

        _copy.Content = "Clipboard busy, try again";
        _logger.LogWarning("Reply card: the clipboard stayed busy");
    }
}
