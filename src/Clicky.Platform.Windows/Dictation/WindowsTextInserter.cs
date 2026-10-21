using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Clicky.Core.Dictation;
using Clicky.Platform.Windows.Input;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Dictation;

/// <summary>
/// Puts dictated text into the focused field of any app, trying in order: UI Automation's
/// ValuePattern, typing it as Unicode keystrokes, and pasting it through the clipboard (which is
/// put back afterwards).
/// </summary>
/// <remarks>
/// <para><b>ValuePattern</b> only for native Win32, WinForms and WPF edit boxes (not rich edits, not
/// passwords) whose text and caret can be read: it writes the whole value, so it must know exactly
/// where the caret is, and it would bypass the editors of Chrome, Electron and the like (React
/// state, undo), which therefore get keystrokes. The caret is put back after the inserted text.</para>
/// <para><b>Typing</b> uses KEYEVENTF_UNICODE, so the keyboard layout does not matter: "é" is
/// typed as "é" on any layout, and nothing is a shortcut.</para>
/// <para><b>Pasting</b> is for text with line breaks (typing Enter would send a chat message), apps
/// in settings.dictation.pasteApps, and when typing is refused. Ctrl+V uses the key that types "v"
/// in the target's layout; terminals get Shift+Insert.</para>
/// <para><b>Terminals</b> (Windows Terminal, the console, mintty, VS Code's terminal and others)
/// have every line break turned into a space first, so dictation can never run a command.</para>
/// <para><b>Elevated windows</b> accept neither keystrokes nor UI Automation from a normal process
/// (UIPI), so the text is left on the clipboard and the user is told to press Ctrl+V.</para>
/// <para>Runs on the thread pool (UI Automation is MTA-friendly); never call from the UI thread.</para>
/// </remarks>
public sealed class WindowsTextInserter : ITextInserter, IDisposable
{
    /// <summary>Longer text is pasted: one undo step, and no waiting for hundreds of keystrokes.</summary>
    public const int MaxTypedChars = 400;

    /// <summary>Fields longer than this are not read back or rewritten through ValuePattern.</summary>
    public const int MaxFieldChars = 20_000;

    // Keystrokes go out in bursts; some Electron apps lose characters from a single huge burst.
    private const int TypingBurst = 64;
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(6);

    // The gesture's own keys are usually still coming up when the text is ready.
    private static readonly TimeSpan ModifierWait = TimeSpan.FromMilliseconds(1500);

