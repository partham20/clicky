using System.Diagnostics;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Agent.Browser;
using Clicky.Core.Connectors;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Platform.Windows.Input;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;
using static Clicky.Platform.Windows.NativeMethods;

namespace Clicky.Platform.Windows.Agent;

/// <summary>
/// Computer control on Windows, UI Automation first: a click becomes Invoke, Toggle, Select or
/// Expand on the control under the point, so the user's pointer stays where it is. Inside Clicky's
/// own browser, clicks, typing and scrolling go over CDP. Keys and text are SendInput keyboard
/// events (they never move the pointer). The real mouse is used only when the agent says the user
/// allowed it.
/// </summary>
/// <remarks>
/// Gotchas: UIPI stops a normal process from automating or injecting into an administrator window
/// (SendInput even reports success), so those are refused up front. UWP apps live in
/// ApplicationFrameHost's frame, so the app is named from the UIA element's process, not the
/// window's. Coordinates come in as screenshot pixels and are mapped with the screenshot's own
/// geometry, which already carries the monitor offset and DPI.
/// </remarks>
public sealed class WindowsComputerControl : IComputerControl, IDisposable
{
    private static readonly TimeSpan s_inspectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_actTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan s_modifierWait = TimeSpan.FromSeconds(3);

    // Words a model puts after a control's name ("Save button") that aren't part of the name.
    private static readonly string[] s_roleWords =
        ["button", "link", "tab", "field", "box", "text box", "menu", "menu item", "icon", "checkbox", "check box", "option", "item", "toggle", "switch"];

    private readonly ChromeHost _browser;
    private readonly IBrowserLauncher _shell;
    private readonly ISettingsStore _settings;
    private readonly ILogger<WindowsComputerControl> _logger;
    private readonly UiaRunner _uia = new();

    public WindowsComputerControl(ChromeHost browser, IBrowserLauncher shell, ISettingsStore settings, ILogger<WindowsComputerControl> logger)
    {
        _browser = browser;
        _shell = shell;
        _settings = settings;
        _logger = logger;
    }

    public async Task<ControlTarget> InspectAsync(ClickyAction action, ScreenshotGeometry? geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            switch (action)
            {
                case OpenUrlAction open:
                    return new ControlTarget(Uri.TryCreate(open.Url, UriKind.Absolute, out var uri) ? uri.Host : open.Url, "web page", "Clicky's browser");

                case ClickAction or ScrollAction when geometry is null:
                    return ControlTarget.Unknown;

                case ClickAction click:
                {
                    var point = ToScreen(click.X, click.Y, geometry!);
                    var root = RootAt(point);
                    if (await BrowserPointAsync(point, cancellationToken).ConfigureAwait(false) is { } page)
                    {
                        var (name, role) = await page.Browser.DescribeAtAsync(page.Session, page.X, page.Y, cancellationToken).ConfigureAwait(false);
                        return new ControlTarget(name, role, "Clicky's browser", ProcessName(_browser.BrowserProcessId), IsBlocked(root));
                    }

                    return await _uia.RunAsync(a => Describe(ResolveClickTarget(a, point, click.Target), root), s_inspectTimeout, cancellationToken).ConfigureAwait(false);
                }

                case ScrollAction scroll:
                {
                    var root = RootAt(ToScreen(scroll.X, scroll.Y, geometry!));
                    return Describe(null, root);
                }

                case TypeAction or KeyAction:
                {
                    var root = GetForegroundWindow();
                    return await _uia.RunAsync(a => Describe(a.FocusedElement(), root), s_inspectTimeout, cancellationToken).ConfigureAwait(false);
                }

                default:
                    return ControlTarget.Unknown;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An app that doesn't answer UIA still gets asked about; the gate just can't name the control.
            _logger.LogDebug(ex, "Agent: could not inspect the target of {Action}", action.Name);
            return ControlTarget.Unknown;
        }
    }

