namespace Clicky.Core.Hotkeys;

/// <summary>
/// Turns raw key transitions into hotkey gestures: modifier-only holds (Ctrl+Alt), presses
/// (Ctrl+Alt+A) and double/triple taps (double Ctrl). Pure logic on a caller-supplied clock, so
/// every timing rule is unit-testable.
/// </summary>
/// <remarks>
/// <para>Single-threaded: call everything from one thread (the hook thread). Feed every key event to
/// <see cref="HandleKey"/>, and call <see cref="Tick"/> at <see cref="NextDeadline"/>; timers are
/// the caller's business. Events are emitted synchronously from those calls.</para>
/// <para>Rules, in short:</para>
/// <list type="bullet">
/// <item>Injected input and the layout driver's fake modifiers (AltGr's LCtrl) are invisible.</item>
/// <item>A modifier "session" runs from the first modifier down to the last modifier up. Any
/// non-modifier key during it makes it dirty: dirty sessions never arm a hold and are never taps,
/// which is what keeps Ctrl+C, Ctrl+Alt+Del and AltGr typing from triggering anything.</item>
/// <item>A modifier-only hold arms after <see cref="HoldArmMs"/> with exactly its modifiers down,
/// reached by pressing (not by releasing extras), and ends when one of them is released.</item>
/// <item>A tap is a clean session no longer than <see cref="TapMaxMs"/> that never armed a hold.
/// Taps of the same modifiers count up while each starts within <see cref="MultiTapGapMs"/> of the
/// previous one's release.</item>
/// </list>
/// </remarks>
public sealed class GestureEngine
{
    /// <summary>How long a modifier-only chord must be held before its hold gesture starts.</summary>
    public const int HoldArmMs = 200;

    /// <summary>Longest press-to-release that still counts as a tap.</summary>
    public const int TapMaxMs = 250;

    /// <summary>Longest gap from one tap's release to the next tap's press.</summary>
    public const int MultiTapGapMs = 300;

    /// <summary>
    /// A held key auto-repeats at least every ~0.5 s (1 s initial delay at the slowest setting), so a
    /// "down" this long after a key's last event is a new press whose key-up we missed (UIPI, secure
    /// desktop), not a repeat.
    /// </summary>
    public const int StaleKeyMs = 1500;

    private readonly Action<HotkeyEvent> _emit;

    // Physical keys we believe are down, with the time of their last event.
    private readonly Dictionary<int, long> _modifiersDown = [];
    private readonly Dictionary<int, long> _keysDown = [];

    // Trigger keys whose down we swallowed: their repeats and key-up are swallowed too.
    private readonly HashSet<int> _swallowed = [];

    private HotkeyBinding[] _bindings = [];

    private bool _sessionActive;
    private long _sessionStart;
    private HotkeyModifiers _sessionMax;
    private bool _sessionDirty;
    private bool _sessionShrunk;
    private bool _sessionArmed;

    private HotkeyBinding? _candidate;
    private long _armAt;

    private HotkeyBinding? _hold;
    private int _holdTriggerVk;

    private HotkeyModifiers _tapChord;
    private int _tapCount;
    private long _lastTapEnd;

    public GestureEngine(Action<HotkeyEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(emit);
        _emit = emit;
    }

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public bool IsHoldActive => _hold is not null;

    /// <summary>True while any key is tracked as down; a missed key-up shows up here until reconciled.</summary>
    public bool HasKeysDown => _modifiersDown.Count > 0 || _keysDown.Count > 0;

    /// <summary>
    /// Set by the last <see cref="HandleKey"/> when it swallowed a Win key-up that ends a Clicky
    /// gesture. The caller must inject <see cref="VirtualKeys.MaskKey"/> down and up and then replay
    /// the swallowed key-up, so the shell sees another key between Win down and up and keeps the
    /// Start menu closed.
    /// </summary>
    public bool MaskKeyRequested { get; private set; }

    /// <summary>When <see cref="Tick"/> must next be called (same clock as the key timestamps), or null if never.</summary>
    public long? NextDeadline
    {
        get
        {
            long? deadline = _candidate is null ? null : _armAt;
            if (_tapCount > 0)
            {
                // While a session is running it may still become the next tap until it gets too long.
                var tapDeadline = _sessionActive ? _sessionStart + TapMaxMs + 1 : _lastTapEnd + MultiTapGapMs + 1;
                deadline = deadline is null ? tapDeadline : Math.Min(deadline.Value, tapDeadline);
            }

            return deadline;
        }
    }

