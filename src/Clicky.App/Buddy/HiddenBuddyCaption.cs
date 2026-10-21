using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Buddy;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.Buddy;

/// <summary>
/// With "Hide floating cursor" on there is no buddy by the pointer to carry captions, so what Clicky
/// heard, what it is saying and why it couldn't were invisible unless Home was open. This shows
/// them as a subtitle at the bottom of the monitor the user is on, with "Listening…" and
/// "Thinking…" in between. It never takes focus or clicks and stays out of screenshots.
/// </summary>
/// <remarks>Start and dispose on the UI thread; presence and settings events may come from any thread.</remarks>
internal sealed class HiddenBuddyCaption(IBuddyPresence presence, ISettingsStore settings, Dispatcher dispatcher, ILogger<HiddenBuddyCaption> logger) : IDisposable
{
    private CaptionWindow? _window;
    private int _refreshPending;
    private bool _started;
    private bool _disposed;

    public void Start()
    {
        dispatcher.VerifyAccess();
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        presence.StateChanged += OnChanged;
        presence.CaptionChanged += OnChanged;
        settings.Changed += OnChanged;
        Refresh();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        presence.StateChanged -= OnChanged;
        presence.CaptionChanged -= OnChanged;
        settings.Changed -= OnChanged;
        if (!dispatcher.HasShutdownStarted)
        {
            _window?.Close();
        }
    }

    /// <summary>What to show, or null to hide.</summary>
    internal static string? TextFor(bool buddyHidden, BuddyState state, string? caption)
    {
        if (!buddyHidden)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(caption))
        {
            return caption;
        }

        return state switch
        {
            BuddyState.Listening => "Listening…",
            BuddyState.Thinking => "Thinking…",
            _ => null,
        };
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        // Captions change per sentence; one refresh queued at a time is plenty.
        if (Interlocked.Exchange(ref _refreshPending, 1) == 0)
        {
            dispatcher.InvokeAsync(Refresh);
        }
    }

    private void Refresh()
    {
        Volatile.Write(ref _refreshPending, 0);
        if (_disposed)
        {
            return;
        }

        var text = TextFor(settings.Current.Overlay.HideFloatingCursor, presence.State, presence.Caption);
        if (text is null)
        {
            _window?.Hide();
            return;
        }

        _window ??= new CaptionWindow(logger);
        _window.ShowText(text);
    }

    /// <summary>A rounded dark pill with one or two lines of text.</summary>
    private sealed class CaptionWindow : Window
    {
        private const double MaxTextWidth = 640;
        private const double BottomGap = 36;

        private readonly ILogger _logger;
        private readonly TextBlock _text;
        private nint _hwnd;
        private MonitorInfo? _monitor;

        public CaptionWindow(ILogger logger)
        {
            _logger = logger;
            Title = "Clicky caption";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -32000;
            Top = -32000;

            _text = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 15,
                MaxWidth = MaxTextWidth,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 64,
                TextAlignment = TextAlignment.Center,
            };
            Content = new Border
            {
                Margin = new Thickness(10),
                Padding = new Thickness(16, 8, 16, 9),
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1F, 0x29, 0x37)),
                CornerRadius = new CornerRadius(14),
                Effect = new DropShadowEffect { BlurRadius = 12, Opacity = 0.3, ShadowDepth = 2, Direction = 270 },
                Child = _text,
            };

            SizeChanged += (_, _) => Place();
        }

        public void ShowText(string text)
        {
            _text.Text = text;
            if (!IsVisible)
            {
                // Follow the user to whichever monitor they are on now.
                _monitor = MonitorUnderPointer();
                Show();
            }

            Place();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            FloatingToolWindow.Apply(_hwnd, _logger, clickThrough: true);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
        }

        private static MonitorInfo? MonitorUnderPointer()
        {
            var monitors = DisplayMonitors.GetAll();
            var pointer = CursorReader.TryGet(out var position, out _) ? position : default;
            return monitors.FirstOrDefault(m => m.Bounds.Contains(pointer)) ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? (monitors.Count > 0 ? monitors[0] : null);
        }

        /// <summary>Centred near the bottom of the work area, like a subtitle.</summary>
        private void Place()
        {
            if (_hwnd == 0 || _monitor is not { } monitor || ActualWidth <= 0)
            {
                return;
            }

            var scale = monitor.Scale;
            var width = (int)Math.Ceiling(ActualWidth * scale);
            var height = (int)Math.Ceiling(ActualHeight * scale);
            var work = monitor.WorkArea;
            var x = work.Left + ((work.Width - width) / 2);
            var y = work.Bottom - height - (int)(BottomGap * scale);
            FloatingToolWindow.MoveTo(_hwnd, new PixelPoint(Math.Max(work.Left, x), Math.Max(work.Top, y)));
        }
    }
}
