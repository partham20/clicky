using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clicky.Core.Suggestions;

/// <summary>One idea as the editor writes it.</summary>
internal sealed record IdeaJson(string? Title, string? Detail, string? Why, string? Task, string? Topic, List<SourceJson>? Sources);

internal sealed record SourceJson(string App, string? Reference);

/// <summary>Reads the editor's JSON leniently: code fences, a preface, a bare array, missing fields.</summary>
internal static class SuggestionJson
{
    /// <summary>The ideas with a title; empty if the reply had no usable JSON.</summary>
    public static IReadOnlyList<IdeaJson> ParseIdeas(string reply)
    {
        if (Parse(reply) is not { } root)
        {
            return [];
        }

        var ideas = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("ideas", out var list) && list.ValueKind == JsonValueKind.Array => list,
            _ => default,
        };
        if (ideas.ValueKind != JsonValueKind.Array)
        {
            return root.ValueKind == JsonValueKind.Object && ReadIdea(root) is { } single ? [single] : [];
        }

        return [.. ideas.EnumerateArray().Select(ReadIdea).OfType<IdeaJson>()];
    }

    public static IdeaJson? ParseIdea(string reply)
    {
        if (Parse(reply) is not { } root)
        {
            return null;
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("ideas", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            return list.EnumerateArray().Select(ReadIdea).FirstOrDefault(i => i is not null);
        }

        return root.ValueKind == JsonValueKind.Object ? ReadIdea(root) : null;
    }

    private static JsonElement? Parse(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return null;
        }

        // From the first opening bracket to the last closing one: skips fences and any preface.
        var start = reply.IndexOfAny(['{', '[']);
        var end = reply.LastIndexOfAny(['}', ']']);
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(reply.AsMemory(start, end - start + 1), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IdeaJson? ReadIdea(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = Text(element, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var sources = new List<SourceJson>();
        if (element.TryGetProperty("sources", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var source in list.EnumerateArray())
            {
                if (source.ValueKind == JsonValueKind.String && source.GetString() is { Length: > 0 } app)
                {
                    sources.Add(new SourceJson(app, null));
                }
                else if (Text(source, "app") is { Length: > 0 } named)
                {
                    sources.Add(new SourceJson(named, Text(source, "reference") ?? Text(source, "url") ?? Text(source, "id")));
                }
            }
        }

        return new IdeaJson(title, Text(element, "detail"), Text(element, "why"), Text(element, "task"), Text(element, "topic"), sources);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(IdeaJson))]
internal sealed partial class SuggestionIdeaJsonContext : JsonSerializerContext;
