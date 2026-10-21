using Clicky.Core.Hotkeys;
using static Clicky.Core.Hotkeys.VirtualKeys;

namespace Clicky.Tests.Hotkeys;

public sealed class GestureRecorderTests
{
    private readonly List<RecorderOutcome> _outcomes = [];
    private readonly GestureRecorder _recorder;
    private long _now = 50_000;

    public GestureRecorderTests() => _recorder = new GestureRecorder(_outcomes.Add);

    [Fact]
    public void Modifiers_with_a_key_record_a_press()
    {
        Down(LeftControl);
        Down(LeftMenu);
        Down('K');

        Assert.Equal(Recorded(HotkeyGesture.Press(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'K')), Assert.Single(_outcomes));
    }

    [Fact]
    public void Held_modifiers_record_a_hold()
    {
        Down(LeftControl);
        Down(LeftWin);
        Wait(600);
        Up(LeftWin);
        Up(LeftControl);

        Assert.Equal(Recorded(HotkeyGesture.Hold(HotkeyModifiers.Ctrl | HotkeyModifiers.Win)), Assert.Single(_outcomes));
    }

    [Fact]
    public void Quick_taps_record_double_and_triple()
    {
        Tap(LeftControl);
        Wait(100);
        Tap(LeftControl);
        Assert.Empty(_outcomes);
        Wait(GestureEngine.MultiTapGapMs + 1);
        Assert.Equal(Recorded(HotkeyGesture.DoubleTap(HotkeyModifiers.Ctrl)), Assert.Single(_outcomes));

        _outcomes.Clear();
        Wait(1000);
        Tap(LeftShift);
        Wait(100);
        Tap(RightShift);
        Wait(100);
        Tap(LeftShift);
        Assert.Equal(Recorded(HotkeyGesture.TripleTap(HotkeyModifiers.Shift)), Assert.Single(_outcomes));
    }

    [Fact]
    public void Escape_cancels_and_backspace_clears()
    {
        Down(Escape);
        Up(Escape);
        Down(Backspace);

        Assert.Equal([new RecorderOutcome(RecorderOutcomeKind.Cancelled), new RecorderOutcome(RecorderOutcomeKind.Cleared)], _outcomes);
    }

    [Fact]
    public void Bare_keys_and_single_held_modifiers_are_refused_with_a_hint()
    {
        Down('Q');
        Up('Q');
        Assert.Contains("Ctrl", _recorder.Prompt, StringComparison.Ordinal);

        Down(LeftShift);
        Wait(800);
        Up(LeftShift);
        Assert.Empty(_outcomes);
        Assert.Contains("two modifiers", _recorder.Prompt, StringComparison.Ordinal);
    }

    private static RecorderOutcome Recorded(HotkeyGesture gesture) => new(RecorderOutcomeKind.Recorded, gesture);

    private void Down(int vk)
    {
        _now += 5;
        _recorder.HandleKey(new KeyEvent(vk, 0, IsDown: true, IsExtended: false, IsInjected: false, _now));
    }

    private void Up(int vk)
    {
        _now += 5;
        _recorder.HandleKey(new KeyEvent(vk, 0, IsDown: false, IsExtended: false, IsInjected: false, _now));
    }

    private void Tap(int vk)
    {
        Down(vk);
        Up(vk);
    }

    private void Wait(int ms)
    {
        _now += ms;
        _recorder.Tick(_now);
    }
}
