using Clicky.Core.Hotkeys;
using static Clicky.Core.Hotkeys.VirtualKeys;

namespace Clicky.Tests.Hotkeys;

public sealed class GestureEngineTests
{
    private const int KeyA = 'A';
    private const int KeyC = 'C';
    private const int KeyQ = 'Q';

    // _events: what most tests care about, without the Pending/Cancelled pair of a hold that never
    // armed. _allEvents: everything, for the Pending tests.
    private readonly List<HotkeyEvent> _events = [];
    private readonly List<HotkeyEvent> _allEvents = [];
    private readonly HashSet<HotkeyAction> _pending = [];
    private readonly GestureEngine _engine;
    private long _now = 10_000;

    public GestureEngineTests()
    {
        _engine = new GestureEngine(Record);
        Bind(Defaults(tripleTapEnabled: false));
    }

    [Fact]
    public void Ctrl_alt_hold_is_pending_as_soon_as_both_keys_are_down()
    {
        Down(LeftControl);
        Assert.Empty(_allEvents);

        Down(LeftMenu);
        var pending = Assert.Single(_allEvents);
        Assert.Equal((HotkeyAction.Talk, HotkeyPhase.Pending), (pending.Action, pending.Phase));
        Assert.Equal(_now, pending.TimestampMs);

        Wait(GestureEngine.HoldArmMs);
        Up(LeftMenu);
        Up(LeftControl);
        AssertAllEvents(
            (HotkeyAction.Talk, HotkeyPhase.Pending),
            (HotkeyAction.Talk, HotkeyPhase.Started),
            (HotkeyAction.Talk, HotkeyPhase.Ended));
    }

    [Fact]
    public void Pending_talk_is_cancelled_by_ctrl_alt_a_before_open_home_fires()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(50);
        Tap(KeyA);
        Up(LeftMenu);
        Up(LeftControl);

