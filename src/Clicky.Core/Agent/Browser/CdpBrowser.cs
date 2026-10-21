using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core.Actions;

namespace Clicky.Core.Agent.Browser;

/// <param name="TargetId">CDP target id.</param>
public sealed record BrowserPage(string TargetId, string Url, string Title);

/// <summary>
/// Page-level work in Clicky's own browser over CDP: open a page, find the tab on screen, and click,
/// type and scroll inside it with synthetic input events, so the user's real pointer never moves.
/// Coordinates are CSS pixels of the page's viewport. Thread-safe.
/// </summary>
public sealed class CdpBrowser
{
    private static readonly TimeSpan s_loadTimeout = TimeSpan.FromSeconds(10);

    // The control under a point, named the way a person would read it, for the permission gate.
    private const string DescribeScript = """
        (() => {
          let el = document.elementFromPoint(%X%, %Y%);
          if (!el) return null;
          const clickable = el.closest('button,a,[role=button],[role=link],[role=menuitem],[role=tab],input,select,textarea,summary,label') || el;
          const name = (clickable.getAttribute('aria-label') || clickable.getAttribute('title') || clickable.value || clickable.innerText || clickable.alt || '').trim().replace(/\s+/g, ' ').slice(0, 120);
          return { name, role: clickable.getAttribute('role') || clickable.tagName.toLowerCase() };
        })()
        """;

