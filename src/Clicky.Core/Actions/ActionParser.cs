using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Clicky.Core.Actions;

/// <param name="Action">The action, when it was valid.</param>
/// <param name="Name">The action name the model used ("?" when it gave none).</param>
/// <param name="Error">Why it was rejected, when it was.</param>
public readonly record struct ActionParseResult(ClickyAction? Action, string Name, string? Error)
{
    public bool IsValid => Action is not null;

    public static ActionParseResult Valid(ClickyAction action) => new(action, action.Name, null);

    public static ActionParseResult Invalid(string name, string error) => new(null, name, error);
}

/// <summary>
/// Turns model output into <see cref="ClickyAction"/>s. Never throws on bad model output: a
/// malformed or out-of-schema action comes back with the reason, for the log, and the reply goes on
/// without it.
/// </summary>
public static class ActionParser
{
    /// <summary>A tool call: the tool name is the action name and the input has no "type".</summary>
    public static ActionParseResult ParseToolCall(string name, JsonElement input, ActionSchema? schema = null)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return ActionParseResult.Invalid(name, "input must be an object");
        }

        var node = JsonNode.Parse(input.GetRawText())!.AsObject();
        node.Remove("type");
        node.Insert(0, "type", name);
        using var document = JsonDocument.Parse(node.ToJsonString());
        return Parse(document.RootElement, schema);
    }

    /// <summary>A tool call whose input arrived as streamed JSON text.</summary>
    public static ActionParseResult ParseToolCall(string name, string inputJson, ActionSchema? schema = null)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(inputJson) ? "{}" : inputJson);
            return ParseToolCall(name, document.RootElement, schema);
        }
        catch (JsonException ex)
        {
            return ActionParseResult.Invalid(name, $"input is not valid JSON ({ex.Message})");
        }
    }

    /// <summary>A complete action object with its "type", e.g. from an inline action tag.</summary>
    public static ActionParseResult Parse(string json, ActionSchema? schema = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
            return Parse(document.RootElement, schema);
        }
        catch (JsonException ex)
        {
            return ActionParseResult.Invalid("?", $"not valid JSON ({ex.Message})");
        }
    }

    public static ActionParseResult Parse(JsonElement element, ActionSchema? schema = null)
    {
        schema ??= ActionSchema.Default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return ActionParseResult.Invalid("?", "an action must be a JSON object");
        }

        if (!element.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            return ActionParseResult.Invalid("?", "type is required");
        }

        var name = typeElement.GetString()!;
        if (!schema.Contains(name))
        {
            return ActionParseResult.Invalid(name, $"unknown action '{name}'");
        }

        if (JsonSchemaSubset.Validate(element, schema.Definition(name), schema.Root) is { } error)
        {
            return ActionParseResult.Invalid(name, error);
        }

        ClickyAction? action;
        try
        {
            action = element.Deserialize(ActionJsonContext.Default.ClickyAction);
        }
        catch (JsonException ex)
        {
            return ActionParseResult.Invalid(name, ex.Message);
        }

        return action is null
            ? ActionParseResult.Invalid(name, "empty action")
            : CheckShape(action) is { } shapeError ? ActionParseResult.Invalid(name, shapeError) : ActionParseResult.Valid(action);
    }

    /// <summary>Rules a JSON schema can't say without making the tool schema hard for the model to read.</summary>
    private static string? CheckShape(ClickyAction action) => action switch
    {
        DrawAction { Shape: DrawShape.Arrow or DrawShape.Underline, Points.Count: not 2 } d => $"{d.Shape.ToString().ToLowerInvariant()} needs exactly 2 points",
        DrawAction { Shape: DrawShape.Circle, Points.Count: not 1 } => "circle needs exactly 1 point (the centre)",
        DrawAction { Shape: DrawShape.Circle, Radius: null } => "circle needs a radius",
        DrawAction { Shape: DrawShape.Freehand, Points.Count: < 2 } => "freehand needs at least 2 points",
        StepAction { Total: { } total, Index: var index } when index > total => "index is past total",
        StepAction { X: null, Y: not null } or StepAction { X: not null, Y: null } => "step needs both x and y, or neither",
        SayAction { Text: var text } when string.IsNullOrWhiteSpace(text) => "text is blank",
        _ => null,
    };

    public static string Serialize(ClickyAction action) => JsonSerializer.Serialize(action, ActionJsonContext.Default.ClickyAction);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(ClickyAction))]
internal sealed partial class ActionJsonContext : JsonSerializerContext;
