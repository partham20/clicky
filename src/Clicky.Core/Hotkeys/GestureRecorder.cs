namespace Clicky.Core.Hotkeys;

public enum RecorderOutcomeKind
{
    /// <summary>A valid gesture was performed; see <see cref="RecorderOutcome.Gesture"/>.</summary>
    Recorded,

    /// <summary>Backspace on its own: the user wants the binding removed.</summary>
    Cleared,

    /// <summary>Escape on its own: keep the old binding.</summary>
    Cancelled,
}

public sealed record RecorderOutcome(RecorderOutcomeKind Kind, HotkeyGesture? Gesture = null);

/// <summary>
/// Works out which gesture the user just performed in a shortcut box, using the same timing rules
/// as <see cref="GestureEngine"/>: a key with modifiers is a press, modifiers held past
/// <see cref="GestureEngine.TapMaxMs"/> are a hold, and quick taps are a double or triple tap.
/// </summary>
/// <remarks>Single-threaded, caller-supplied clock, like the engine. Call <see cref="Tick"/> at <see cref="NextDeadline"/>.</remarks>
public sealed class GestureRecorder
{
    private readonly Action<RecorderOutcome> _completed;
    private readonly HashSet<int> _modifiersDown = [];
    private readonly HashSet<int> _keysDown = [];

    private bool _sessionActive;
    private long _sessionStart;
    private HotkeyModifiers _sessionMax;
    private bool _sessionDirty;

    private HotkeyModifiers _tapChord;
    private int _tapCount;
    private long _lastTapEnd;

    public GestureRecorder(Action<RecorderOutcome> completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        _completed = completed;
    }

    /// <summary>What to show while recording: the modifiers held so far, or a hint.</summary>
    public string Prompt { get; private set; } = DefaultPrompt;

    public static string DefaultPrompt => "Press a shortcut… (Esc cancels, Backspace clears)";

    public long? NextDeadline => _tapCount > 0 && !_sessionActive ? _lastTapEnd + GestureEngine.MultiTapGapMs + 1 : null;

    public void HandleKey(KeyEvent key)
    {
        if (key.IsInjected || VirtualKeys.IsSyntheticModifier(key))
        {
            return;
        }

        Tick(key.TimestampMs);
        var now = key.TimestampMs;
        var modifier = VirtualKeys.ModifierOf(key.Vk);

        if (modifier != HotkeyModifiers.None)
        {
            if (key.IsDown)
            {
                if (!_modifiersDown.Add(key.Vk))
                {
                    return;
                }

                if (!_sessionActive)
                {
                    _sessionActive = true;
                    _sessionStart = now;
                    _sessionMax = HotkeyModifiers.None;
                    _sessionDirty = _keysDown.Count > 0;
                }

                _sessionMax |= Current();
                Prompt = $"{new HotkeyChord(_sessionMax)}+…";
                return;
            }

            if (_modifiersDown.Remove(key.Vk) && _modifiersDown.Count == 0 && _sessionActive)
            {
                EndSession(now);
            }

            return;
        }

        if (!key.IsDown)
        {
            _keysDown.Remove(key.Vk);
            return;
        }

        if (!_keysDown.Add(key.Vk))
        {
            return;
        }

        if (_sessionActive)
        {
            _sessionDirty = true;
        }

        _tapCount = 0;
        var modifiers = Current();
        if (modifiers == HotkeyModifiers.None && key.Vk == VirtualKeys.Escape)
        {
            Complete(new RecorderOutcome(RecorderOutcomeKind.Cancelled));
            return;
        }

        if (modifiers == HotkeyModifiers.None && key.Vk == VirtualKeys.Backspace)
        {
            Complete(new RecorderOutcome(RecorderOutcomeKind.Cleared));
            return;
        }

        var gesture = HotkeyGesture.Press(modifiers, key.Vk);
        if (gesture.Validate() is { } problem)
        {
            Prompt = problem;
            return;
        }

        Complete(new RecorderOutcome(RecorderOutcomeKind.Recorded, gesture));
    }

    public void Tick(long nowMs)
    {
        if (_tapCount > 0 && !_sessionActive && nowMs - _lastTapEnd > GestureEngine.MultiTapGapMs)
        {
            FinishTaps();
        }
    }

    private HotkeyModifiers Current()
    {
        var modifiers = HotkeyModifiers.None;
        foreach (var vk in _modifiersDown)
        {
            modifiers |= VirtualKeys.ModifierOf(vk);
        }

        return modifiers;
    }

    private void EndSession(long now)
    {
        _sessionActive = false;
        if (_sessionDirty)
        {
            return;
        }

        if (now - _sessionStart > GestureEngine.TapMaxMs)
        {
            _tapCount = 0;
            Offer(HotkeyGesture.Hold(_sessionMax));
            return;
        }

        if (_tapCount > 0 && _sessionMax == _tapChord && _sessionStart - _lastTapEnd <= GestureEngine.MultiTapGapMs)
        {
            _tapCount++;
        }
        else
        {
            _tapChord = _sessionMax;
            _tapCount = 1;
        }

        _lastTapEnd = now;
        if (_tapCount >= 3)
        {
            FinishTaps();
            return;
        }

        Prompt = _tapCount == 1 ? $"Tap {_tapChord} again for a double-tap, or hold it" : $"Double-tap {_tapChord}…";
    }

    private void FinishTaps()
    {
        var count = _tapCount;
        _tapCount = 0;
        switch (count)
        {
            case 2:
                Offer(HotkeyGesture.DoubleTap(_tapChord));
                break;
            case >= 3:
                Offer(HotkeyGesture.TripleTap(_tapChord));
                break;
            default:
                Prompt = DefaultPrompt;
                break;
        }
    }

    private void Offer(HotkeyGesture gesture)
    {
        if (gesture.Validate() is { } problem)
        {
            Prompt = problem;
            return;
        }

        Complete(new RecorderOutcome(RecorderOutcomeKind.Recorded, gesture));
    }

    private void Complete(RecorderOutcome outcome)
    {
        _tapCount = 0;
        Prompt = DefaultPrompt;
        _completed(outcome);
    }
}
