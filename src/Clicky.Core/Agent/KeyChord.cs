using Clicky.Core.Hotkeys;

namespace Clicky.Core.Agent;

/// <summary>
/// A key or shortcut the model asked for ("Ctrl+S", "enter", "super+d", xdotool names such as
/// "Page_Down" or "KP_Enter"), as virtual keys: modifiers pressed in order, then the key.
/// </summary>
/// <param name="Modifiers">Ctrl, Alt, Shift and Win as left-hand virtual keys, in the order written.</param>
public sealed record KeyChord(IReadOnlyList<int> Modifiers, int Key)
{
    private static readonly Dictionary<string, int> s_modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = VirtualKeys.LeftControl,
        ["control"] = VirtualKeys.LeftControl,
        ["ctl"] = VirtualKeys.LeftControl,
        ["alt"] = VirtualKeys.LeftMenu,
        ["option"] = VirtualKeys.LeftMenu,
        ["shift"] = VirtualKeys.LeftShift,
        ["win"] = VirtualKeys.LeftWin,
        ["windows"] = VirtualKeys.LeftWin,
        ["super"] = VirtualKeys.LeftWin,
        ["meta"] = VirtualKeys.LeftWin,
        ["cmd"] = VirtualKeys.LeftWin,
    };

    // xdotool / browser key names the models use, on top of VirtualKeys' own names.
    private static readonly Dictionary<string, int> s_aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["backspace"] = VirtualKeys.Backspace,
        ["bksp"] = VirtualKeys.Backspace,
        ["kp_enter"] = VirtualKeys.Enter,
        ["linefeed"] = VirtualKeys.Enter,
        ["page_up"] = 0x21,
        ["prior"] = 0x21,
        ["page_down"] = 0x22,
        ["next"] = 0x22,
        ["arrowleft"] = 0x25,
        ["arrowup"] = 0x26,
        ["arrowright"] = 0x27,
        ["arrowdown"] = 0x28,
        ["print"] = 0x2C,
        ["menu"] = 0x5D,
        ["contextmenu"] = 0x5D,
        ["space"] = VirtualKeys.Space,
        ["spacebar"] = VirtualKeys.Space,
        ["plus"] = 0xBB,
        ["minus"] = 0xBD,
        ["equal"] = 0xBB,
        ["comma"] = 0xBC,
        ["period"] = 0xBE,
        ["slash"] = 0xBF,
        ["backslash"] = 0xDC,
        ["semicolon"] = 0xBA,
        ["apostrophe"] = 0xDE,
        ["grave"] = 0xC0,
        ["bracketleft"] = 0xDB,
        ["bracketright"] = 0xDD,
        ["volumeup"] = 0xAF,
        ["volumedown"] = 0xAE,
        ["volumemute"] = 0xAD,
        ["audiovolumeup"] = 0xAF,
        ["audiovolumedown"] = 0xAE,
        ["audiomute"] = 0xAD,
        ["medianext"] = 0xB0,
        ["mediaprev"] = 0xB1,
        ["mediaplaypause"] = 0xB3,
        ["audioplay"] = 0xB3,
    };

    // Unshifted punctuation on a US layout. Other layouts differ, which is why "type" is used for text.
    private static readonly Dictionary<char, int> s_punctuation = new()
    {
        [';'] = 0xBA, ['='] = 0xBB, [','] = 0xBC, ['-'] = 0xBD, ['.'] = 0xBE, ['/'] = 0xBF, ['`'] = 0xC0, ['['] = 0xDB, ['\\'] = 0xDC, [']'] = 0xDD, ['\''] = 0xDE,
    };

    /// <returns>The chord, or null with the reason.</returns>
    public static (KeyChord? Chord, string? Error) Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, "no key given");
        }

        var trimmed = text.Trim();

        // "Ctrl++" or a bare "+": the last part is the plus key.
        var parts = trimmed.EndsWith("++", StringComparison.Ordinal) || trimmed == "+"
            ? [.. trimmed[..^1].Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "plus"]
            : trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return (null, $"\"{text}\" is not a key");
        }

        var modifiers = new List<int>();
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!s_modifiers.TryGetValue(parts[i], out var modifier))
            {
                return (null, $"\"{parts[i]}\" is not a modifier (use Ctrl, Alt, Shift or Win)");
            }

            if (!modifiers.Contains(modifier))
            {
                modifiers.Add(modifier);
            }
        }

        var last = parts[^1];
        if (s_modifiers.TryGetValue(last, out var lone))
        {
            // A modifier on its own, e.g. "Win" to open Start.
            return (new KeyChord(modifiers, lone), null);
        }

        if (LookUp(last) is { } key)
        {
            return (new KeyChord(modifiers, key), null);
        }

        return (null, $"\"{last}\" is not a key Clicky knows; use type for text");
    }

    private static int? LookUp(string name)
    {
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return c;
            }

            return s_punctuation.TryGetValue(name[0], out var punctuation) ? punctuation : null;
        }

        if (s_aliases.TryGetValue(name, out var alias))
        {
            return alias;
        }

        return VirtualKeys.TryParse(name, out var vk) && !VirtualKeys.IsModifier(vk) ? vk : null;
    }
}
