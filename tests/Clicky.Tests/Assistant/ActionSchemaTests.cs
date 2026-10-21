using System.Text.Json;
using Clicky.Core.Actions;

namespace Clicky.Tests.Assistant;

public sealed class ActionSchemaTests
{
    private static readonly string[] s_expectedNames =
        ["say", "point", "highlight", "draw", "step", "click", "type", "key", "scroll", "wait", "ask_permission", "done", "open_url", "use_tool"];

    private static readonly ClickyAction[] s_actions =
    [
        new SayAction("Hello there."),
        new PointAction(10, 20.5, "Share"),
        new HighlightAction(1, 2, 30, 40, "Panel"),
        new DrawAction(DrawShape.Arrow, [new DrawPoint(1, 2), new DrawPoint(30, 40)], Color: DrawColor.Red),
        new DrawAction(DrawShape.Circle, [new DrawPoint(50, 50)], Radius: 12, Label: "here"),
        new StepAction(1, "Open the File menu.", 3, 12, 8),
        new ClickAction(100, 200, MouseButton.Right, 2, "Save button"),
        new TypeAction("hello world"),
        new KeyAction("Ctrl+S"),
        new KeyAction("Tab", Repeat: 3),
        new ScrollAction(5, 6, ScrollDirection.Down, 4),
        new WaitAction(500),
        new AskPermissionAction("Send the email to Priya", "It can't be unsent", ActionRisk.Irreversible),
        new DoneAction("Saved the file.", true),
        new OpenUrlAction("https://github.com/notifications"),
        new UseToolAction("github__create_issue", JsonSerializer.SerializeToElement(new { title = "Bug", labels = new[] { "ui" } })),
    ];

    public static TheoryData<ClickyAction> EveryAction => new(s_actions);

    [Fact]
    public void Schema_has_the_fourteen_actions_in_order_with_descriptions()
    {
        var schema = ActionSchema.Default;

        Assert.Equal(1, schema.Version);
        Assert.Equal(s_expectedNames, schema.Names);
        Assert.All(schema.Names, name => Assert.False(string.IsNullOrWhiteSpace(schema.Description(name))));
        Assert.Equal(s_expectedNames.Length, schema.Root.GetProperty("oneOf").GetArrayLength());
    }

    [Fact]
    public void Every_action_type_has_a_schema_definition_and_the_reverse()
    {
        var derived = typeof(ClickyAction).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(ClickyAction)) && !t.IsAbstract).ToList();

        Assert.Equal(s_expectedNames.Length, derived.Count);
        foreach (var action in s_actions)
        {
            Assert.Contains(action.Name, ActionSchema.Default.Names);
        }
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void Every_action_serializes_to_schema_valid_json_and_parses_back_equal(ClickyAction action)
    {
        var json = ActionParser.Serialize(action);

        var parsed = ActionParser.Parse(json);

        Assert.True(parsed.IsValid, parsed.Error);
        Assert.Equal(action, parsed.Action);
    }

    [Theory]
    [MemberData(nameof(EveryAction))]
    public void Tool_input_schemas_drop_the_discriminator_and_keywords_strict_mode_rejects(ClickyAction action)
    {
        if (action is SayAction)
        {
            return; // Speech is plain text, not a tool.
        }

        var tool = ActionSchema.Default.ToolInputSchema(action.Name).ToJsonString();

        using var document = JsonDocument.Parse(tool);
        Assert.False(document.RootElement.GetProperty("properties").TryGetProperty("type", out _));
        Assert.DoesNotContain("\"type\"", document.RootElement.GetProperty("required").GetRawText());
        Assert.False(document.RootElement.GetProperty("additionalProperties").GetBoolean());
        foreach (var keyword in new[] { "\"minimum\"", "\"maximum\"", "\"minLength\"", "\"maxLength\"", "\"maxItems\"", "\"description\":\"" + ActionSchema.Default.Description(action.Name) })
        {
            Assert.DoesNotContain(keyword, tool);
        }

        // The same input, without "type", is accepted as a tool call.
        var input = JsonSerializer.SerializeToElement(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(ActionParser.Serialize(action))!
            .Where(p => p.Key != "type").ToDictionary());
        var parsed = ActionParser.ParseToolCall(action.Name, input);
        Assert.True(parsed.IsValid, parsed.Error);
        Assert.Equal(action, parsed.Action);
    }

    [Fact]
    public void Schema_json_for_prompts_is_compact_and_readable()
    {
        var json = ActionSchema.Default.Json;

        Assert.DoesNotContain("\n", json);
        Assert.Contains("\"ask_permission\"", json);
        Assert.Contains("\\\"Share\\\"", json); // Escaped quotes inside descriptions survive, < > and + aren't \u-escaped.
        Assert.DoesNotContain("\\u0022", json);
    }

    [Fact]
    public void Unsupported_schema_keywords_are_refused_at_load()
    {
        using var stream = new MemoryStream("""{"version":1,"$defs":{"x":{"type":"object","pattern":"a","description":"d"}}}"""u8.ToArray());

        Assert.Throws<InvalidOperationException>(() => ActionSchema.Load(stream));
    }
}
