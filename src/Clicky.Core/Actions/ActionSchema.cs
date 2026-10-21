using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clicky.Core.Actions;

/// <summary>
/// The shared action schema, loaded from the embedded <c>actions.schema.json</c>. It is the single
/// source of truth: the API backend turns each action into a tool, the subscription backend pastes
/// it into its prompt, and <see cref="ActionParser"/> validates every action against it.
/// </summary>
public sealed class ActionSchema
{
    public const string ResourceName = "Clicky.Core.Actions.actions.schema.json";

    /// <summary>
    /// Keywords strict tool use does not accept. They come out of the schema sent to the API and
    /// are still enforced here, after the call.
    /// </summary>
    private static readonly string[] s_strictUnsupported = ["minimum", "maximum", "minLength", "maxLength", "maxItems", "$schema", "$id", "title", "version"];

    private static readonly Lazy<ActionSchema> s_default = new(() => Load(typeof(ActionSchema).Assembly.GetManifestResourceStream(ResourceName)
        ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.")));

    // Relaxed: keeps quotes and < > readable in the prompt; the text never reaches HTML.
    private static readonly JsonSerializerOptions s_compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly JsonDocument _document;

    private ActionSchema(JsonDocument document)
    {
        _document = document;
        JsonSchemaSubset.EnsureSupported(Root);
        Version = Root.GetProperty("version").GetInt32();
        Names = [.. Root.GetProperty("$defs").EnumerateObject().Select(p => p.Name)];
        Json = JsonSerializer.Serialize(Root, s_compact);
    }

    public static ActionSchema Default => s_default.Value;

    public JsonElement Root => _document.RootElement;

    public int Version { get; }

    /// <summary>Every action name, in schema order: say, point, highlight, …</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>The schema as compact JSON, for prompts.</summary>
    public string Json { get; }

    public static ActionSchema Load(Stream stream)
    {
        using (stream)
        {
            return new ActionSchema(JsonDocument.Parse(stream));
        }
    }

    public bool Contains(string name) => Root.GetProperty("$defs").TryGetProperty(name, out _);

    public JsonElement Definition(string name) => Root.GetProperty("$defs").GetProperty(name);

    public string Description(string name) => Definition(name).GetProperty("description").GetString()!;

    /// <summary>
    /// Input schema for the action as a strict tool: the "type" discriminator becomes the tool
    /// name, and keywords strict mode rejects are removed (the parser still checks them).
    /// </summary>
    public JsonObject ToolInputSchema(string name)
    {
        var schema = JsonNode.Parse(Definition(name).GetRawText())!.AsObject();
        schema.Remove("description");
        schema["properties"]!.AsObject().Remove("type");
        var required = schema["required"]!.AsArray();
        var keep = required.Select(r => r!.GetValue<string>()).Where(r => r != "type").ToArray();
        required.Clear();
        foreach (var r in keep)
        {
            required.Add(r);
        }

        StripForStrict(schema);
        return schema;
    }

    private static void StripForStrict(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var keyword in s_strictUnsupported)
            {
                obj.Remove(keyword);
            }

            // Strict mode only accepts minItems of 0 or 1.
            if (obj["minItems"] is JsonValue min && min.GetValue<int>() > 1)
            {
                obj.Remove("minItems");
            }

            foreach (var (_, child) in obj.ToList())
            {
                StripForStrict(child);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
            {
                StripForStrict(child);
            }
        }
    }
}
