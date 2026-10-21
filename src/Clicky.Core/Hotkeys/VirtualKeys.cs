using System.Globalization;

namespace Clicky.Core.Hotkeys;

/// <summary>
/// Win32 virtual-key codes the hotkey code needs, plus the names used in settings.json. Kept here
/// (rather than in the Windows project) so gesture parsing and the engine stay unit-testable.
/// </summary>
public static class VirtualKeys
{
    public const int Backspace = 0x08;
    public const int Tab = 0x09;
    public const int Enter = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int LeftWin = 0x5B;
    public const int RightWin = 0x5C;
    public const int F1 = 0x70;
    public const int F13 = 0x7C;
    public const int F24 = 0x87;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftMenu = 0xA4;
    public const int RightMenu = 0xA5;

    /// <summary>
    /// Unassigned VK used as the "menu mask" key: tapping it while Win is down stops the Start menu
    /// from opening on Win's release, and no application binds it.
    /// </summary>
    public const int MaskKey = 0xFF;

    /// <summary>Scan-code bit Windows sets on keys the layout driver synthesizes (AltGr's fake LCtrl is 0x21D).</summary>
    public const int SyntheticScanCodeFlag = 0x200;

    private static readonly Dictionary<int, string> NamesByVk = BuildNames();

    private static readonly Dictionary<string, int> VksByName = BuildLookup();

    /// <summary>The modifier a key contributes, side-agnostic; <see cref="HotkeyModifiers.None"/> for other keys.</summary>
    public static HotkeyModifiers ModifierOf(int vk) => vk switch
    {
        LeftControl or RightControl or Control => HotkeyModifiers.Ctrl,
        LeftMenu or RightMenu or Menu => HotkeyModifiers.Alt,
        LeftShift or RightShift or Shift => HotkeyModifiers.Shift,
        LeftWin or RightWin => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    public static bool IsModifier(int vk) => ModifierOf(vk) != HotkeyModifiers.None;

    /// <summary>
    /// Layout-driver fakes: AltGr sends LCtrl down/up with scan code 0x21D ahead of RAlt, and some
    /// layouts wrap numpad keys in fake Shift transitions. Neither is a key the user pressed.
    /// </summary>
    public static bool IsSyntheticModifier(in KeyEvent key) =>
        (key.ScanCode & SyntheticScanCodeFlag) != 0
        && key.Vk is LeftControl or RightControl or Control or LeftShift or RightShift or Shift;

    /// <summary>F13-F24 exist on no common keyboard, so they are safe to bind without a modifier.</summary>
    public static bool IsUnmodifiedBindable(int vk) => vk is >= F13 and <= F24;

    /// <summary>Name used in settings.json, e.g. "A", "F5", "Space"; unknown keys format as "0xNN".</summary>
    public static string Name(int vk) =>
        NamesByVk.TryGetValue(vk, out var name) ? name : string.Create(CultureInfo.InvariantCulture, $"0x{vk:X2}");

    /// <summary>Inverse of <see cref="Name"/>, case-insensitive. Also accepts a few aliases (Esc, Return, Del).</summary>
    public static bool TryParse(string text, out int vk)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (VksByName.TryGetValue(trimmed, out vk))
        {
            return true;
        }

        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(trimmed.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out vk)
            && vk is > 0 and < 0xFF
            && !IsModifier(vk))
        {
            return true;
        }

        vk = 0;
        return false;
    }

    private static Dictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [Backspace] = "Backspace",
            [Tab] = "Tab",
            [Enter] = "Enter",
            [0x13] = "Pause",
            [0x14] = "CapsLock",
            [Escape] = "Escape",
            [Space] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x5D] = "Apps",
            [0x6A] = "NumMultiply",
            [0x6B] = "NumAdd",
            [0x6D] = "NumSubtract",
            [0x6E] = "NumDecimal",
            [0x6F] = "NumDivide",
            [0x90] = "NumLock",
            [0x91] = "ScrollLock",
            [0xBA] = "Semicolon",
            [0xBB] = "Equals",
            [0xBC] = "Comma",
            [0xBD] = "Minus",
            [0xBE] = "Period",
            [0xBF] = "Slash",
            [0xC0] = "Backtick",
            [0xDB] = "LeftBracket",
            [0xDC] = "Backslash",
            [0xDD] = "RightBracket",
            [0xDE] = "Quote",
        };

        for (var c = '0'; c <= '9'; c++)
        {
            names[c] = new string(c, 1);
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            names[c] = new string(c, 1);
        }

        for (var i = 0; i <= 9; i++)
        {
            names[0x60 + i] = string.Create(CultureInfo.InvariantCulture, $"Num{i}");
        }

        for (var i = 0; i < 24; i++)
        {
            names[F1 + i] = string.Create(CultureInfo.InvariantCulture, $"F{i + 1}");
        }

        return names;
    }

    private static Dictionary<string, int> BuildLookup()
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (vk, name) in NamesByVk)
        {
            lookup[name] = vk;
        }

        lookup["Esc"] = Escape;
        lookup["Return"] = Enter;
        lookup["Del"] = 0x2E;
        lookup["Ins"] = 0x2D;
        lookup["PgUp"] = 0x21;
        lookup["PgDn"] = 0x22;
        return lookup;
    }
}