        AssertAllEvents(
            (HotkeyAction.Talk, HotkeyPhase.Pending),
            (HotkeyAction.Talk, HotkeyPhase.Cancelled),
            (HotkeyAction.OpenHome, HotkeyPhase.Fired));
    }

    [Fact]
    public void Pending_talk_is_cancelled_by_a_quick_release_and_by_adding_a_modifier()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Up(LeftMenu);
        Up(LeftControl);
        AssertAllEvents((HotkeyAction.Talk, HotkeyPhase.Pending), (HotkeyAction.Talk, HotkeyPhase.Cancelled));

        _allEvents.Clear();
        Wait(1000);
        Down(LeftControl);
        Down(LeftMenu);
        Down(LeftShift);
        Wait(500);
        Up(LeftShift);
        Up(LeftMenu);
        Up(LeftControl);
        AssertAllEvents((HotkeyAction.Talk, HotkeyPhase.Pending), (HotkeyAction.Talk, HotkeyPhase.Cancelled));
    }

    [Fact]
    public void Every_pending_ends_in_started_or_cancelled()
    {
        // A mix of everything: holds, taps, presses, AltGr, a reset mid-hold.
        Down(LeftControl);
        Down(LeftMenu);
        Wait(300);
        Up(LeftControl);
        Up(LeftMenu);
        Tap(LeftControl);
        Tap(LeftControl);
        Down(LeftControl);
        Down(LeftWin);
        Wait(20);
        Up(LeftWin);
        Up(LeftControl);
        Down(LeftControl);
        Down(LeftMenu);
        _engine.Reset(_now);
        Wait(1000);

        var open = new HashSet<HotkeyAction>();
        foreach (var e in _allEvents)
        {
            switch (e.Phase)
            {
                case HotkeyPhase.Pending:
                    Assert.True(open.Add(e.Action));
                    break;
                case HotkeyPhase.Started when open.Remove(e.Action):
                    break;
                case HotkeyPhase.Cancelled:
                    open.Remove(e.Action);
                    break;
            }
        }

        Assert.Empty(open);
    }

    [Fact]
    public void Ctrl_alt_hold_arms_after_200_ms_and_ends_on_release()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(GestureEngine.HoldArmMs - 1);
        Assert.Empty(_events);

        Wait(1);
        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started));
        Assert.True(_engine.IsHoldActive);

        Wait(800);
        Up(LeftMenu);
        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Ended));
        Up(LeftControl);
        Assert.Equal(2, _events.Count);
    }

    [Fact]
    public void Hold_arms_from_the_key_event_timestamp_even_if_the_timer_is_late()
    {
        Down(RightControl);
        Down(RightMenu);
        var altDown = _now;
        _now += 500;

        // No Tick in between: the release itself must first arm, then end.
        Up(RightMenu);

        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Ended));
        Assert.Equal(altDown + GestureEngine.HoldArmMs, _events[0].TimestampMs);
        Assert.Equal(_now, _events[1].TimestampMs);
    }

    [Fact]
    public void Ctrl_alt_a_fires_open_home_and_not_talk()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(50);

        Assert.True(Down(KeyA));
        Assert.True(Down(KeyA)); // auto-repeat stays swallowed and does not fire again
        Assert.True(Up(KeyA));
        Wait(1000);
        Up(LeftMenu);
        Up(LeftControl);

        AssertEvents((HotkeyAction.OpenHome, HotkeyPhase.Fired));
    }

    [Fact]
    public void Ctrl_alt_a_after_talk_armed_cancels_talk_then_opens_home()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(400);
        Assert.True(Down(KeyA));
        Up(KeyA);
        Up(LeftMenu);
        Up(LeftControl);

        AssertEvents(
            (HotkeyAction.Talk, HotkeyPhase.Started),
            (HotkeyAction.Talk, HotkeyPhase.Cancelled),
            (HotkeyAction.OpenHome, HotkeyPhase.Fired));
    }

    [Fact]
    public void Altgr_typing_fires_nothing()
    {
        // German layout: AltGr+Q types '@'. Windows sends a fake LCtrl (scan 0x21D) ahead of RAlt.
        for (var i = 0; i < 3; i++)
        {
            Assert.False(_engine.HandleKey(Key(LeftControl, down: true, scanCode: 0x21D)));
            Assert.False(Down(RightMenu));
            Wait(400);
            Assert.False(Down(KeyQ));
            Assert.False(Up(KeyQ));
            Assert.False(_engine.HandleKey(Key(LeftControl, down: false, scanCode: 0x21D)));
            Assert.False(Up(RightMenu));
            Wait(100);
        }

        Wait(1000);
        Assert.Empty(_events);
    }

    [Fact]
    public void Ctrl_c_is_not_a_tap()
    {
        Tap(LeftControl, KeyC);
        Wait(50);
        Tap(LeftControl, KeyC);
        Wait(1000);

        Assert.Empty(_events);
    }

    [Fact]
    public void Double_tap_ctrl_fires_on_the_second_release_when_triple_is_disabled()
    {
        Tap(LeftControl);
        Wait(100);
        Tap(RightControl);

        AssertEvents((HotkeyAction.TextMode, HotkeyPhase.Fired));
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void Double_tap_ctrl_waits_out_the_window_when_triple_is_enabled()
    {
        Bind(Defaults(tripleTapEnabled: true));

        Tap(LeftControl);
        Wait(100);
        Tap(LeftControl);
        var secondRelease = _now;
        Assert.Empty(_events);
        Assert.Equal(secondRelease + GestureEngine.MultiTapGapMs + 1, _engine.NextDeadline);

        Wait(GestureEngine.MultiTapGapMs);
        Assert.Empty(_events);
        Wait(1);
        AssertEvents((HotkeyAction.TextMode, HotkeyPhase.Fired));
    }

    [Fact]
    public void Triple_tap_ctrl_fires_always_on_voice_and_not_text_mode()
    {
        Bind(Defaults(tripleTapEnabled: true));

        Tap(LeftControl);
        Wait(150);
        Tap(LeftControl);
        Wait(150);
        Tap(LeftControl);
        Wait(1000);

        AssertEvents((HotkeyAction.AlwaysOnVoice, HotkeyPhase.Fired));
    }

    [Fact]
    public void Pending_double_tap_fires_as_soon_as_the_next_press_cannot_be_a_tap()
    {
        Bind(Defaults(tripleTapEnabled: true));

        Tap(LeftControl);
        Wait(100);
        Tap(LeftControl);
        Wait(100);
        Down(KeyC);

        AssertEvents((HotkeyAction.TextMode, HotkeyPhase.Fired));
    }

    [Fact]
    public void Slow_taps_do_not_count_up()
    {
        Tap(LeftControl);
        Wait(GestureEngine.MultiTapGapMs + 1);
        Tap(LeftControl);
        Wait(1000);

        Assert.Empty(_events);
    }

    [Fact]
    public void Long_press_is_not_a_tap()
    {
        Tap(LeftControl);
        Wait(100);
        Down(LeftControl);
        Wait(GestureEngine.TapMaxMs + 1);
        Up(LeftControl);
        Wait(1000);

        Assert.Empty(_events);
    }

    [Fact]
    public void Ctrl_win_hold_and_double_tap_coexist()
    {
        Down(LeftControl);
        Down(LeftWin);
        Wait(GestureEngine.HoldArmMs + 100);
        Up(LeftWin);
        Up(LeftControl);
        AssertEvents((HotkeyAction.Dictation, HotkeyPhase.Started), (HotkeyAction.Dictation, HotkeyPhase.Ended));
        _events.Clear();

        Wait(1000);
        Tap(LeftControl, LeftWin);
        Wait(80);
        Tap(LeftControl, LeftWin);

        AssertEvents((HotkeyAction.DictationHandsFree, HotkeyPhase.Fired));
    }

    [Fact]
    public void Win_release_after_a_ctrl_win_gesture_requests_the_mask_key()
    {
        Down(LeftControl);
        Down(LeftWin);
        Wait(GestureEngine.HoldArmMs + 10);

        // Ctrl first: the hold ends, but Win still has to be released without opening Start.
        Up(LeftControl);
        Assert.False(_engine.MaskKeyRequested);
        Assert.True(Up(LeftWin));
        Assert.True(_engine.MaskKeyRequested);

        // A tap is short enough that the hold never arms, and still gets masked.
        Down(LeftControl);
        Down(LeftWin);
        Wait(40);
        Assert.True(Up(LeftWin));
        Assert.True(_engine.MaskKeyRequested);
        Assert.False(Up(LeftControl));
        Assert.False(_engine.MaskKeyRequested);
    }

    [Fact]
    public void Win_shortcuts_and_plain_win_are_left_alone()
    {
        // Win+D: a key between Win down and up already keeps Start closed.
        Down(LeftWin);
        Down('D');
        Up('D');
        Assert.False(Up(LeftWin));
        Assert.False(_engine.MaskKeyRequested);

        // Win on its own still opens Start.
        Wait(500);
        Down(LeftWin);
        Assert.False(Up(LeftWin));
        Assert.False(_engine.MaskKeyRequested);

        // Ctrl+Win+Left (switch desktop) is not ours either.
        Down(LeftControl);
        Down(LeftWin);
        Down(0x25);
        Up(0x25);
        Assert.False(Up(LeftWin));
        Up(LeftControl);
        Assert.Empty(_events);
    }

    [Fact]
    public void Injected_events_are_ignored_entirely()
    {
        Assert.False(_engine.HandleKey(Key(LeftControl, down: true, injected: true)));
        Assert.False(_engine.HandleKey(Key(LeftMenu, down: true, injected: true)));
        Wait(1000);
        Assert.False(_engine.HandleKey(Key(KeyA, down: true, injected: true)));

        Assert.Empty(_events);
        Assert.False(_engine.HasKeysDown);
    }

    [Fact]
    public void Reconcile_ends_a_hold_whose_key_up_was_missed()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(300);
        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started));

        // Focus moved to an elevated window and the key-ups never reached us.
        Wait(100);
        Assert.False(_engine.ReconcileHeld(vk => vk is LeftControl or LeftMenu, _now));
        Assert.True(_engine.ReconcileHeld(vk => vk == LeftControl, _now));

        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Ended));
        Assert.True(_engine.ReconcileHeld(_ => false, _now));
        Assert.False(_engine.HasKeysDown);
    }

    [Fact]
    public void Extra_modifier_cancels_an_armed_hold()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(300);
        Down(LeftShift);
        Up(LeftShift);
        Up(LeftMenu);
        Up(LeftControl);

        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Cancelled));
    }

    [Fact]
    public void Releasing_down_to_the_chord_does_not_arm()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Down(LeftShift);
        Wait(50);
        Up(LeftShift);
        Wait(1000);
        Up(LeftMenu);
        Up(LeftControl);

        Assert.Empty(_events);
    }

    [Fact]
    public void Key_held_before_the_modifiers_blocks_the_hold()
    {
        Down('W');
        Wait(30);
        Down('W');
        Down(LeftControl);
        Down(LeftMenu);
        Wait(1000);

        Assert.Empty(_events);
    }

    [Fact]
    public void Stale_key_with_a_missed_key_up_does_not_block_the_hold_or_eat_the_next_press()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Assert.True(Down(KeyA));
        AssertEvents((HotkeyAction.OpenHome, HotkeyPhase.Fired));
        Up(LeftMenu);
        Up(LeftControl);

        // The A key-up was lost. Much later A is pressed on its own: it must reach the app.
        Wait(GestureEngine.StaleKeyMs + 1);
        Assert.False(Down(KeyA));
        Assert.False(Up(KeyA));
    }

    [Fact]
    public void Reset_cancels_an_active_hold_and_forgets_keys()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(300);

        _engine.Reset(_now);

        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Cancelled));
        Assert.False(_engine.HasKeysDown);
        Assert.Null(_engine.NextDeadline);
    }

    [Fact]
    public void Removing_the_active_binding_cancels_its_hold()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Wait(300);

        _engine.UpdateBindings([new HotkeyBinding(HotkeyAction.OpenHome, HotkeyGesture.Press(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, KeyA))], _now);

        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Cancelled));
    }

    [Fact]
    public void Trigger_hold_starts_on_key_down_and_ends_on_key_up()
    {
        Bind([new HotkeyBinding(HotkeyAction.Talk, HotkeyGesture.Hold(HotkeyModifiers.Ctrl, Space))]);

        Down(LeftControl);
        Assert.True(Down(Space));
        Wait(40);
        Assert.True(Down(Space));
        Assert.True(Up(Space));
        Up(LeftControl);

        AssertEvents((HotkeyAction.Talk, HotkeyPhase.Started), (HotkeyAction.Talk, HotkeyPhase.Ended));
    }

    [Fact]
    public void Invalid_bindings_are_dropped()
    {
        Bind([
            new HotkeyBinding(HotkeyAction.Talk, HotkeyGesture.Hold(HotkeyModifiers.Ctrl)),
            new HotkeyBinding(HotkeyAction.OpenHome, HotkeyGesture.Press(HotkeyModifiers.None, KeyA)),
        ]);

        Assert.Empty(_engine.Bindings);
        Assert.False(Down(KeyA));
    }

    private static HotkeyBinding[] Defaults(bool tripleTapEnabled)
    {
        var bindings = new List<HotkeyBinding>
        {
            new(HotkeyAction.Talk, HotkeyGesture.Hold(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)),
            new(HotkeyAction.TextMode, HotkeyGesture.DoubleTap(HotkeyModifiers.Ctrl)),
            new(HotkeyAction.Dictation, HotkeyGesture.Hold(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)),
            new(HotkeyAction.DictationHandsFree, HotkeyGesture.DoubleTap(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)),
            new(HotkeyAction.OpenHome, HotkeyGesture.Press(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, KeyA)),
        };

        if (tripleTapEnabled)
        {
            bindings.Add(new(HotkeyAction.AlwaysOnVoice, HotkeyGesture.TripleTap(HotkeyModifiers.Ctrl)));
        }

        return [.. bindings];
    }

    private void Bind(IEnumerable<HotkeyBinding> bindings) => _engine.UpdateBindings(bindings, _now);

    private KeyEvent Key(int vk, bool down, int scanCode = 0x1D, bool injected = false) =>
        new(vk, scanCode, down, IsExtended: false, injected, _now);

    private bool Down(int vk)
    {
        _now += 5;
        return _engine.HandleKey(Key(vk, down: true));
    }

    private bool Up(int vk)
    {
        _now += 5;
        return _engine.HandleKey(Key(vk, down: false));
    }

    /// <summary>Presses the keys in order and releases them in reverse, 5 ms apart.</summary>
    private void Tap(params int[] keys)
    {
        foreach (var vk in keys)
        {
            Down(vk);
        }

        foreach (var vk in keys.Reverse())
        {
            Up(vk);
        }
    }

    /// <summary>Advances the clock, ticking at every deadline on the way like the hook thread's timer.</summary>
    private void Wait(int ms)
    {
        var target = _now + ms;
        while (_engine.NextDeadline is { } deadline && deadline <= target)
        {
            _now = Math.Max(_now, deadline);
            _engine.Tick(_now);
        }

        _now = target;
        _engine.Tick(_now);
    }

    private void AssertEvents(params (HotkeyAction Action, HotkeyPhase Phase)[] expected) =>
        Assert.Equal(expected, _events.Select(e => (e.Action, e.Phase)));

    private void AssertAllEvents(params (HotkeyAction Action, HotkeyPhase Phase)[] expected) =>
        Assert.Equal(expected, _allEvents.Select(e => (e.Action, e.Phase)));

    private void Record(HotkeyEvent e)
    {
        _allEvents.Add(e);
        switch (e.Phase)
        {
            case HotkeyPhase.Pending:
                Assert.True(_pending.Add(e.Action), $"{e.Action} was already pending");
                return;
            case HotkeyPhase.Cancelled when _pending.Remove(e.Action):
                return;
            case HotkeyPhase.Started:
                _pending.Remove(e.Action);
                break;
        }

        _events.Add(e);
    }
}
