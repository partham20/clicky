using System.Text.Json;
using Clicky.Core.Actions;

namespace Clicky.Core.Assistant;

/// <summary>
/// Anthropic's computer toolset (<c>computer_toolset_20260801</c>), the computer-use tool the
/// Claude 5.x models are trained on. On the API backend it replaces Clicky's own click, type, key,
/// scroll and wait tools; each member call is mapped onto the same <see cref="ClickyAction"/>s, so
/// the permission gate and the UI Automation-first executors treat both the same way.
/// </summary>
/// <remarks>
/// Only members Clicky can do without moving the user's pointer unasked are enabled. Every agent
/// step already sends a fresh screenshot, so <c>screenshot</c> just points at it. Coordinates are in
/// the pixels of the screenshots we send, like everything else.
/// </remarks>
public static class ComputerToolset
{
    public const string ToolsetName = "computer";

    public const string Screenshot = "screenshot";

    /// <summary>The batch rule: once an action fails, every later one in the reply gets exactly this.</summary>
    public const string SkippedAfterFailure = "Not executed: an earlier computer action in this turn failed.";

    /// <summary>Members that are switched on; the rest are disabled in the tool's configs.</summary>
    public static IReadOnlyList<string> EnabledMembers { get; } =
        [Screenshot, "left_click", "right_click", "middle_click", "double_click", "triple_click", "scroll", "type", "key", "wait"];

    /// <summary>Members switched off: zoom (needs a full-resolution crop), and raw pointer moves, drags and held keys.</summary>
    public static IReadOnlyList<string> DisabledMembers { get; } =
        ["zoom", "mouse_move", "left_click_drag", "left_mouse_down", "left_mouse_up", "hold_key", "cursor_position"];

    /// <summary>
    /// Maps a member call onto an action. <c>screenshot</c> maps to no action and no error: the agent
    /// answers it with the step's screenshot.
    /// </summary>
    /// <returns>The action, or the reason the call can't be run.</returns>
    public static (ClickyAction? Action, string? Error) Map(string member, string inputJson)
    {
        JsonElement input;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            input = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return (null, $"input is not valid JSON ({ex.Message})");
        }

        if (input.ValueKind != JsonValueKind.Object)
        {
            return (null, "input must be an object");
        }

        switch (member)
        {
            case Screenshot:
                return (null, null);
            case "left_click" or "right_click" or "middle_click" or "double_click" or "triple_click":
                if (Modifiers(input) is { } modifiers)
                {
                    return (null, $"clicking with {modifiers} held isn't supported; press the keys with key, or click without them");
                }

                if (Coordinate(input, "coordinate") is not { } at)
                {
                    return (null, "give the coordinate to click");
                }

                var (button, count) = member switch
                {
                    "right_click" => (MouseButton.Right, 1),
                    "middle_click" => (MouseButton.Middle, 1),
                    "double_click" => (MouseButton.Left, 2),
                    "triple_click" => (MouseButton.Left, 3),
                    _ => (MouseButton.Left, 1),
                };
                return (new ClickAction(at.X, at.Y, button, count), null);
            case "scroll":
                if (Modifiers(input) is { } scrollModifiers)
                {
                    return (null, $"scrolling with {scrollModifiers} held isn't supported");
                }

                if (Coordinate(input, "coordinate") is not { } where)
                {
                    return (null, "give the coordinate to scroll at");
                }

                var direction = String(input, "scroll_direction") switch
                {
                    "up" => ScrollDirection.Up,
                    "down" => ScrollDirection.Down,
                    "left" => ScrollDirection.Left,
                    "right" => ScrollDirection.Right,
                    _ => (ScrollDirection?)null,
                };
                if (direction is null)
                {
                    return (null, "scroll_direction must be up, down, left or right");
                }

                var amount = Integer(input, "scroll_amount") ?? 3;
                return (new ScrollAction(where.X, where.Y, direction.Value, Math.Clamp(amount, 1, 20)), null);
            case "type":
                return String(input, "text") is { Length: > 0 } text ? (new TypeAction(text), null) : (null, "text is required");
            case "key":
                if (String(input, "text") is not { Length: > 0 } keys)
                {
                    return (null, "text is required");
                }

                return (new KeyAction(keys, Math.Clamp(Integer(input, "repeat") ?? 1, 1, 100)), null);
            case "wait":
                var seconds = input.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 1;
                return (new WaitAction((int)Math.Clamp(seconds * 1000, 100, 30_000)), null);
            default:
                return (null, $"{member} isn't available in Clicky");
        }
    }

    private static (double X, double Y)? Coordinate(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2)
        {
            return null;
        }

        var x = value[0];
        var y = value[1];
        return x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number && x.GetDouble() >= 0 && y.GetDouble() >= 0
            ? (x.GetDouble(), y.GetDouble())
            : null;
    }

    private static string? Modifiers(JsonElement input) => String(input, "text") is { Length: > 0 } text ? text : null;

    private static string? String(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Integer(JsonElement input, string name) =>
        input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
