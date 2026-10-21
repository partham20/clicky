using Clicky.Core.Hotkeys;

namespace Clicky.Tests.Hotkeys;

public sealed class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt", HotkeyGestureKind.Hold, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0)]
    [InlineData("Ctrl+Alt+A", HotkeyGestureKind.Press, HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A')]
    [InlineData("double Ctrl", HotkeyGestureKind.DoubleTap, HotkeyModifiers.Ctrl, 0)]
    [InlineData("triple Ctrl", HotkeyGestureKind.TripleTap, HotkeyModifiers.Ctrl, 0)]
    [InlineData("double Ctrl+Win", HotkeyGestureKind.DoubleTap, HotkeyModifiers.Ctrl | HotkeyModifiers.Win, 0)]
    [InlineData("hold Ctrl+Space", HotkeyGestureKind.Hold, HotkeyModifiers.Ctrl, 0x20)]
    [InlineData("Win+Shift+F5", HotkeyGestureKind.Press, HotkeyModifiers.Shift | HotkeyModifiers.Win, 0x74)]
    [InlineData("F13", HotkeyGestureKind.Press, HotkeyModifiers.None, 0x7C)]
    public void Parses_settings_text(string text, HotkeyGestureKind kind, HotkeyModifiers modifiers, int trigger)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(new HotkeyChord(modifiers, trigger), kind), gesture);
    }

    [Theory]
    [InlineData("  control + ALT + a ", "Ctrl+Alt+A")]
    [InlineData("DOUBLE ctrl", "double Ctrl")]
    [InlineData("Alt+Ctrl", "Ctrl+Alt")]
    [InlineData("Windows+Ctrl", "Ctrl+Win")]
    [InlineData("press Ctrl+Esc", "Ctrl+Escape")]
    [InlineData("Ctrl+0xE9", "Ctrl+0xE9")]
    public void Formats_canonically(string input, string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(input, out var gesture));
        Assert.Equal(canonical, gesture.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+Ctrl")]
    [InlineData("Ctrl+A+B")]
    [InlineData("quadruple Ctrl")]
    [InlineData("Ctrl+Nonsense")]
    [InlineData("Ctrl+")]
    public void Rejects_malformed_text(string text) => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Fact]
    public void Every_kind_with_every_modifier_set_and_named_key_round_trips()
    {
        int[] triggers = [0, 'A', 'Z', '0', 0x20, 0x70, 0x87, 0x25, 0xBA, 0xDE, 0x60, 0x6F, 0x2E, 0x0D];
        foreach (var kind in Enum.GetValues<HotkeyGestureKind>())
        {
            for (var modifiers = 0; modifiers < 16; modifiers++)
            {
                foreach (var trigger in triggers)
                {
                    if (modifiers == 0 && trigger == 0)
                    {
                        continue;
                    }

                    var gesture = new HotkeyGesture(new HotkeyChord((HotkeyModifiers)modifiers, trigger), kind);
                    Assert.True(HotkeyGesture.TryParse(gesture.ToString(), out var parsed), gesture.ToString());
                    Assert.Equal(gesture, parsed);
                }
            }
        }
    }

    [Theory]
    [InlineData("Ctrl+Alt", true)]
    [InlineData("Ctrl", false)]
    [InlineData("hold Shift", false)]
    [InlineData("Ctrl+Alt+A", true)]
    [InlineData("A", false)]
    [InlineData("F13", true)]
    [InlineData("press Ctrl", false)]
    [InlineData("double Ctrl", true)]
    [InlineData("double Ctrl+A", false)]
    public void Validate_rejects_gestures_that_would_break_typing(string text, bool valid)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(valid, gesture.Validate() is null);
    }

    [Fact]
    public void Display_text_reads_naturally()
    {
        Assert.Equal("Hold Ctrl+Alt", HotkeyGesture.Hold(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt).ToDisplayString());
        Assert.Equal("Ctrl+Alt+A", HotkeyGesture.Press(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'A').ToDisplayString());
        Assert.Equal("Double-tap Ctrl+Win", HotkeyGesture.DoubleTap(HotkeyModifiers.Ctrl | HotkeyModifiers.Win).ToDisplayString());
        Assert.Equal("Triple-tap Ctrl", HotkeyGesture.TripleTap(HotkeyModifiers.Ctrl).ToDisplayString());
    }
}
