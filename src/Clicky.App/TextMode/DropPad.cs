using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Input;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.TextMode;

/// <summary>
/// Drag files onto the buddy: the buddy's overlays are click-through, so they can't take a drop.
/// Instead, while a file drag is in progress anywhere (see <see cref="DragWatcher"/>), a small
/// "Drop here to ask Clicky" bubble appears just past the pointer, where the buddy is; dropping on
/// it opens text mode with the files attached. It goes away when the drag ends.
/// </summary>
/// <remarks>
/// A 250 ms poll of two cheap calls while idle, well inside the idle CPU budget. Not shown while
/// the text box is open (the box takes drops itself) or with settings.text.offerDropTarget off.
/// UI thread only.
/// </remarks>
internal sealed class DropPad(TextModeController textMode, ISettingsStore settings, Dispatcher dispatcher, ILogger<DropPad> logger) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Polls in a row a drag must be seen (or gone) before the bubble shows (or hides), so a quick click-drag doesn't flash it.</summary>
    private const int Debounce = 2;

    /// <summary>From the pointer to the bubble's top-left, in DIPs: about where the buddy sits.</summary>
    private const double PointerOffset = 36;

    private DispatcherTimer? _poll;
    private Window? _pad;
    private int _seen;
    private int _gone;

    public void Start()
    {
        dispatcher.VerifyAccess();
        _poll = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = PollInterval };
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    public void Dispose()
    {
        if (_poll is not null)
        {
            _poll.Stop();
            _poll.Tick -= OnPoll;
        }

        Hide();
    }

    private void OnPoll(object? sender, EventArgs e)
    {
        var dragging = settings.Current.Text.OfferDropTarget && !textMode.IsOpen && DragWatcher.IsShellDragInProgress();
        if (dragging)
        {
            _gone = 0;
            if (++_seen == Debounce && _pad is null)
            {
                Show();
            }
        }
        else
        {
            _seen = 0;
            if (_pad is not null && ++_gone >= Debounce)
            {
                Hide();
            }
        }
    }

    private void Show()
    {
        if (!CursorReader.TryGet(out var pointer, out _))
        {
            return;
        }

        var pad = new Window
        {
            Title = "Drop here to ask Clicky",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            AllowDrop = true,
        };
        var bubble = new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 12, Opacity = 0.25, ShadowDepth = 2, Direction = 300 },
            Child = new TextBlock { Text = "Drop here to ask Clicky", FontSize = 13, Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37)) },
        };
        pad.Content = bubble;
        pad.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            bubble.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xEF, 0xF4, 0xFF));
            e.Handled = true;
        };
        pad.DragLeave += (_, _) => bubble.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        pad.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                logger.LogInformation("Text mode: {Count} file(s) dropped on the buddy", files.Length);
                Hide();
                textMode.OpenWithFiles(files);
            }
        };
        pad.SourceInitialized += (_, _) => FloatingToolWindow.Apply(new WindowInteropHelper(pad).Handle, logger);
        pad.ContentRendered += (_, _) => Place(pad, pointer);
        _pad = pad;
        pad.Show();
    }

    private void Hide()
    {
        _pad?.Close();
        _pad = null;
        _gone = 0;
    }

    private static void Place(Window pad, PixelPoint pointer)
    {
        var hwnd = new WindowInteropHelper(pad).Handle;
        var monitors = DisplayMonitors.GetAll();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(pointer)) ?? monitors.FirstOrDefault(m => m.IsPrimary);
        if (hwnd == 0 || monitor is null)
        {
            return;
        }

        var offset = (int)(PointerOffset * monitor.Scale);
        var width = (int)Math.Ceiling(pad.ActualWidth * monitor.Scale);
        var height = (int)Math.Ceiling(pad.ActualHeight * monitor.Scale);
        var work = monitor.WorkArea;
        var x = pointer.X + offset + width <= work.Right ? pointer.X + offset : pointer.X - offset - width;
        var y = pointer.Y + offset + height <= work.Bottom ? pointer.Y + offset : pointer.Y - offset - height;
        FloatingToolWindow.MoveTo(hwnd, new PixelPoint(Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width)), Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height))));
    }
}