    public async Task<ControlOutcome> ExecuteAsync(ClickyAction action, ScreenshotGeometry? geometry, ControlOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return action switch
            {
                OpenUrlAction open => await OpenUrlAsync(open.Url, cancellationToken).ConfigureAwait(false),
                ClickAction or ScrollAction when geometry is null => ControlOutcome.Failed("There was no screenshot to aim at."),
                ClickAction click => await ClickAsync(click, geometry!, options, cancellationToken).ConfigureAwait(false),
                ScrollAction scroll => await ScrollAsync(scroll, geometry!, options, cancellationToken).ConfigureAwait(false),
                TypeAction type => await TypeAsync(type.Text, cancellationToken).ConfigureAwait(false),
                KeyAction key => await KeyAsync(key, cancellationToken).ConfigureAwait(false),
                _ => ControlOutcome.Failed($"{action.Name} is not something Clicky does on the computer."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Agent: {Action} failed", action.Name);
            return ControlOutcome.Failed($"Windows reported an error: {ex.Message}");
        }
    }

    public void Dispose() => _uia.Dispose();

    private async Task<ControlOutcome> ClickAsync(ClickAction click, ScreenshotGeometry geometry, ControlOptions options, CancellationToken cancellationToken)
    {
        var point = ToScreen(click.X, click.Y, geometry);
        var root = RootAt(point);
        if (IsBlocked(root))
        {
            return Blocked(root);
        }

        if (await BrowserPointAsync(point, cancellationToken).ConfigureAwait(false) is { } page)
        {
            await page.Browser.ClickAsync(page.Session, page.X, page.Y, click.Button, click.Count, cancellationToken).ConfigureAwait(false);
            return ControlOutcome.Done($"Clicked in Clicky's browser{Named(click.Target)}.");
        }

        if (click.Button == MouseButton.Left && click.Count <= 2)
        {
            var stage = new UiaStage();
            try
            {
                var done = await _uia.RunAsync(a => Activate(ResolveClickTarget(a, point, click.Target), click.Count, stage), s_actTimeout, cancellationToken).ConfigureAwait(false);
                if (done is not null)
                {
                    return ControlOutcome.Done(done);
                }
            }
            catch (TimeoutException) when (stage.Acting is { } acting)
            {
                // Typical of a button that opened a modal dialog: the pattern call waits for it to close.
                return ControlOutcome.Done($"{acting}; the app is still busy, maybe it opened a dialog.");
            }
            catch (TimeoutException)
            {
                return ControlOutcome.Failed("The app isn't answering (UI Automation timed out); it may be busy or hung.");
            }
        }

        if (!options.AllowMouse)
        {
            return new ControlOutcome(ControlStatus.NeedsMouse, $"No way to press{Named(click.Target)} without moving the mouse.");
        }

        if (!await InputInjector.WaitForModifiersReleasedAsync(s_modifierWait, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Failed("The user is holding Ctrl, Alt, Shift or Windows; clicking now would be a different click.");
        }

        return await InputInjector.ClickAsync(point, click.Button, click.Count, cancellationToken).ConfigureAwait(false)
            ? ControlOutcome.Done($"Clicked{Named(click.Target)} with the mouse.")
            : ControlOutcome.Failed("Windows blocked the mouse click.");
    }

    private async Task<ControlOutcome> ScrollAsync(ScrollAction scroll, ScreenshotGeometry geometry, ControlOptions options, CancellationToken cancellationToken)
    {
        var point = ToScreen(scroll.X, scroll.Y, geometry);
        var root = RootAt(point);
        if (IsBlocked(root))
        {
            return Blocked(root);
        }

        var horizontal = scroll.Direction is ScrollDirection.Left or ScrollDirection.Right;
        var forward = scroll.Direction is ScrollDirection.Down or ScrollDirection.Right;
        var notches = Math.Clamp(scroll.Amount, 1, 20);
        if (await BrowserPointAsync(point, cancellationToken).ConfigureAwait(false) is { } page)
        {
            // Chrome scrolls about 100 CSS pixels per wheel notch.
            var delta = (forward ? 1 : -1) * notches * 100.0;
            await page.Browser.ScrollAsync(page.Session, page.X, page.Y, horizontal ? delta : 0, horizontal ? 0 : delta, cancellationToken).ConfigureAwait(false);
            return ControlOutcome.Done($"Scrolled {scroll.Direction.ToString().ToLowerInvariant()} in Clicky's browser.");
        }

        try
        {
            if (await _uia.RunAsync(a => ScrollWithPattern(a, point, horizontal, forward, notches), s_actTimeout, cancellationToken).ConfigureAwait(false))
            {
                return ControlOutcome.Done($"Scrolled {scroll.Direction.ToString().ToLowerInvariant()}.");
            }
        }
        catch (TimeoutException)
        {
            return ControlOutcome.Failed("The app isn't answering (UI Automation timed out).");
        }

        if (!options.AllowMouse)
        {
            return new ControlOutcome(ControlStatus.NeedsMouse, "That area can't be scrolled without moving the mouse.");
        }

        return await InputInjector.ScrollAsync(point, forward ? notches : -notches, horizontal, cancellationToken).ConfigureAwait(false)
            ? ControlOutcome.Done($"Scrolled {scroll.Direction.ToString().ToLowerInvariant()} with the mouse wheel.")
            : ControlOutcome.Failed("Windows blocked the scroll.");
    }

    private async Task<ControlOutcome> TypeAsync(string text, CancellationToken cancellationToken)
    {
        var foreground = GetForegroundWindow();
        if (Unreachable(foreground) is { } refused)
        {
            return refused;
        }

        if (_browser.Connected is { } browser && ProcessIdOf(foreground) == _browser.BrowserProcessId
            && await browser.VisiblePageAsync(cancellationToken).ConfigureAwait(false) is { } session)
        {
            await browser.TypeAsync(session, text, cancellationToken).ConfigureAwait(false);
            return ControlOutcome.Done($"Typed {text.Length} characters in Clicky's browser.");
        }

        if (!await InputInjector.WaitForModifiersReleasedAsync(s_modifierWait, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Failed("The user is holding Ctrl, Alt, Shift or Windows, which would turn the text into shortcuts.");
        }

        return InputInjector.Text(text)
            ? ControlOutcome.Done($"Typed {text.Length} characters into {AppName(foreground)}.")
            : ControlOutcome.Failed("Windows blocked the typing.");
    }

    private static async Task<ControlOutcome> KeyAsync(KeyAction key, CancellationToken cancellationToken)
    {
        var (chord, error) = KeyChord.Parse(key.Keys);
        if (chord is null)
        {
            return ControlOutcome.Failed(error ?? $"\"{key.Keys}\" is not a key Clicky knows.");
        }

        var foreground = GetForegroundWindow();
        if (Unreachable(foreground) is { } refused)
        {
            return refused;
        }

        if (!await InputInjector.WaitForModifiersReleasedAsync(s_modifierWait, cancellationToken).ConfigureAwait(false))
        {
            return ControlOutcome.Failed("The user is holding Ctrl, Alt, Shift or Windows, which would change the shortcut.");
        }

        var times = Math.Clamp(key.Repeat, 1, 100);
        return InputInjector.Chord(chord, times)
            ? ControlOutcome.Done($"Pressed {key.Keys}{(times > 1 ? $" {times} times" : string.Empty)} in {AppName(foreground)}.")
            : ControlOutcome.Failed("Windows blocked the key press.");
    }

    private async Task<ControlOutcome> OpenUrlAsync(string url, CancellationToken cancellationToken)
    {
        if (!ActionParser.IsWebAddress(url))
        {
            return ControlOutcome.Failed("Only http and https addresses are opened.");
        }

        if (_settings.Current.Agent.UseBrowserProfile)
        {
            try
            {
                if (await _browser.EnsureAsync(cancellationToken).ConfigureAwait(false) is { } browser)
                {
                    ForegroundRights.GrantToAnyProcess();
                    var title = await browser.OpenAsync(url, cancellationToken).ConfigureAwait(false);
                    return ControlOutcome.Done(title is null
                        ? $"Opened {url} in Clicky's browser; it is still loading."
                        : $"Opened {url} in Clicky's browser: \"{title}\".");
                }
            }
            catch (Exception ex) when (ex is CdpException or IOException or System.Net.WebSockets.WebSocketException or TimeoutException)
            {
                _logger.LogWarning(ex, "Agent: Clicky's browser could not open the page; using the default browser");
            }
        }

        _shell.Open(new Uri(url));
        return ControlOutcome.Done($"Opened {url} in the default browser (not Clicky's own, so only the screen shows what happened).");
    }

    // ---- UI Automation (these run on UiaRunner threads) ----

    /// <summary>The element to click: the one under the point, or a nearby one with the name the model gave.</summary>
    private static AutomationElement? ResolveClickTarget(UIA3Automation automation, PixelPoint point, string? target)
    {
        var hit = automation.FromPoint(new System.Drawing.Point(point.X, point.Y));
        var name = ControlName(target);
        if (hit is null || name is null || NameMatches(hit, name))
        {
            return hit;
        }

        // The hit is often the text or icon inside the control.
        var current = hit.Parent;
        for (var depth = 0; current is not null && depth < 3; depth++, current = current.Parent)
        {
            if (NameMatches(current, name))
            {
                return current;
            }
        }

        // Screenshot coordinates can be off by a few pixels; look for a control with that name close by.
        // Search the nearest window (a dialog, or the app's main window), never the whole desktop.
        var window = hit;
        for (var depth = 0; depth < 40 && window.Properties.ControlType.ValueOrDefault != ControlType.Window; depth++)
        {
            if (window.Parent is not { Parent: not null } parent)
            {
                break;
            }

            window = parent;
        }

        AutomationElement? best = null;
        var bestDistance = 80.0;
        foreach (var candidate in window.FindAllDescendants(cf => cf.ByName(name, PropertyConditionFlags.IgnoreCase)))
        {
            var bounds = candidate.Properties.BoundingRectangle.ValueOrDefault;
            if (bounds.IsEmpty || candidate.Properties.IsOffscreen.ValueOrDefault)
            {
                continue;
            }

            var dx = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
            var dy = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
            var distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best ?? hit;
    }

    /// <summary>Presses the control with a UIA pattern, walking up from text inside it.</summary>
    /// <returns>What was done, or null if the control has no pattern for it (needs the mouse).</returns>
    private static string? Activate(AutomationElement? element, int count, UiaStage stage)
    {
        var current = element;
        for (var depth = 0; current is not null && depth < 3; depth++, current = current.Parent)
        {
            var type = current.Properties.ControlType.ValueOrDefault;
            var label = Label(current);
            var patterns = current.Patterns;
            if (!current.Properties.IsEnabled.ValueOrDefault)
            {
                return $"{label} is disabled, so nothing happened";
            }

            if (count == 2)
            {
                // Double-click opens list and tree items (Explorer files); that is their default action.
                if (type is ControlType.ListItem or ControlType.TreeItem or ControlType.DataItem
                    && patterns.LegacyIAccessible.PatternOrDefault is { } item)
                {
                    stage.Acting = $"Opened {label}";
                    item.DoDefaultAction();
                    return $"Opened {label} (UI Automation).";
                }

                continue;
            }

            if (patterns.Invoke.PatternOrDefault is { } invoke)
            {
                stage.Acting = $"Pressed {label}";
                invoke.Invoke();
                return $"Pressed {label} (UI Automation).";
            }

            if (patterns.Toggle.PatternOrDefault is { } toggle)
            {
                stage.Acting = $"Toggled {label}";
                toggle.Toggle();
                return $"Toggled {label}; it is now {toggle.ToggleState.ValueOrDefault.ToString().ToLowerInvariant()} (UI Automation).";
            }

            if (patterns.SelectionItem.PatternOrDefault is { } selection)
            {
                stage.Acting = $"Selected {label}";
                selection.Select();
                return $"Selected {label} (UI Automation).";
            }

            if (patterns.ExpandCollapse.PatternOrDefault is { } expand)
            {
                var collapsed = expand.ExpandCollapseState.ValueOrDefault == ExpandCollapseState.Collapsed;
                stage.Acting = collapsed ? $"Opened {label}" : $"Closed {label}";
                if (collapsed)
                {
                    expand.Expand();
                }
                else
                {
                    expand.Collapse();
                }

                return $"{stage.Acting} (UI Automation).";
            }

            if (type is ControlType.Edit or ControlType.Document or ControlType.ComboBox && current.Properties.IsKeyboardFocusable.ValueOrDefault)
            {
                stage.Acting = $"Put the cursor in {label}";
                current.Focus();
                return $"Put the cursor in {label} (UI Automation); type next.";
            }

            if (patterns.LegacyIAccessible.PatternOrDefault is { } legacy && !string.IsNullOrEmpty(legacy.DefaultAction.ValueOrDefault))
            {
                stage.Acting = $"Did \"{legacy.DefaultAction.ValueOrDefault}\" on {label}";
                legacy.DoDefaultAction();
                return $"{stage.Acting} (UI Automation).";
            }
        }

        return null;
    }

    private static bool ScrollWithPattern(UIA3Automation automation, PixelPoint point, bool horizontal, bool forward, int notches)
    {
        var current = automation.FromPoint(new System.Drawing.Point(point.X, point.Y));
        for (var depth = 0; current is not null && depth < 12; depth++, current = current.Parent)
        {
            if (current.Patterns.Scroll.PatternOrDefault is not { } scroll)
            {
                continue;
            }

            var scrollable = horizontal ? scroll.HorizontallyScrollable.ValueOrDefault : scroll.VerticallyScrollable.ValueOrDefault;
            if (!scrollable)
            {
                continue;
            }

            // A wheel notch is three lines; SmallIncrement is one.
            var amount = forward ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement;
            for (var i = 0; i < Math.Min(notches * 3, 60); i++)
            {
                scroll.Scroll(horizontal ? amount : ScrollAmount.NoAmount, horizontal ? ScrollAmount.NoAmount : amount);
            }

            return true;
        }

        return false;
    }

    private static ControlTarget Describe(AutomationElement? element, nint root)
    {
        var pid = element?.Properties.ProcessId.ValueOrDefault is > 0 and var elementPid ? (uint)elementPid : ProcessIdOf(root);
        var role = element?.Properties.ControlType.ValueOrDefault is { } type and not ControlType.Unknown ? type.ToString().ToLowerInvariant() : null;
        var name = element?.Properties.Name.ValueOrDefault;
        return new ControlTarget(string.IsNullOrWhiteSpace(name) ? null : name.Trim(), role, AppDescription(pid), ProcessName(pid), IsBlocked(root));
    }

    // ---- Windows and processes ----

    private async Task<BrowserPoint?> BrowserPointAsync(PixelPoint point, CancellationToken cancellationToken)
    {
        if (_browser.Connected is not { } browser)
        {
            return null;
        }

        var hwnd = WindowFromPoint(new Point { X = point.X, Y = point.Y });
        var root = hwnd == 0 ? 0 : GetAncestor(hwnd, GaRoot);
        if (root == 0 || ProcessIdOf(root) != _browser.BrowserProcessId
            || await browser.VisiblePageAsync(cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return null;
        }

        var ratio = await browser.DevicePixelRatioAsync(session, cancellationToken).ConfigureAwait(false);
        Rect viewport;
        if (ClassName(hwnd) == "Chrome_RenderWidgetHostHWND" && GetWindowRect(hwnd, out var content))
        {
            viewport = content;
        }
        else
        {
            // No content child window under the point (the tab strip, or a build without it): place
            // the viewport from the frame. Side and bottom borders are the same width, so the page
            // sits that far in from the left and up from the bottom.
            var size = await browser.EvaluateAsync(session, "[window.innerWidth, window.innerHeight]", cancellationToken).ConfigureAwait(false);
            if (!GetWindowRect(root, out var frame) || size.GetArrayLength() != 2)
            {
                return null;
            }

            var width = (int)Math.Round(size[0].GetDouble() * ratio);
            var height = (int)Math.Round(size[1].GetDouble() * ratio);
            var border = Math.Max(0, (frame.Right - frame.Left - width) / 2);
            viewport = new Rect { Left = frame.Left + border, Right = frame.Left + border + width, Bottom = frame.Bottom - border, Top = frame.Bottom - border - height };
        }

        if (point.X < viewport.Left || point.X >= viewport.Right || point.Y < viewport.Top || point.Y >= viewport.Bottom)
        {
            // The tab strip or address bar: Chrome's own UI, which UI Automation can press.
            return null;
        }

        return new BrowserPoint(browser, session, (point.X - viewport.Left) / ratio, (point.Y - viewport.Top) / ratio);
    }

    private static PixelPoint ToScreen(double x, double y, ScreenshotGeometry geometry) =>
        CoordinateMapper.ToPhysical(new ScreenshotPoint(x, y), geometry);

    private static nint RootAt(PixelPoint point)
    {
        var hwnd = WindowFromPoint(new Point { X = point.X, Y = point.Y });
        return hwnd == 0 ? 0 : GetAncestor(hwnd, GaRoot);
    }

    private static bool IsBlocked(nint root) =>
        root != 0 && !ProcessElevation.IsCurrentProcessElevated && ProcessElevation.IsWindowElevated(root, out _);

    private static ControlOutcome Blocked(nint root) =>
        new(ControlStatus.Blocked, $"{AppName(root)} runs as administrator, so Windows won't let Clicky control it. The user has to do this step.");

    private static ControlOutcome? Unreachable(nint foreground)
    {
        if (foreground == 0)
        {
            return ControlOutcome.Failed("No window has the keyboard focus (Windows may be showing a secure screen).");
        }

        if (ProcessIdOf(foreground) == (uint)Environment.ProcessId)
        {
            return ControlOutcome.Failed("Clicky's own window has the keyboard focus; click the field to type into first.");
        }

        return IsBlocked(foreground) ? Blocked(foreground) : null;
    }

    private static string AppName(nint hwnd) => AppDescription(ProcessIdOf(hwnd)) ?? "the app";

    private static string? AppDescription(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            try
            {
                var description = process.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Another user's or an elevated process: the name is all we get.
            }

            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ProcessName(uint pid)
    {
        if (pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // ---- Names ----

    /// <summary>The control's name without a trailing role word: "Save button" is "Save".</summary>
    internal static string? ControlName(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var name = target.Trim().Trim('"', '\'', '“', '”');
        foreach (var word in s_roleWords.OrderByDescending(w => w.Length))
        {
            if (name.Length > word.Length + 1 && name.EndsWith(" " + word, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^(word.Length + 1)].TrimEnd();
                break;
            }
        }

        if (name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) && name.Length > 4)
        {
            name = name[4..];
        }

        return name.Trim('"', '\'', '“', '”');
    }

    private static bool NameMatches(AutomationElement element, string name)
    {
        var actual = element.Properties.Name.ValueOrDefault;
        return !string.IsNullOrWhiteSpace(actual)
            && (actual.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                || (actual.Length >= 2 && actual.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Label(AutomationElement element)
    {
        var name = element.Properties.Name.ValueOrDefault;
        return string.IsNullOrWhiteSpace(name) ? $"the {element.Properties.ControlType.ValueOrDefault.ToString().ToLowerInvariant()}" : $"\"{name.Trim()}\"";
    }

    private static string Named(string? target) => ControlName(target) is { } name ? $" \"{name}\"" : string.Empty;

    private sealed record BrowserPoint(CdpBrowser Browser, string Session, double X, double Y);

    /// <summary>What the UIA thread was doing, read when it doesn't return in time.</summary>
    private sealed class UiaStage
    {
        private volatile string? _acting;

        public string? Acting
        {
            get => _acting;
            set => _acting = value;
        }
    }
}
