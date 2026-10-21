namespace Clicky.Core.Hotkeys;

public enum HotkeyGestureKind
{
    /// <summary>Active while held. Modifier-only holds arm after <see cref="GestureEngine.HoldArmMs"/>.</summary>
    Hold,

    /// <summary>Fires once when the trigger key goes down with exactly the chord's modifiers.</summary>
    Press,

    /// <summary>The modifiers tapped twice in quick succession.</summary>
    DoubleTap,

    /// <summary>The modifiers tapped three times in quick succession.</summary>
    TripleTap,
}

/// <summary>A chord and how it is performed. Text form is what settings.json stores.</summary>
/// <remarks>
/// Text form: a modifier-only chord on its own is a hold ("Ctrl+Alt"), a chord with a trigger key on
/// its own is a press ("Ctrl+Alt+A"), and the "double", "triple", "hold" and "press" prefixes say
/// so explicitly ("double Ctrl", "hold Ctrl+Space").
/// </remarks>
public readonly record struct HotkeyGesture(HotkeyChord Chord, HotkeyGestureKind Kind)
{
    private const string HoldPrefix = "hold";
    private const string PressPrefix = "press";
    private const string DoublePrefix = "double";
    private const string TriplePrefix = "triple";

    public static HotkeyGesture Hold(HotkeyModifiers modifiers, int triggerVk = 0) => new(new HotkeyChord(modifiers, triggerVk), HotkeyGestureKind.Hold);

    public static HotkeyGesture Press(HotkeyModifiers modifiers, int triggerVk) => new(new HotkeyChord(modifiers, triggerVk), HotkeyGestureKind.Press);

    public static HotkeyGesture DoubleTap(HotkeyModifiers modifiers) => new(new HotkeyChord(modifiers), HotkeyGestureKind.DoubleTap);

    public static HotkeyGesture TripleTap(HotkeyModifiers modifiers) => new(new HotkeyChord(modifiers), HotkeyGestureKind.TripleTap);

    /// <summary>Canonical settings text; <see cref="TryParse"/> reads it back to an equal gesture.</summary>
    public override string ToString() => Kind switch
    {
        HotkeyGestureKind.Hold when !Chord.HasTrigger => Chord.ToString(),
        HotkeyGestureKind.Hold => $"{HoldPrefix} {Chord}",
        HotkeyGestureKind.Press when Chord.HasTrigger => Chord.ToString(),
        HotkeyGestureKind.Press => $"{PressPrefix} {Chord}",
        HotkeyGestureKind.DoubleTap => $"{DoublePrefix} {Chord}",
        HotkeyGestureKind.TripleTap => $"{TriplePrefix} {Chord}",
        _ => Chord.ToString(),
    };

    /// <summary>For people: "Hold Ctrl+Alt", "Ctrl+Alt+A", "Double-tap Ctrl".</summary>
    public string ToDisplayString() => Kind switch
    {
        HotkeyGestureKind.Hold => $"Hold {Chord}",
        HotkeyGestureKind.DoubleTap => $"Double-tap {Chord}",
        HotkeyGestureKind.TripleTap => $"Triple-tap {Chord}",
        _ => Chord.ToString(),
    };

    /// <summary>Syntax only; call <see cref="Validate"/> to know whether the engine will accept it.</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        HotkeyGestureKind? kind = null;
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space > 0)
        {
            kind = trimmed[..space].ToUpperInvariant() switch
            {
                "HOLD" => HotkeyGestureKind.Hold,
                "PRESS" => HotkeyGestureKind.Press,
                "DOUBLE" => HotkeyGestureKind.DoubleTap,
                "TRIPLE" => HotkeyGestureKind.TripleTap,
                _ => null,
            };
            // Not a prefix: the space is inside the chord ("Ctrl + Alt").
            if (kind is not null)
            {
                trimmed = trimmed[(space + 1)..];
            }
        }

        if (!HotkeyChord.TryParse(trimmed, out var chord))
        {
            return false;
        }

        gesture = new HotkeyGesture(chord, kind ?? (chord.HasTrigger ? HotkeyGestureKind.Press : HotkeyGestureKind.Hold));
        return true;
    }

    /// <summary>Null if the engine can run this gesture without breaking normal typing; otherwise why not.</summary>
    public string? Validate()
    {
        var modifierCount = System.Numerics.BitOperations.PopCount((uint)Chord.Modifiers);
        switch (Kind)
        {
            case HotkeyGestureKind.Hold when !Chord.HasTrigger:
                // A single held modifier would arm every time someone pauses on Shift or Ctrl mid-shortcut.
                return modifierCount >= 2 ? null : "A modifier-only hold needs at least two modifiers (e.g. Ctrl+Alt).";
            case HotkeyGestureKind.Hold:
            case HotkeyGestureKind.Press:
                if (!Chord.HasTrigger)
                {
                    return "A press needs a key besides the modifiers.";
                }

                return modifierCount >= 1 || VirtualKeys.IsUnmodifiedBindable(Chord.TriggerVk)
                    ? null
                    : "Add Ctrl, Alt, Shift or Win; a bare key would stop working for typing.";
            case HotkeyGestureKind.DoubleTap:
            case HotkeyGestureKind.TripleTap:
                if (Chord.HasTrigger)
                {
                    return "Taps are modifier-only (e.g. double Ctrl).";
                }

                return modifierCount >= 1 ? null : "A tap needs at least one modifier.";
            default:
                return "Unknown gesture kind.";
        }
    }
}

/// <summary>A gesture the engine listens for and the action it triggers.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, HotkeyGesture Gesture);