    /// <summary>
    /// Replaces the bindings. Invalid gestures (see <see cref="HotkeyGesture.Validate"/>) are dropped.
    /// An active hold whose binding is gone is cancelled; pending taps and holds-in-waiting are discarded.
    /// </summary>
    public void UpdateBindings(IEnumerable<HotkeyBinding> bindings, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var next = bindings.Where(b => b is not null && b.Gesture.Validate() is null).Distinct().ToArray();
        if (_hold is not null && !next.Contains(_hold))
        {
            EndHold(HotkeyPhase.Cancelled, nowMs);
        }

        _bindings = next;
        _candidate = null;
        ResetTaps();
    }

    /// <summary>Processes one key event. Returns true if it must be swallowed (kept from every application).</summary>
    public bool HandleKey(KeyEvent key)
    {
        MaskKeyRequested = false;
        if (key.IsInjected || VirtualKeys.IsSyntheticModifier(key))
        {
            return false;
        }

        Advance(key.TimestampMs);

        if (VirtualKeys.IsModifier(key.Vk))
        {
            return key.IsDown ? OnModifierDown(key.Vk, key.TimestampMs) : OnModifierUp(key.Vk, key.TimestampMs);
        }

        return key.IsDown ? OnKeyDown(key.Vk, key.TimestampMs) : OnKeyUp(key.Vk, key.TimestampMs);
    }

    /// <summary>Runs whatever timers are due: arms holds and settles tap sequences.</summary>
    public void Tick(long nowMs) => Advance(nowMs);

    /// <summary>
    /// Releases keys we think are down but <paramref name="isPhysicallyDown"/> says are not, as if
    /// their key-up had arrived now. Key-ups go missing when focus moves to an elevated window or the
    /// secure desktop. Swallowed trigger keys are skipped: the OS never saw them go down, so their
    /// async state is always "up". Returns true if anything was released.
    /// </summary>
    public bool ReconcileHeld(Func<int, bool> isPhysicallyDown, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(isPhysicallyDown);
        Advance(nowMs);

        var changed = false;
        foreach (var vk in _modifiersDown.Keys.ToArray())
        {
            if (!isPhysicallyDown(vk))
            {
                OnModifierUp(vk, nowMs);
                changed = true;
            }
        }

        foreach (var vk in _keysDown.Keys.ToArray())
        {
            if (!_swallowed.Contains(vk) && !isPhysicallyDown(vk))
            {
                _keysDown.Remove(vk);
                changed = true;
            }
        }

        // The real Win key-up already happened unseen; there is nothing left to mask.
        MaskKeyRequested = false;
        return changed;
    }

    /// <summary>Forgets all key state (session lock/switch, hook restart). An active hold is cancelled.</summary>
    public void Reset(long nowMs)
    {
        if (_hold is not null)
        {
            EndHold(HotkeyPhase.Cancelled, nowMs);
        }

        _modifiersDown.Clear();
        _keysDown.Clear();
        _swallowed.Clear();
        _sessionActive = false;
        _candidate = null;
        MaskKeyRequested = false;
        ResetTaps();
    }

    private HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        foreach (var vk in _modifiersDown.Keys)
        {
            modifiers |= VirtualKeys.ModifierOf(vk);
        }

