using System.Runtime.InteropServices;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Geometry;
using Clicky.Core.Hotkeys;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Agent;

/// <summary>
/// SendInput: keys and Unicode text (which never move the pointer), and the mouse fallback, which
/// moves the real pointer to the spot and puts it back afterwards.
/// </summary>
/// <remarks>
/// Gotchas: injected input is blocked by UIPI for elevated windows (SendInput still returns success
/// for some of it), and goes to whatever has focus, so keys the user is still holding (the Talk
/// hotkey's Ctrl+Alt) would turn text into shortcuts; injection waits for them to be released.
/// Clicky's own keyboard hook ignores injected keys, so this never triggers a hotkey.
/// </remarks>
internal static class InputInjector
{
    private const int TextChunk = 64;

    // dwExtraInfo on everything injected here, so Clicky's own hooks (the walkthrough click watcher)
    // can tell agent input from the user's. Same value as the keyboard hook's synthetic keys.
    private const nuint InjectionSignature = 0x434C_4B59; // "CLKY"

    private static readonly int[] s_extendedKeys =
    [
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90,
        VirtualKeys.RightControl, VirtualKeys.RightMenu, 0xAD, 0xAE, 0xAF, 0xB0, 0xB1, 0xB2, 0xB3,
    ];

    /// <summary>Waits (up to <paramref name="timeout"/>) until no modifier is physically held.</summary>
    /// <returns>False if the user kept holding one.</returns>
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (AnyModifierDown())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public static unsafe bool Chord(KeyChord chord, int repeat)
    {
        var inputs = new List<NativeInput>();
        for (var i = 0; i < Math.Clamp(repeat, 1, 100); i++)
        {
            foreach (var modifier in chord.Modifiers)
            {
                inputs.Add(Key(modifier, up: false));
            }

            inputs.Add(Key(chord.Key, up: false));
            inputs.Add(Key(chord.Key, up: true));
            for (var m = chord.Modifiers.Count - 1; m >= 0; m--)
            {
                inputs.Add(Key(chord.Modifiers[m], up: true));
            }
        }

        return Send(inputs);
    }

    /// <summary>Types text as Unicode key events (layout-independent); new lines are Enter, tabs are Tab.</summary>
    public static bool Text(string text)
    {
        var inputs = new List<NativeInput>(TextChunk * 2);
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            switch (c)
            {
                case '\n':
                    inputs.Add(Key(VirtualKeys.Enter, up: false));
                    inputs.Add(Key(VirtualKeys.Enter, up: true));
                    break;
                case '\t':
                    inputs.Add(Key(VirtualKeys.Tab, up: false));
                    inputs.Add(Key(VirtualKeys.Tab, up: true));
                    break;
                default:
                    // Surrogate pairs go as two code units, which is what KEYEVENTF_UNICODE expects.
                    inputs.Add(Unicode(c, up: false));
                    inputs.Add(Unicode(c, up: true));
                    break;
            }

            if (inputs.Count >= TextChunk * 2)
            {
                if (!Send(inputs))
                {
                    return false;
                }

                inputs.Clear();
            }
        }

        return inputs.Count == 0 || Send(inputs);
    }

    /// <summary>Moves the pointer to <paramref name="at"/>, clicks, and moves it back.</summary>
    public static async Task<bool> ClickAsync(PixelPoint at, MouseButton button, int count, CancellationToken cancellationToken)
    {
        var (down, up) = button switch
        {
            MouseButton.Right => (MouseeventfRightDown, MouseeventfRightUp),
            MouseButton.Middle => (MouseeventfMiddleDown, MouseeventfMiddleUp),
            _ => (MouseeventfLeftDown, MouseeventfLeftUp),
        };
        return await WithPointerAtAsync(at, () =>
        {
            var inputs = new List<NativeInput>();
            for (var i = 0; i < Math.Clamp(count, 1, 3); i++)
            {
                inputs.Add(Mouse(down, 0));
                inputs.Add(Mouse(up, 0));
            }

            return Send(inputs);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="notches">Positive scrolls down (or right when <paramref name="horizontal"/>).</param>
    public static Task<bool> ScrollAsync(PixelPoint at, int notches, bool horizontal, CancellationToken cancellationToken) =>
        WithPointerAtAsync(at, () =>
        {
            // Wheel: positive is away from the user (up); horizontal wheel: positive is right.
            var delta = horizontal ? notches * WheelDelta : -notches * WheelDelta;
            return Send([Mouse(horizontal ? MouseeventfHWheel : MouseeventfWheel, delta)]);
        }, cancellationToken);

    private static async Task<bool> WithPointerAtAsync(PixelPoint at, Func<bool> act, CancellationToken cancellationToken)
    {
        var restore = Input.CursorReader.TryGet(out var original, out _);
        if (!SetCursorPos(at.X, at.Y))
        {
            return false;
        }

        try
        {
            // Let the target see the move (hover states, menus) before the button goes down.
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            return act();
        }
        finally
        {
            if (restore)
            {
                await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);
                SetCursorPos(original.X, original.Y);
            }
        }
    }

    private static bool AnyModifierDown()
    {
        foreach (var vk in new[] { VirtualKeys.Control, VirtualKeys.Menu, VirtualKeys.Shift, VirtualKeys.LeftWin, VirtualKeys.RightWin })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static NativeInput Key(int vk, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeybdInput
            {
                Vk = (ushort)vk,
                Flags = (up ? KeyeventfKeyUp : 0) | (s_extendedKeys.Contains(vk) ? KeyeventfExtendedKey : 0),
                ExtraInfo = InjectionSignature,
            },
        },
    };

    private static NativeInput Unicode(char c, bool up) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeybdInput { Scan = c, Flags = KeyeventfUnicode | (up ? KeyeventfKeyUp : 0), ExtraInfo = InjectionSignature } },
    };

    private static NativeInput Mouse(uint flags, int data) => new()
    {
        Type = InputMouse,
        Union = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = unchecked((uint)data), ExtraInfo = InjectionSignature } },
    };

    private static unsafe bool Send(List<NativeInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return true;
        }

        var array = inputs.ToArray();
        fixed (NativeInput* pointer = array)
        {
            return SendInput((uint)array.Length, pointer, Marshal.SizeOf<NativeInput>()) == (uint)array.Length;
        }
    }
}