    private readonly CdpConnection _connection;
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);

    public CdpBrowser(CdpConnection connection)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _connection.EventReceived += OnEvent;
    }

    public bool IsConnected => _connection.IsOpen;

    public async Task<IReadOnlyList<BrowserPage>> PagesAsync(CancellationToken cancellationToken)
    {
        var result = await _connection.SendAsync("Target.getTargets", cancellationToken: cancellationToken).ConfigureAwait(false);
        var pages = new List<BrowserPage>();
        foreach (var info in result.GetProperty("targetInfos").EnumerateArray())
        {
            if (info.GetProperty("type").GetString() == "page")
            {
                pages.Add(new BrowserPage(info.GetProperty("targetId").GetString()!, Str(info, "url"), Str(info, "title")));
            }
        }

        return pages;
    }

    /// <summary>Opens <paramref name="url"/> in a blank tab if there is one, else a new tab, brings it to the front and waits (up to 10 s) for it to load.</summary>
    /// <returns>The page's title once loaded, or null if it was still loading.</returns>
    public async Task<string?> OpenAsync(string url, CancellationToken cancellationToken)
    {
        if (!ActionParser.IsWebAddress(url))
        {
            throw new ArgumentException("Only http and https addresses are opened.", nameof(url));
        }

        var pages = await PagesAsync(cancellationToken).ConfigureAwait(false);
        var blank = pages.FirstOrDefault(p => p.Url is "about:blank" or "chrome://newtab/" or "edge://newtab/" or "chrome://new-tab-page/");
        string targetId;
        if (blank is not null)
        {
            targetId = blank.TargetId;
            var session = await SessionAsync(targetId, cancellationToken).ConfigureAwait(false);
            await _connection.SendAsync("Page.navigate", new JsonObject { ["url"] = url }, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var created = await _connection.SendAsync("Target.createTarget", new JsonObject { ["url"] = url }, cancellationToken: cancellationToken).ConfigureAwait(false);
            targetId = created.GetProperty("targetId").GetString()!;
        }

        await _connection.SendAsync("Target.activateTarget", new JsonObject { ["targetId"] = targetId }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var page = await SessionAsync(targetId, cancellationToken).ConfigureAwait(false);
        var deadline = DateTime.UtcNow + s_loadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = await EvaluateAsync(page, "[document.readyState, document.title]", cancellationToken).ConfigureAwait(false);
            if (state.ValueKind == JsonValueKind.Array && state[0].GetString() == "complete")
            {
                return state[1].GetString();
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>The session of the tab the user can see (visible document), or null if none is.</summary>
    public async Task<string?> VisiblePageAsync(CancellationToken cancellationToken)
    {
        foreach (var page in await PagesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (page.Url.StartsWith("devtools://", StringComparison.Ordinal))
            {
                continue;
            }

            var session = await SessionAsync(page.TargetId, cancellationToken).ConfigureAwait(false);
            var visible = await EvaluateAsync(session, "document.visibilityState", cancellationToken).ConfigureAwait(false);
            if (visible.ValueKind == JsonValueKind.String && visible.GetString() == "visible")
            {
                return session;
            }
        }

        return null;
    }

    /// <summary>CSS pixels per physical pixel of the page (Windows scale times browser zoom).</summary>
    public async Task<double> DevicePixelRatioAsync(string session, CancellationToken cancellationToken)
    {
        var ratio = await EvaluateAsync(session, "window.devicePixelRatio", cancellationToken).ConfigureAwait(false);
        return ratio.ValueKind == JsonValueKind.Number && ratio.GetDouble() > 0 ? ratio.GetDouble() : 1;
    }

    public async Task<(string? Name, string? Role)> DescribeAtAsync(string session, double x, double y, CancellationToken cancellationToken)
    {
        var script = DescribeScript
            .Replace("%X%", x.ToString("0.##", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("%Y%", y.ToString("0.##", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var described = await EvaluateAsync(session, script, cancellationToken).ConfigureAwait(false);
        return described.ValueKind == JsonValueKind.Object
            ? (NullIfEmpty(Str(described, "name")), NullIfEmpty(Str(described, "role")))
            : (null, null);
    }

    public async Task ClickAsync(string session, double x, double y, MouseButton button, int count, CancellationToken cancellationToken)
    {
        var name = button switch
        {
            MouseButton.Right => "right",
            MouseButton.Middle => "middle",
            _ => "left",
        };
        await Mouse(session, "mouseMoved", x, y, "none", 0, cancellationToken).ConfigureAwait(false);
        for (var i = 1; i <= Math.Clamp(count, 1, 3); i++)
        {
            await Mouse(session, "mousePressed", x, y, name, i, cancellationToken).ConfigureAwait(false);
            await Mouse(session, "mouseReleased", x, y, name, i, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Types text into the focused element; new lines are Enter presses.</summary>
    public async Task TypeAsync(string session, string text, CancellationToken cancellationToken)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                await KeyAsync(session, "Enter", "Enter", 13, "\r", cancellationToken).ConfigureAwait(false);
            }

            if (lines[i].Length > 0)
            {
                await _connection.SendAsync("Input.insertText", new JsonObject { ["text"] = lines[i] }, session, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <param name="deltaX">CSS pixels, positive scrolls right.</param>
    /// <param name="deltaY">CSS pixels, positive scrolls down.</param>
    public async Task ScrollAsync(string session, double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken) =>
        await _connection.SendAsync(
            "Input.dispatchMouseEvent",
            new JsonObject { ["type"] = "mouseWheel", ["x"] = x, ["y"] = y, ["deltaX"] = deltaX, ["deltaY"] = deltaY },
            session,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<JsonElement> EvaluateAsync(string session, string expression, CancellationToken cancellationToken)
    {
        var result = await _connection.SendAsync(
            "Runtime.evaluate",
            new JsonObject { ["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = false },
            session,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.TryGetProperty("exceptionDetails", out var exception))
        {
            throw new CdpException($"Page script failed: {Str(exception, "text")}");
        }

        return result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var v) ? v : default;
    }

    private async Task<string> SessionAsync(string targetId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(targetId, out var existing))
        {
            return existing;
        }

        var attached = await _connection.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var session = attached.GetProperty("sessionId").GetString()!;
        return _sessions.GetOrAdd(targetId, session);
    }

    private async Task KeyAsync(string session, string key, string code, int virtualKey, string text, CancellationToken cancellationToken)
    {
        await _connection.SendAsync("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = virtualKey, ["text"] = text }, session, cancellationToken: cancellationToken).ConfigureAwait(false);
        await _connection.SendAsync("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = virtualKey }, session, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task Mouse(string session, string type, double x, double y, string button, int clickCount, CancellationToken cancellationToken) =>
        await _connection.SendAsync(
            "Input.dispatchMouseEvent",
            new JsonObject { ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button, ["clickCount"] = clickCount },
            session,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    private void OnEvent(string method, JsonElement parameters, string? session)
    {
        if (method == "Target.detachedFromTarget" && parameters.ValueKind == JsonValueKind.Object && Str(parameters, "sessionId") is { Length: > 0 } detached)
        {
            foreach (var (target, value) in _sessions)
            {
                if (value == detached)
                {
                    _sessions.TryRemove(target, out _);
                }
            }
        }
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
