using System.Text;

namespace Clicky.Core.Hotkeys;

/// <summary>Side-agnostic modifiers: left and right Ctrl are the same Ctrl.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A set of modifiers plus an optional non-modifier trigger key (0 for modifier-only chords).</summary>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, int TriggerVk = 0)
{
    public bool HasTrigger => TriggerVk != 0;

    /// <summary>"Ctrl+Alt", "Ctrl+Alt+A", "Win+F13". Modifiers always in Ctrl, Alt, Shift, Win order.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        Append(text, HotkeyModifiers.Ctrl, "Ctrl");
        Append(text, HotkeyModifiers.Alt, "Alt");
        Append(text, HotkeyModifiers.Shift, "Shift");
        Append(text, HotkeyModifiers.Win, "Win");
        if (HasTrigger)
        {
            if (text.Length > 0)
            {
                text.Append('+');
            }

            text.Append(VirtualKeys.Name(TriggerVk));
        }

        return text.ToString();
    }

    /// <summary>Parses "Ctrl+Alt+A" style text (case-insensitive, Control/Windows aliases allowed).</summary>
    public static bool TryParse(string? text, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var trigger = 0;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = ParseModifier(part);
            if (modifier != HotkeyModifiers.None)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            // At most one trigger key, and only one that is not itself a modifier.
            if (trigger != 0 || !VirtualKeys.TryParse(part, out trigger))
            {
                return false;
            }
        }

        chord = new HotkeyChord(modifiers, trigger);
        return true;
    }

    private static HotkeyModifiers ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private void Append(StringBuilder text, HotkeyModifiers modifier, string name)
    {
        if ((Modifiers & modifier) == 0)
        {
            return;
        }

        if (text.Length > 0)
        {
            text.Append('+');
        }

        text.Append(name);
    }
}