        return modifiers;
    }

    private void Advance(long now)
    {
        if (_candidate is not null && now >= _armAt)
        {
            var binding = _candidate;
            _candidate = null;
            _hold = binding;
            _holdTriggerVk = 0;
            _sessionArmed = true;
            Emit(binding.Action, HotkeyPhase.Started, _armAt);
        }

        if (_tapCount > 0 && TapSequenceBroken(now))
        {
            ResolveTaps(now);
        }
    }

    private bool TapSequenceBroken(long now)
    {
        if (!_sessionActive)
        {
            return now - _lastTapEnd > MultiTapGapMs;
        }

        // A session in progress can still be the next tap only while it is short, clean, started in
        // time and uses no modifier outside the chord.
        return _sessionDirty
            || _sessionArmed
            || now - _sessionStart > TapMaxMs
            || _sessionStart - _lastTapEnd > MultiTapGapMs
            || (_sessionMax & ~_tapChord) != 0;
    }

    private bool OnModifierDown(int vk, long now)
    {
        if (_modifiersDown.ContainsKey(vk))
        {
            // Auto-repeat. Never stale-checked: a modifier stops repeating as soon as another key is
            // pressed while it stays down, so its timestamp says nothing about whether it is held.
            _modifiersDown[vk] = now;
            return false;
        }

        var before = CurrentModifiers();
        _modifiersDown[vk] = now;
        var after = CurrentModifiers();

        if (!_sessionActive)
        {
            StartSession(now);
        }

        _sessionMax |= after;
        if (after == before)
        {
            // The other side of a modifier that was already down.
            return false;
        }

        if (_hold is not null && after != _hold.Gesture.Chord.Modifiers)
        {
            EndHold(HotkeyPhase.Cancelled, now);
        }

        UpdateHoldCandidate(after, now);
        return false;
    }

    private bool OnModifierUp(int vk, long now)
    {
        var before = CurrentModifiers();
        if (!_modifiersDown.Remove(vk))
        {
            // Pressed before we started listening.
            return false;
        }

        var after = CurrentModifiers();
        if (_hold is not null)
        {
            var required = _hold.Gesture.Chord.Modifiers;
            if ((after & required) != required)
            {
                EndHold(HotkeyPhase.Ended, now);
            }
        }

        if (after != before)
        {
            _candidate = null;
            _sessionShrunk = after != HotkeyModifiers.None;
        }

        var swallow = false;
        if ((before & HotkeyModifiers.Win) != 0
            && (after & HotkeyModifiers.Win) == 0
            && _sessionActive
            && !_sessionDirty
            && OwnsModifierChord(_sessionMax))
        {
            MaskKeyRequested = true;
            swallow = true;
        }

        if (after == HotkeyModifiers.None && _sessionActive)
        {
            EndSession(now);
        }

        return swallow;
    }

    private bool OnKeyDown(int vk, long now)
    {
        if (_keysDown.TryGetValue(vk, out var last) && now - last <= StaleKeyMs)
        {
            _keysDown[vk] = now;
            return _swallowed.Contains(vk);
        }

        _swallowed.Remove(vk);
        _keysDown[vk] = now;

        _candidate = null;
        if (_sessionActive)
        {
            _sessionDirty = true;
        }

        if (_tapCount > 0)
        {
            ResolveTaps(now);
        }

        if (_hold is not null)
        {
            EndHold(HotkeyPhase.Cancelled, now);
        }

        var modifiers = CurrentModifiers();
        var binding = FindTriggered(vk, modifiers);
        if (binding is null)
        {
            return false;
        }

        _swallowed.Add(vk);
        if (binding.Gesture.Kind == HotkeyGestureKind.Press)
        {
            Emit(binding.Action, HotkeyPhase.Fired, now);
        }
        else
        {
            _hold = binding;
            _holdTriggerVk = vk;
            _sessionArmed = _sessionActive;
            Emit(binding.Action, HotkeyPhase.Started, now);
        }

        return true;
    }

    private bool OnKeyUp(int vk, long now)
    {
        _keysDown.Remove(vk);
        var swallowed = _swallowed.Remove(vk);
        if (_hold is not null && _holdTriggerVk == vk)
        {
            EndHold(HotkeyPhase.Ended, now);
        }

        return swallowed;
    }

    private void StartSession(long now)
    {
        // Keys that are genuinely held keep auto-repeating until this modifier went down, so anything
        // older than that is a key-up we never saw.
        foreach (var (vk, last) in _keysDown.ToArray())
        {
            if (now - last > StaleKeyMs)
            {
                _keysDown.Remove(vk);
                _swallowed.Remove(vk);
            }
        }

        _sessionActive = true;
        _sessionStart = now;
        _sessionMax = HotkeyModifiers.None;
        _sessionDirty = _keysDown.Count > 0;
        _sessionShrunk = false;
        _sessionArmed = false;
    }

    private void EndSession(long now)
    {
        _sessionActive = false;
        var isTap = !_sessionDirty
            && !_sessionArmed
            && _sessionMax != HotkeyModifiers.None
            && now - _sessionStart <= TapMaxMs;

        if (isTap)
        {
            RegisterTap(_sessionMax, now);
        }
        else if (_tapCount > 0)
        {
            ResolveTaps(now);
        }
    }

    private void UpdateHoldCandidate(HotkeyModifiers modifiers, long now)
    {
        _candidate = null;
        if (_hold is not null || !_sessionActive || _sessionDirty || _sessionShrunk)
        {
            return;
        }

        foreach (var binding in _bindings)
        {
            var gesture = binding.Gesture;
            if (gesture.Kind == HotkeyGestureKind.Hold && !gesture.Chord.HasTrigger && gesture.Chord.Modifiers == modifiers)
            {
                _candidate = binding;
                _armAt = now + HoldArmMs;
                return;
            }
        }
    }

    private void RegisterTap(HotkeyModifiers chord, long now)
    {
        if (_tapCount > 0 && chord == _tapChord && _sessionStart - _lastTapEnd <= MultiTapGapMs)
        {
            _tapCount++;
        }
        else
        {
            if (_tapCount > 0)
            {
                ResolveTaps(now);
            }

            _tapChord = chord;
            _tapCount = 1;
        }

        _lastTapEnd = now;

        var doubleTap = FindTap(HotkeyGestureKind.DoubleTap, chord);
        var tripleTap = FindTap(HotkeyGestureKind.TripleTap, chord);
        if (_tapCount == 2 && doubleTap is not null && tripleTap is null)
        {
            // Nothing longer to wait for, so no added latency.
            Emit(doubleTap.Action, HotkeyPhase.Fired, now);
            ResetTaps();
        }
        else if (_tapCount >= 3)
        {
            if (tripleTap is not null)
            {
                Emit(tripleTap.Action, HotkeyPhase.Fired, now);
            }

            ResetTaps();
        }
        else if (doubleTap is null && tripleTap is null)
        {
            // Nobody listens for taps of this chord; do not keep a timer running for it.
            ResetTaps();
        }
    }

    /// <summary>The tap sequence is over: fire a double tap that was held back for a possible third.</summary>
    private void ResolveTaps(long now)
    {
        if (_tapCount == 2 && FindTap(HotkeyGestureKind.DoubleTap, _tapChord) is { } doubleTap)
        {
            Emit(doubleTap.Action, HotkeyPhase.Fired, now);
        }

        ResetTaps();
    }

    private void ResetTaps()
    {
        _tapCount = 0;
        _tapChord = HotkeyModifiers.None;
    }

    private void EndHold(HotkeyPhase phase, long now)
    {
        var binding = _hold!;
        _hold = null;
        _holdTriggerVk = 0;
        Emit(binding.Action, phase, now);
    }

    /// <summary>True if a modifier-only binding uses exactly these modifiers, including Win.</summary>
    private bool OwnsModifierChord(HotkeyModifiers modifiers)
    {
        if ((modifiers & HotkeyModifiers.Win) == 0)
        {
            return false;
        }

        foreach (var binding in _bindings)
        {
            if (!binding.Gesture.Chord.HasTrigger && binding.Gesture.Chord.Modifiers == modifiers)
            {
                return true;
            }
        }

        return false;
    }

    private HotkeyBinding? FindTriggered(int vk, HotkeyModifiers modifiers)
    {
        foreach (var binding in _bindings)
        {
            var gesture = binding.Gesture;
            if (gesture.Chord.TriggerVk == vk
                && gesture.Chord.Modifiers == modifiers
                && gesture.Kind is HotkeyGestureKind.Press or HotkeyGestureKind.Hold)
            {
                return binding;
            }
        }

        return null;
    }

    private HotkeyBinding? FindTap(HotkeyGestureKind kind, HotkeyModifiers chord)
    {
        foreach (var binding in _bindings)
        {
            var gesture = binding.Gesture;
            if (gesture.Kind == kind && !gesture.Chord.HasTrigger && gesture.Chord.Modifiers == chord)
            {
                return binding;
            }
        }

        return null;
    }

    private void Emit(HotkeyAction action, HotkeyPhase phase, long timestamp) => _emit(new HotkeyEvent(action, phase, timestamp));
}
