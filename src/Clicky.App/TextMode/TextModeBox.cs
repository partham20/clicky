using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Clicky.Core.Geometry;
using Clicky.Platform.Windows.Display;
using Clicky.Platform.Windows.Overlay;
using Microsoft.Extensions.Logging;

namespace Clicky.App.TextMode;

/// <summary>
/// Text mode's box by the pointer: type a question (Enter asks, Shift+Enter is a new line), drop or
/// paste files and pictures onto it, tick the open app's document, and read the answer as it
/// streams in. It takes focus (it is typed into) but stays out of the taskbar, Alt+Tab and
/// screenshots, so the screenshot sent with the question shows the screen, not the box.
/// </summary>
/// <remarks>UI thread only. The controller owns what happens; the box only shows it and raises events.</remarks>
internal sealed class TextModeBox : Window
{
    private const double BoxWidth = 460;
    private const double PointerGap = 18;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0x1F, 0x29, 0x37));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush Problem = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush ChipBackground = new SolidColorBrush(Color.FromRgb(0xEF, 0xF4, 0xFF));
    private static readonly Brush ChipOffBackground = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));

    private readonly ILogger _logger;
    private readonly PixelPoint _anchor;
    private readonly TextBox _input;
    private readonly WrapPanel _chips;
    private readonly TextBlock _hint;
    private readonly Button _ask;
    private readonly Border _answerArea;
    private readonly TextBlock _question;
    private readonly TextBox _answer;
    private readonly TextBlock _answerNote;
    private readonly Button _copy;
    private readonly Border _frame;
    private readonly Dictionary<PendingAttachment, FrameworkElement> _chipViews = [];
    private nint _hwnd;
    private bool _busy;

    public TextModeBox(PixelPoint anchor, ILogger logger)
    {
        _anchor = anchor;
        _logger = logger;

        Title = "Ask Clicky";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = BoxWidth + 24;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowDrop = true;

        // Off screen until it knows its size and can be placed by the pointer.
        Left = -32000;
        Top = -32000;

        _chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };

        _input = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 46,
            MaxHeight = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
            Padding = new Thickness(6, 5, 6, 5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x25, 0x63, 0xEB)),
            AllowDrop = true,
        };
        _input.PreviewKeyDown += OnInputKeyDown;
        _input.TextChanged += (_, _) => UpdateAskButton();
        DataObject.AddPastingHandler(_input, OnPasting);

        // TextBox handles text drags itself and refuses files; take the files first.
        _input.PreviewDragOver += OnDragOver;
        _input.PreviewDrop += OnDrop;

        var placeholder = new TextBlock
        {
            Text = "Ask Clicky anything, or drop a file here…",
            Foreground = Muted,
            FontSize = 14,
            Margin = new Thickness(9, 6, 0, 0),
            IsHitTestVisible = false,
        };
        _input.TextChanged += (_, _) => placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var inputGrid = new Grid();
        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(placeholder);

        _ask = new Button { Content = "Ask", MinWidth = 64, Padding = new Thickness(12, 3, 12, 3), IsEnabled = false };
        _ask.Click += (_, _) => Submit();
        _hint = new TextBlock
        {
            Text = "Enter to ask · Shift+Enter for a new line · Esc to close",
            Foreground = Muted,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = true };
        DockPanel.SetDock(_ask, Dock.Right);
        footer.Children.Add(_ask);
        footer.Children.Add(_hint);

        _question = new TextBlock { Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        _answer = new TextBox
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Foreground = Ink,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0),
        };
        _answerNote = new TextBlock { Foreground = Muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        _copy = new Button { Content = "Copy", Padding = new Thickness(12, 2, 12, 2), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        _copy.Click += (_, _) => Copy();
        var answerPanel = new StackPanel();
        answerPanel.Children.Add(_question);
        answerPanel.Children.Add(_answer);
        answerPanel.Children.Add(_answerNote);
        answerPanel.Children.Add(_copy);
        _answerArea = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00)),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 0),
            Margin = new Thickness(0, 10, 0, 0),
            Child = answerPanel,
            Visibility = Visibility.Collapsed,
        };

        var close = new Button
        {
            Content = "✕",
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            Padding = new Thickness(6, 0, 6, 0),
            ToolTip = "Close (Esc)",
        };
        close.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Ask Clicky", Foreground = Accent, FontWeight = FontWeights.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });

        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(_chips);
        panel.Children.Add(inputGrid);
        panel.Children.Add(footer);
        panel.Children.Add(_answerArea);

        _frame = new Border
        {
            Width = BoxWidth,
            Margin = new Thickness(12),
            Padding = new Thickness(14, 10, 12, 12),
            Background = new SolidColorBrush(Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x25, 0x63, 0xEB)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Effect = new DropShadowEffect { BlurRadius = 16, Opacity = 0.28, ShadowDepth = 2, Direction = 300 },
            Child = panel,
        };
        Content = _frame;

        PreviewDragOver += OnDragOver;
        DragLeave += (_, _) => _frame.Background = new SolidColorBrush(Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF));
        Drop += OnDrop;
        PreviewKeyDown += OnKeyDown;
    }

    /// <summary>The user asked (Enter or the Ask button); the text is the question.</summary>
    public event EventHandler<string>? AskRequested;

    /// <summary>Esc while an answer streams in.</summary>
    public event EventHandler? StopRequested;

    /// <summary>Esc or ✕ while idle.</summary>
    public event EventHandler? CloseRequested;

    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// <summary>A picture pasted from the clipboard (a snip), as PNG.</summary>
    public event EventHandler<byte[]>? ImagePasted;

    public nint Handle => _hwnd;

    /// <summary>Nothing typed, attached or answered: safe to close when the user clicks away.</summary>
    public bool IsEmpty => _input.Text.Length == 0 && _chipViews.Count == 0 && _answerArea.Visibility != Visibility.Visible && !_busy;

    public void FocusInput()
    {
        _input.Focus();
        Keyboard.Focus(_input);
    }

    public void AddChip(PendingAttachment attachment, Action<PendingAttachment> remove)
    {
        var name = new TextBlock { Text = attachment.Name, FontSize = 12, Foreground = Ink, MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var summary = new TextBlock { FontSize = 11, Foreground = Muted, Margin = new Thickness(6, 0, 0, 0), MaxWidth = 190, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        CheckBox? include = null;
        if (attachment.IsWindowDocument)
        {
            include = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0), Focusable = false, ToolTip = "Send this window's document with the question" };
            include.Click += (_, _) => attachment.Choose(include.IsChecked == true);
            row.Children.Add(include);
        }
        else
        {
            row.Children.Add(new TextBlock { Text = "📄", FontSize = 12, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        row.Children.Add(name);
        row.Children.Add(summary);
        var removeButton = new Button
        {
            Content = "✕",
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Muted,
            FontSize = 10,
            Padding = new Thickness(5, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Remove",
        };
        removeButton.Click += (_, _) => remove(attachment);
        row.Children.Add(removeButton);

        var chip = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 4, 3), Margin = new Thickness(0, 0, 6, 4), Child = row };
        void Refresh()
        {
            summary.Text = attachment.Summary;
            summary.Foreground = attachment.State == PendingState.Failed ? Problem : Muted;
            chip.ToolTip = attachment.State == PendingState.Failed ? attachment.Problem : attachment.Document?.Warning ?? attachment.Name;
            chip.Background = attachment.Included || attachment.State == PendingState.Reading ? ChipBackground : ChipOffBackground;
            chip.Opacity = attachment.State == PendingState.Sent ? 0.6 : 1;
            if (include is not null)
            {
                include.IsChecked = attachment.Included;
                include.IsEnabled = attachment.State == PendingState.Ready;
            }
        }

        attachment.Changed += (_, _) => Dispatcher.InvokeAsync(Refresh);
        Refresh();
        _chipViews[attachment] = chip;

        // The window's document comes first: it is the one most often wanted.
        if (attachment.IsWindowDocument)
        {
            _chips.Children.Insert(0, chip);
        }
        else
        {
            _chips.Children.Add(chip);
        }

        _chips.Visibility = Visibility.Visible;
        UpdateAskButton();
    }

    public void RemoveChip(PendingAttachment attachment)
    {
        if (_chipViews.Remove(attachment, out var chip))
        {
            _chips.Children.Remove(chip);
        }

        _chips.Visibility = _chipViews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAskButton();
    }

    /// <summary>A question went out: clears the input and shows the question above an empty answer.</summary>
    public void BeginAnswer(string question, string? note)
    {
        _busy = true;
        _input.Clear();
        _question.Text = question.ReplaceLineEndings(" ").Trim();
        _answer.Text = string.Empty;
        _answer.Foreground = Ink;
        _answerNote.Text = note ?? string.Empty;
        _answerNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        _copy.Visibility = Visibility.Collapsed;
        _answerArea.Visibility = Visibility.Visible;
        _hint.Text = "Thinking… · Esc to stop";
        UpdateAskButton();
    }

    public void AppendAnswer(string text)
    {
        // Mid-word chunks: Claude streams tokens, not sentences.
        _answer.AppendText(text);
        _answer.ScrollToEnd();
        if (_hint.Text.StartsWith("Thinking", StringComparison.Ordinal))
        {
            _hint.Text = "Answering… · Esc to stop";
        }
    }

    /// <summary>The answer is done (or stopped, or failed: <paramref name="problem"/>).</summary>
    public void EndAnswer(string? problem, string? note)
    {
        _busy = false;
        _answer.Text = _answer.Text.Trim();
        if (problem is not null)
        {
            _answer.Text = _answer.Text.Length == 0 ? problem : $"{_answer.Text}\n\n{problem}";
            _answer.Foreground = _answer.Text == problem ? Problem : Ink;
        }

        if (!string.IsNullOrEmpty(note))
        {
            _answerNote.Text = string.IsNullOrEmpty(_answerNote.Text) ? note : $"{_answerNote.Text} {note}";
            _answerNote.Visibility = Visibility.Visible;
        }

        _copy.Content = "Copy";
        _copy.Foreground = SystemColors.ControlTextBrush;
        _copy.Visibility = _answer.Text.Length > 0 && problem is null ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = "Ask a follow-up · Esc to close";
        UpdateAskButton();
        FocusInput();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        FloatingToolWindow.Apply(_hwnd, _logger, activatable: true);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
        FocusInput();
        SizeChanged += (_, _) => Dispatcher.InvokeAsync(KeepOnScreen, DispatcherPriority.Loaded);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.InvokeAsync(Place, DispatcherPriority.Loaded);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;
        if (_busy)
        {
            StopRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            Submit();
        }
    }

    private void Submit()
    {
        var text = _input.Text.Trim();
        if (_busy || (text.Length == 0 && !_chipViews.Keys.Any(a => a.Included)))
        {
            return;
        }

        AskRequested?.Invoke(this, text);
    }

    private void UpdateAskButton() =>
        _ask.IsEnabled = !_busy && (_input.Text.Trim().Length > 0 || _chipViews.Keys.Any(a => a.Included));

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _frame.Background = new SolidColorBrush(Color.FromArgb(0xFA, 0xF4, 0xF8, 0xFF));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        _frame.Background = new SolidColorBrush(Color.FromArgb(0xFA, 0xFF, 0xFF, 0xFF));
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            e.Handled = true;
            FilesDropped?.Invoke(this, files);
        }
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        // Files copied in Explorer, or a snip: attach them instead of pasting their names.
        if (e.DataObject.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            e.CancelCommand();
            FilesDropped?.Invoke(this, files);
            return;
        }

        if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) && e.DataObject.GetDataPresent(DataFormats.Bitmap)
            && e.DataObject.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            e.CancelCommand();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            ImagePasted?.Invoke(this, png.ToArray());
        }
    }

    private void Copy()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(_answer.Text, copy: true);
                _copy.Content = "Copied ✓";
                _copy.Foreground = Accent;
                return;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                Thread.Sleep(30);
            }
        }

        _copy.Content = "Clipboard busy, try again";
    }

    /// <summary>Below and right of the pointer, flipped and clamped to stay inside the monitor's work area.</summary>
    private void Place() => Position(initial: true);

    /// <summary>The answer made the box taller: move it up if it now runs off the bottom.</summary>
    private void KeepOnScreen() => Position(initial: false);

    private void Position(bool initial)
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

        int x;
        int y;
        if (initial)
        {
            x = _anchor.X + gap + width <= work.Right ? _anchor.X + gap : _anchor.X - gap - width;
            y = _anchor.Y + gap + height <= work.Bottom ? _anchor.Y + gap : _anchor.Y - gap - height;
        }
        else
        {
            var current = PointToScreen(new Point(0, 0));
            x = (int)current.X;
            y = (int)current.Y;
        }

        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));
        FloatingToolWindow.MoveTo(_hwnd, new PixelPoint(x, y));
    }
}
