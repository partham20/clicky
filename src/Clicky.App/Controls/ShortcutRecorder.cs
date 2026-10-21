using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core.Hotkeys;

namespace Clicky.App.Controls;

/// <summary>
/// A shortcut box. Click (or tab) into it and perform the gesture: "Hold Ctrl+Alt", "Ctrl+Alt+A",
/// "Double-tap Ctrl". Esc keeps the old value, Backspace clears it.
/// </summary>
/// <remarks>
/// Keys come from the global hook in recording mode, not from WPF: that is the only way to see Win
/// without the Start menu opening, and it pauses every Clicky hotkey while recording, so pressing the
/// current Talk chord records it instead of triggering it. Timing (hold vs tap) uses the same
/// <see cref="GestureRecorder"/> rules as the live engine.
/// </remarks>
internal sealed class ShortcutRecorder : Border
{
    private static readonly Brush IdleBackground = SystemColors.WindowBrush;
    private static readonly Brush RecordingBackground = new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE));
    private static readonly Brush RecordingBorder = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));

    private readonly IHotkeyService _hotkeys;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer;

    private GestureRecorder? _recorder;
    private IDisposable? _recording;
    private HotkeyGesture? _gesture;

    public ShortcutRecorder(IHotkeyService hotkeys)
    {
        _hotkeys = hotkeys;
        _text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Child = _text;

        Focusable = true;
        Cursor = Cursors.Hand;
        MinWidth = 220;
        MinHeight = 28;
        Padding = new Thickness(8, 3, 8, 3);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(4);
        BorderBrush = SystemColors.ControlDarkBrush;
        Background = IdleBackground;
        AutomationProperties.SetHelpText(this, "Focus and press a shortcut. Escape cancels, Backspace clears.");

        _timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher);
        _timer.Tick += OnTimer;

        MouseLeftButtonDown += (_, e) =>
        {
            Focus();
            e.Handled = true;
        };
        GotKeyboardFocus += (_, _) => StartRecording();
        LostKeyboardFocus += (_, _) => StopRecording();
        Unloaded += (_, _) => StopRecording();
        Refresh();
    }

    /// <summary>Raised when the user records a gesture, or clears it (null). Not raised on cancel.</summary>
    public event EventHandler<HotkeyGesture?>? GestureCommitted;

    public HotkeyGesture? Gesture
    {
        get => _gesture;
        set
        {
            _gesture = value;
            Refresh();
        }
    }

    public bool IsRecording => _recorder is not null;

    private void StartRecording()
    {
        if (_recorder is not null)
        {
            return;
        }

        _recorder = new GestureRecorder(OnOutcome);
        var dispatcher = Dispatcher;

        // Called on the hook thread for every key while a Clicky window has focus.
        _recording = _hotkeys.BeginRecording(key => dispatcher.BeginInvoke(DispatcherPriority.Input, () => Feed(key)));
        Background = RecordingBackground;
        BorderBrush = RecordingBorder;
        Refresh();
    }

    private void StopRecording()
    {
        _recording?.Dispose();
        _recording = null;
        _recorder = null;
        _timer.Stop();
        Background = IdleBackground;
        BorderBrush = SystemColors.ControlDarkBrush;
        Refresh();
    }

    private void Feed(KeyEvent key)
    {
        if (_recorder is null)
        {
            return;
        }

        _recorder.HandleKey(key);
        Reschedule();
        Refresh();
    }

    private void OnTimer(object? sender, EventArgs e)
    {
        _timer.Stop();
        _recorder?.Tick(Environment.TickCount64);
        Reschedule();
        Refresh();
    }

    private void Reschedule()
    {
        _timer.Stop();
        if (_recorder?.NextDeadline is { } deadline)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, deadline - Environment.TickCount64));
            _timer.Start();
        }
    }

    private void OnOutcome(RecorderOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case RecorderOutcomeKind.Recorded:
                _gesture = outcome.Gesture;
                GestureCommitted?.Invoke(this, _gesture);
                break;
            case RecorderOutcomeKind.Cleared:
                _gesture = null;
                GestureCommitted?.Invoke(this, null);
                break;
        }

        // Done: leave the box so the next keystrokes go back to normal (and to the hotkeys).
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsKeyboardFocused)
            {
                Keyboard.ClearFocus();
            }
        });
    }

    private void Refresh()
    {
        _text.Text = _recorder?.Prompt ?? _gesture?.ToDisplayString() ?? "Not set";
        _text.Opacity = _recorder is null && _gesture is null ? 0.6 : 1.0;
        AutomationProperties.SetName(this, _text.Text);
    }
}