    // Terminal apps with their own windows. Shells (cmd, powershell, bash) are not listed: they can
    // also host ordinary GUI windows, and in a console they are caught by its window class.
    private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal.exe", "OpenConsole.exe", "conhost.exe", "mintty.exe", "alacritty.exe", "wezterm-gui.exe", "Hyper.exe",
        "Tabby.exe", "putty.exe", "kitty.exe", "ConEmu.exe", "ConEmu64.exe", "FluentTerminal.App.exe", "warp.exe",
    };

    private static readonly HashSet<string> TerminalWindowClasses = new(StringComparer.Ordinal)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "PuTTY", "VirtualConsoleClass",
    };

    // The desktop and taskbar: focus is there when nothing else has it.
    private static readonly HashSet<string> ShellWindowClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    // Editors that drop or repeat characters when Unicode keystrokes arrive faster than they draw
    // (Windows 11 Notepad turns "into Notepad" into "ppppppppppad" at anything under ~15 ms a
    // character). Text is pasted there instead.
    private static readonly HashSet<string> PasteClasses = new(StringComparer.Ordinal) { "RichEditD2DPT" };

    private static readonly HashSet<string> NativeFrameworks = new(StringComparer.OrdinalIgnoreCase) { "Win32", "WinForm", "WPF" };

    // Apps seen mangling typed text in this run; they get pasted text from then on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _typingFailed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ClipboardSwap _clipboard;
    private readonly ILogger<WindowsTextInserter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lazy<UIA3Automation> _automation = new(() => new UIA3Automation
    {
        // A hung app must not hang dictation.
        ConnectionTimeout = TimeSpan.FromSeconds(1.5),
        TransactionTimeout = TimeSpan.FromSeconds(2),
    });

    public WindowsTextInserter(Dispatcher dispatcher, ILogger<WindowsTextInserter> logger)
    {
        _logger = logger;
        _clipboard = new ClipboardSwap(dispatcher, logger);
    }

    public async Task<InsertionResult> InsertAsync(InsertionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await InsertCoreAsync(request, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Dictation: {Method} {Chars} chars into {App}{Terminal} in {Ms:0} ms{Problem}",
                result.Method,
                result.Text.Length,
                result.App,
                result.IsTerminal ? " (terminal)" : string.Empty,
                stopwatch.Elapsed.TotalMilliseconds,
                result.Problem is null ? string.Empty : $": {result.Problem}");
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What dictation would see in the focused window right now, for diagnostics and tests.</summary>
    public string DescribeTarget()
    {
        var target = Target.Inspect(_automation.Value, _logger);
        return $"{target.ProcessName} window {target.WindowClass}, element {target.ControlType} \"{target.ElementClass}\" ({target.FrameworkId}), terminal {IsTerminal(target, [])}, value {target.Value is not null}, text {target.Text is not null}";
    }

    public void Dispose()
    {
        if (_automation.IsValueCreated)
        {
            _automation.Value.Dispose();
        }

        _clipboard.Dispose();
        _gate.Dispose();
    }

    private async Task<InsertionResult> InsertCoreAsync(InsertionRequest request, CancellationToken cancellationToken)
    {
        var target = await Task.Run(() => Target.Inspect(_automation.Value, _logger), cancellationToken).ConfigureAwait(false);
        var terminal = IsTerminal(target, request.Settings.TerminalApps);
        var text = terminal ? DictationText.ForTerminal(request.Text) : request.Text;
        if (text.Length == 0)
        {
            return new InsertionResult(InsertionMethod.None, text, target.ProcessName, terminal, Problem: null);
        }

        if (target.Elevated)
        {
            await _clipboard.CopyAsync(text, cancellationToken).ConfigureAwait(false);
            return new InsertionResult(
                InsertionMethod.Copied,
                text,
                target.ProcessName,
                terminal,
                $"{target.ProcessName} runs as administrator, so Clicky can't type into it. The text is on the clipboard: press Ctrl+V.");
        }

        if (target.Window == 0 || ShellWindowClasses.Contains(target.WindowClass))
        {
            return new InsertionResult(InsertionMethod.None, text, target.ProcessName, terminal, "Nothing to type into. Click into a text field and dictate again, or copy the text from here.");
        }

        // Spacing: from the field when it can be read, else from what this dictation typed last.
        var before = target.CharBeforeCaret ?? request.PreviousChar;
        text = DictationText.Spaced(text, before);

        if (!terminal && target.CanSetValue && !text.Contains('\n', StringComparison.Ordinal) && TrySetValue(target, text) is { } set)
        {
            return set;
        }

        await WaitForModifiersUpAsync(cancellationToken).ConfigureAwait(false);

        var paste = text.Contains('\n', StringComparison.Ordinal)
            || text.Length > MaxTypedChars
            || PasteClasses.Contains(target.ElementClass)
            || Matches(request.Settings.PasteApps, target.ProcessName)
            || _typingFailed.ContainsKey(target.ProcessName);
        if (!paste && Type(text))
        {
            var probe = await ProbeAsync(target, text, cancellationToken).ConfigureAwait(false);
            if (probe is null && target.Text is not null && target.Selection is not null)
            {
                // The field can be read but the text isn't in it as typed: this app mangles keystrokes.
                _typingFailed[target.ProcessName] = true;
                _logger.LogWarning("Dictation: {App} did not take the typed text as typed; pasting there from now on", target.ProcessName);
            }

            return new InsertionResult(InsertionMethod.Typed, text, target.ProcessName, terminal, Problem: null, probe);
        }

        var outcome = await _clipboard.PasteAsync(text, () => SendPaste(target, terminal), cancellationToken).ConfigureAwait(false);
        return outcome switch
        {
            PasteOutcome.Pasted => new InsertionResult(InsertionMethod.Pasted, text, target.ProcessName, terminal, Problem: null, await ProbeAsync(target, text, cancellationToken).ConfigureAwait(false)),
            PasteOutcome.NotRead => new InsertionResult(InsertionMethod.Copied, text, target.ProcessName, terminal, $"{target.ProcessName} didn't take the text. It's on the clipboard: press Ctrl+V."),
            _ => new InsertionResult(InsertionMethod.None, text, target.ProcessName, terminal, "The clipboard is busy, so Clicky couldn't paste. Copy the text from here."),
        };
    }

    private static bool IsTerminal(Target target, IReadOnlyList<string> extraApps) =>
        TerminalProcesses.Contains(target.ProcessName)
        || TerminalWindowClasses.Contains(target.WindowClass)
        || Matches(extraApps, target.ProcessName)
        // xterm.js (VS Code, Hyper, Tabby and web terminals) types through a hidden textarea with this class.
        || target.ElementClass.Contains("xterm", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(IReadOnlyList<string> apps, string processName) =>
        apps.Any(a => string.Equals(a, processName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a + ".exe", processName, StringComparison.OrdinalIgnoreCase));

    private InsertionResult? TrySetValue(Target target, string text)
    {
        try
        {
            var value = target.Value!;
            var (start, end) = target.Selection!.Value;
            var expected = string.Concat(value.AsSpan(0, start), text, value.AsSpan(end));
            target.Element!.Patterns.Value.Pattern.SetValue(expected);
            var now = target.Element.Patterns.Value.Pattern.Value.ValueOrDefault;
            if (!string.Equals(now, expected, StringComparison.Ordinal))
            {
                if (string.Equals(now, value, StringComparison.Ordinal))
                {
                    // Ignored: fall through to typing.
                    _logger.LogDebug("Dictation: {App} ignored SetValue", target.ProcessName);
                    return null;
                }

                // Something changed, but not as planned; typing as well would insert it twice.
                _logger.LogWarning("Dictation: {App} changed the value differently than asked", target.ProcessName);
                return new InsertionResult(InsertionMethod.ValuePattern, text, target.ProcessName, false, Problem: null);
            }

            PlaceCaret(target, start + text.Length);
            return new InsertionResult(InsertionMethod.ValuePattern, text, target.ProcessName, false, Problem: null, new UiaProbe(target.Element, expected, start, text.Length));
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug(ex, "Dictation: SetValue failed in {App}; typing instead", target.ProcessName);
            return null;
        }
    }

    private void PlaceCaret(Target target, int offset)
    {
        if (target.EditWindow != 0)
        {
            SendMessageTimeout(target.EditWindow, EmSetSel, (nuint)offset, offset, SmtoAbortIfHung, 500, out _);
            return;
        }

        try
        {
            var range = target.Element!.Patterns.Text.Pattern.DocumentRange.Clone();
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, offset);
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            range.Select();
        }
        catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Dictation: could not put the caret back in {App}", target.ProcessName);
        }
    }

    private static async Task WaitForModifiersUpAsync(CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (AnyModifierDown() && waited.Elapsed < ModifierWait)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool AnyModifierDown() =>
        IsDown(VkShift) || IsDown(VkControl) || IsDown(VkMenu) || IsDown(VkLWin) || IsDown(VkRWin);

    private static bool IsDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Types <paramref name="text"/> as Unicode keystrokes. False if Windows refused the input.</summary>
    private static unsafe bool Type(string text)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var count = Math.Min(TypingBurst, text.Length - offset);

            // A surrogate pair must not be split across bursts.
            if (offset + count < text.Length && char.IsHighSurrogate(text[offset + count - 1]))
            {
                count++;
            }

            var inputs = new NativeInput[count * 2];
            for (var i = 0; i < count; i++)
            {
                var c = text[offset + i];
                inputs[2 * i] = Key(0, c, KeyeventfUnicode);
                inputs[(2 * i) + 1] = Key(0, c, KeyeventfUnicode | KeyeventfKeyUp);
            }

            fixed (NativeInput* pointer = inputs)
            {
                if (SendInput((uint)inputs.Length, pointer, sizeof(NativeInput)) != inputs.Length)
                {
                    return false;
                }
            }

            offset += count;
            if (offset < text.Length)
            {
                Pause(TypingPause);
            }
        }

        return true;
    }

    private static void Pause(TimeSpan pause)
    {
        // Thread.Sleep rounds up to the 15.6 ms system tick; a short spin keeps the pacing tight.
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < pause)
        {
            Thread.Yield();
        }
    }

    private static unsafe void SendPaste(Target target, bool terminal)
    {
        NativeInput[] inputs;
        if (terminal)
        {
            // Shift+Insert pastes in every terminal; Ctrl+V does not (mintty, older consoles).
            inputs = [Key(VkShift, '\0', 0), Key(VkInsert, '\0', KeyeventfExtendedKey), Key(VkInsert, '\0', KeyeventfExtendedKey | KeyeventfKeyUp), Key(VkShift, '\0', KeyeventfKeyUp)];
        }
        else
        {
            var v = VkForV(target);
            inputs = [Key(VkControl, '\0', 0), Key(v, '\0', 0), Key(v, '\0', KeyeventfKeyUp), Key(VkControl, '\0', KeyeventfKeyUp)];
        }

        fixed (NativeInput* pointer = inputs)
        {
            SendInput((uint)inputs.Length, pointer, sizeof(NativeInput));
        }
    }

    /// <summary>The virtual key that types "v" in the target's keyboard layout (Ctrl+V is that key).</summary>
    private static ushort VkForV(Target target)
    {
        var thread = GetWindowThreadProcessId(target.Window, out _);
        var scan = VkKeyScanEx('v', GetKeyboardLayout(thread));
        return scan == -1 ? VkV : (ushort)(scan & 0xFF);
    }

    private static NativeInput Key(ushort vk, char scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion { Keyboard = new KeybdInput { Vk = vk, Scan = scan, Flags = flags } },
    };

    /// <summary>Reads the field back after typing or pasting so corrections can be learned, if it can be read.</summary>
    private async Task<IInsertedTextProbe?> ProbeAsync(Target target, string text, CancellationToken cancellationToken)
    {
        if (target.Element is null || target.Selection is not { } selection || (target.Text is null && target.Value is null))
        {
            return null;
        }

        // The app handles the keystrokes or the paste after SendInput returns.
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        var now = await Task.Run(() => UiaProbe.Read(target.Element), cancellationToken).ConfigureAwait(false);
        if (now is null)
        {
            return null;
        }

        // Apps store line breaks their own way (RichEdit uses \r, Win32 edits \r\n).
        foreach (var form in new[] { text, text.Replace("\n", "\r", StringComparison.Ordinal), text.Replace("\n", "\r\n", StringComparison.Ordinal) }.Distinct())
        {
            var start = selection.Start;
            if (start + form.Length <= now.Length && string.CompareOrdinal(now, start, form, 0, form.Length) == 0)
            {
                return new UiaProbe(target.Element, now, start, form.Length);
            }

            // The field may have scrolled its contents or the app trimmed a space; look nearby.
            var found = now.IndexOf(form, Math.Max(0, start - 64), StringComparison.Ordinal);
            if (found >= 0)
            {
                return new UiaProbe(target.Element, now, found, form.Length);
            }
        }

        _logger.LogDebug("Dictation: could not find the inserted text in {App} to watch for corrections", target.ProcessName);
        return null;
    }

    /// <summary>The focused window and element, read once per insertion.</summary>
    private sealed record Target(
        nint Window,
        string WindowClass,
        string ProcessName,
        bool Elevated,
        AutomationElement? Element,
        string ElementClass,
        string FrameworkId,
        ControlType ControlType,
        string? Value,
        string? Text,
        (int Start, int End)? Selection,
        char? CharBeforeCaret,
        bool CanSetValue,
        nint EditWindow = 0)
    {
        public static Target Inspect(UIA3Automation automation, ILogger logger)
        {
            var window = GetForegroundWindow();
            var elevated = ProcessElevation.IsWindowElevated(window, out var processName) && !ProcessElevation.IsCurrentProcessElevated;
            var windowClass = ClassOf(window);
            if (window == 0 || elevated)
            {
                return new Target(window, windowClass, processName, elevated, null, string.Empty, string.Empty, ControlType.Unknown, null, null, null, null, false);
            }

            try
            {
                var element = automation.FocusedElement();
                var properties = element.Properties;
                var elementClass = properties.ClassName.ValueOrDefault ?? string.Empty;
                var framework = properties.FrameworkId.ValueOrDefault ?? string.Empty;
                var controlType = properties.ControlType.ValueOrDefault;
                var password = properties.IsPassword.ValueOrDefault;

                string? value = null;
                var readOnly = true;
                if (!password && element.Patterns.Value.TryGetPattern(out var valuePattern))
                {
                    value = valuePattern.Value.ValueOrDefault;
                    readOnly = valuePattern.IsReadOnly.ValueOrDefault;
                    if (value is { Length: > MaxFieldChars })
                    {
                        value = null;
                    }
                }

                string? text = null;
                (int, int)? selection = null;
                char? before = null;
                if (!password && element.Patterns.Text.TryGetPattern(out var textPattern))
                {
                    var document = textPattern.DocumentRange;
                    text = document.GetText(MaxFieldChars + 1);
                    if (text.Length > MaxFieldChars)
                    {
                        text = null;
                    }

                    var ranges = textPattern.GetSelection();
                    if (text is not null && ranges.Length == 1)
                    {
                        var head = document.Clone();
                        head.MoveEndpointByRange(TextPatternRangeEndpoint.End, ranges[0], TextPatternRangeEndpoint.Start);
                        var start = head.GetText(-1).Length;
                        var length = ranges[0].GetText(-1).Length;
                        if (start + length <= text.Length)
                        {
                            selection = (start, start + length);
                            before = start > 0 ? text[start - 1] : null;
                        }
                    }
                }

                // A classic Edit control (Win32, WinForms) often has no TextPattern; it reports
                // its selection itself, in the same units as its value.
                var editWindow = elementClass.Contains("EDIT", StringComparison.OrdinalIgnoreCase) ? properties.NativeWindowHandle.ValueOrDefault : 0;
                if (selection is null && value is not null && editWindow != 0 && EditSelection(editWindow) is { } native && native.End <= value.Length)
                {
                    selection = native;
                    before = native.Start > 0 ? value[native.Start - 1] : null;
                }

                // An empty field has its caret at the start whatever the selection API says.
                if (selection is null && value is { Length: 0 })
                {
                    selection = (0, 0);
                }

                var canSet = !readOnly
                    && value is not null
                    && NativeFrameworks.Contains(framework)
                    && controlType == ControlType.Edit
                    && !elementClass.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)
                    && !value.Contains('\r', StringComparison.Ordinal)
                    && selection is { } s && s.Item2 <= value.Length
                    && (text is null || string.Equals(text, value, StringComparison.Ordinal));

                return new Target(window, windowClass, processName, false, element, elementClass, framework, controlType, value, text, selection, before, canSet, editWindow);
            }
            catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException or ArgumentException)
            {
                logger.LogDebug(ex, "Dictation: could not inspect the focused element in {App}", processName);
                return new Target(window, windowClass, processName, false, null, string.Empty, string.Empty, ControlType.Unknown, null, null, null, null, false);
            }
        }

        private static (int Start, int End)? EditSelection(nint edit)
        {
            if (SendMessageTimeout(edit, EmGetSel, 0, 0, SmtoAbortIfHung, 500, out var packed) == 0)
            {
                return null;
            }

            // LOWORD start, HIWORD end: exact up to 65535, and fields here are at most MaxFieldChars.
            var start = (int)(packed & 0xFFFF);
            var end = (int)((packed >> 16) & 0xFFFF);
            return start <= end ? (start, end) : null;
        }

        private static unsafe string ClassOf(nint hwnd)
        {
            if (hwnd == 0)
            {
                return string.Empty;
            }

            const int capacity = 256;
            var buffer = stackalloc char[capacity];
            var length = GetClassName(hwnd, buffer, capacity);
            return length > 0 ? new string(buffer, 0, length) : string.Empty;
        }
    }

    private sealed class UiaProbe(AutomationElement element, string textAfterInsert, int insertStart, int insertLength) : IInsertedTextProbe
    {
        public string TextAfterInsert => textAfterInsert;

        public int InsertStart => insertStart;

        public int InsertLength => insertLength;

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.Run(() => Read(element), cancellationToken);

        public static string? Read(AutomationElement element)
        {
            try
            {
                if (element.Patterns.Text.TryGetPattern(out var text))
                {
                    var all = text.DocumentRange.GetText(MaxFieldChars + 1);
                    return all.Length > MaxFieldChars ? null : all;
                }

                return element.Patterns.Value.TryGetPattern(out var value) ? value.Value.ValueOrDefault : null;
            }
            catch (Exception ex) when (ex is COMException or TimeoutException or InvalidOperationException)
            {
                return null;
            }
        }
    }
}
