using System.Text.Json;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Assistant;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Hotkeys;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;
using static Clicky.Tests.Agent.AgentTestData;

namespace Clicky.Tests.Agent;

public sealed class AgentPartsTests
{
    [Theory]
    [InlineData("left_click", """{"coordinate":[10,20]}""", MouseButton.Left, 1)]
    [InlineData("double_click", """{"coordinate":[10,20]}""", MouseButton.Left, 2)]
    [InlineData("triple_click", """{"coordinate":[10,20]}""", MouseButton.Left, 3)]
    [InlineData("right_click", """{"coordinate":[10,20]}""", MouseButton.Right, 1)]
    public void Toolset_clicks_map_to_click_actions(string member, string input, MouseButton button, int count)
    {
        var (action, error) = ComputerToolset.Map(member, input);

        Assert.Null(error);
        Assert.Equal(new ClickAction(10, 20, button, count), action);
    }

    [Fact]
    public void Toolset_members_map_or_explain_what_is_wrong()
    {
        Assert.Equal((null, null), ComputerToolset.Map("screenshot", "{}"));
        Assert.Equal(new ScrollAction(5, 6, ScrollDirection.Down, 20), ComputerToolset.Map("scroll", """{"coordinate":[5,6],"scroll_direction":"down","scroll_amount":50}""").Action);
        Assert.Equal(new TypeAction("hi"), ComputerToolset.Map("type", """{"text":"hi"}""").Action);
        Assert.Equal(new KeyAction("ctrl+s", 3), ComputerToolset.Map("key", """{"text":"ctrl+s","repeat":3}""").Action);
        Assert.Equal(new WaitAction(1500), ComputerToolset.Map("wait", """{"duration":1.5}""").Action);

        Assert.Contains("coordinate", ComputerToolset.Map("left_click", "{}").Error, StringComparison.Ordinal);
        Assert.Contains("shift", ComputerToolset.Map("left_click", """{"coordinate":[1,1],"text":"shift"}""").Error, StringComparison.Ordinal);
        Assert.Contains("scroll_direction", ComputerToolset.Map("scroll", """{"coordinate":[1,1]}""").Error, StringComparison.Ordinal);
        Assert.Contains("isn't available", ComputerToolset.Map("zoom", """{"region":[0,0,1,1]}""").Error, StringComparison.Ordinal);
        Assert.Contains("not valid JSON", ComputerToolset.Map("type", "{").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_results_cover_calls_the_agent_never_ran()
    {
        Assert.Equal(DefaultToolResults.Shown, DefaultToolResults.For(new ToolCallRecord("a", "point", "{}")).Text);
        var notRun = DefaultToolResults.For(new ToolCallRecord("b", "click", "{}"));
        Assert.Equal(DefaultToolResults.NotRun, notRun.Text);
        Assert.True(notRun.IsError);
        var rejected = DefaultToolResults.For(new ToolCallRecord("c", "click", "{}", Accepted: false, Error: "x is required"));
        Assert.StartsWith("Rejected: x is required", rejected.Text, StringComparison.Ordinal);

        var resolved = DefaultToolResults.Resolve(
            [new ToolCallRecord("a", "point", "{}"), new ToolCallRecord("b", "click", "{}")],
            [new ToolOutcome("b", "click", "Clicked.")]);
        Assert.Equal(["Shown to the user.", "Clicked."], resolved.Select(r => r.Text));
    }

    [Theory]
    [InlineData("Ctrl+S", new[] { VirtualKeys.LeftControl }, 0x53)]
    [InlineData("ctrl+shift+t", new[] { VirtualKeys.LeftControl, VirtualKeys.LeftShift }, 0x54)]
    [InlineData("Return", new int[0], VirtualKeys.Enter)]
    [InlineData("super+e", new[] { VirtualKeys.LeftWin }, 0x45)]
    [InlineData("Alt+F4", new[] { VirtualKeys.LeftMenu }, 0x73)]
    [InlineData("Ctrl++", new[] { VirtualKeys.LeftControl }, 0xBB)]
    public void Key_chords_parse_the_way_models_write_them(string text, int[] modifiers, int key)
    {
        var (chord, error) = KeyChord.Parse(text);

        Assert.Null(error);
        Assert.Equal(modifiers, chord!.Modifiers);
        Assert.Equal(key, chord.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Hyper")]
    [InlineData("Ctrl+Snowman")]
    [InlineData("Hyper+Q")]
    public void Unknown_keys_are_refused(string text) => Assert.NotNull(KeyChord.Parse(text).Error);

    [Fact]
    public void Risky_targets_are_irreversible_and_ordinary_ones_are_not()
    {
        Assert.True(RiskClassifier.Classify(new ClickAction(1, 1), new ControlTarget("Send", "button", "Outlook")).Irreversible);
        Assert.True(RiskClassifier.Classify(new ClickAction(1, 1, Target: "Delete forever"), ControlTarget.Unknown).Irreversible);
        Assert.True(RiskClassifier.Classify(new ClickAction(1, 1), new ControlTarget("Place order", "button", "Chrome")).Irreversible);
        Assert.True(RiskClassifier.Classify(new KeyAction("Enter"), new ControlTarget(null, "edit", "Microsoft Teams", "ms-teams")).Irreversible);
        Assert.True(RiskClassifier.Classify(new KeyAction("Ctrl+Enter"), new ControlTarget(null, "edit", "Word", "winword")).Irreversible);
        Assert.True(RiskClassifier.Classify(new TypeAction("hello\n"), new ControlTarget(null, "edit", "Slack", "slack")).Irreversible);

        Assert.False(RiskClassifier.Classify(new ClickAction(1, 1), new ControlTarget("Save", "button", "Notepad", "notepad")).Irreversible);
        Assert.False(RiskClassifier.Classify(new KeyAction("Enter"), new ControlTarget(null, "edit", "Notepad", "notepad")).Irreversible);
        Assert.False(RiskClassifier.Classify(new TypeAction("hello"), new ControlTarget(null, "edit", "Slack", "slack")).Irreversible);

        Assert.False(RiskClassifier.Classify(Tool("github__list_issues", readOnly: true)).Irreversible);
        Assert.True(RiskClassifier.Classify(Tool("github__delete_repo", readOnly: false, destructive: true)).Irreversible);
    }

    [Fact]
    public void The_gate_remembers_per_kind_and_one_step_approval_is_used_once()
    {
        var gate = new PermissionGate();
        Assert.False(gate.AlwaysControl);

        gate.Remember(PermissionKind.Connector, ["github__create_issue"]);
        Assert.True(gate.AlwaysTool("github__create_issue"));
        Assert.False(gate.AlwaysControl);

        gate.Remember(PermissionKind.Mouse);
        Assert.True(gate.AlwaysMouse);
        Assert.False(gate.AlwaysControl);

        gate.ApproveNextStep();
        Assert.True(gate.TakeNextStepApproval());
        Assert.False(gate.TakeNextStepApproval());
    }

    [Fact]
    public void Open_url_takes_only_web_addresses()
    {
        Assert.True(ActionParser.Parse("""{"type":"open_url","url":"https://example.com/a"}""").IsValid);
        Assert.False(ActionParser.Parse("""{"type":"open_url","url":"file:///C:/Windows/notepad.exe"}""").IsValid);
        Assert.False(ActionParser.Parse("""{"type":"open_url","url":"example.com"}""").IsValid);
        Assert.False(ActionParser.IsWebAddress("javascript:alert(1)"));
    }

    [Fact]
    public void Agent_settings_are_clamped()
    {
        var settings = new AgentSettings(MaxSteps: 0, CancelWindowSeconds: 99, BrowserPath: null!).Normalize();

        Assert.Equal(1, settings.MaxSteps);
        Assert.Equal(AgentSettings.MaxCancelWindowSeconds, settings.CancelWindowSeconds);
        Assert.Equal(string.Empty, settings.BrowserPath);
        Assert.Equal(AgentSettings.DefaultMaxSteps, new ClickySettings().Normalize().Agent.MaxSteps);
    }

    [Fact]
    public void Action_tag_steps_carry_the_results_in_the_text()
    {
        var step = new AgentStep(2, 25, [new ToolOutcome(null, "click", "Pressed \"Save\"."), new ToolOutcome(null, "type", "Windows blocked the typing.", IsError: true)]);
        var request = new ClaudeRequest("Carry on.", RequestSource.Agent, Shot, Step: step);

        var tags = PromptLibrary.UserText(request, ActionDelivery.Tags);
        var tools = PromptLibrary.UserText(request);

        Assert.StartsWith("[What your actions did:\n- click: Pressed \"Save\".\n- type: FAILED: Windows blocked the typing.\n]\n[Agent step 2 of at most 25.]\n", tags, StringComparison.Ordinal);
        Assert.DoesNotContain("What your actions did", tools, StringComparison.Ordinal); // Tool results carry them on the API.
        Assert.Contains("[Agent step 2 of at most 25.]", tools, StringComparison.Ordinal);
    }

    [Fact]
    public void The_api_request_swaps_in_the_computer_toolset_and_answers_toolset_calls()
    {
        var conversation = new Conversation();
        conversation.Append(
            new ConversationTurn("q", null, "Clicking.", [new ToolCallRecord("toolu_1", "left_click", """{"coordinate":[1,2]}""", ToolsetName: "computer")], DateTimeOffset.UnixEpoch),
            new HistoryLimits());
        var step = new AgentStep(1, 25, [new ToolOutcome("toolu_1", "left_click", "Nothing there.", IsError: true)]);
        var request = new ClaudeRequest("Carry on.", RequestSource.Agent, Shot, Step: step);
        var tools = new[] { Tool("github__list_issues", readOnly: true) };

        var parameters = AnthropicApiBackend.BuildParameters(conversation, request, "u", "claude-opus-5-5", ClaudeEffort.Medium, tools, computerToolset: true);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters.RawBodyData));
        var root = json.RootElement;

        var declared = root.GetProperty("tools").EnumerateArray().ToList();
        var names = declared.Where(t => t.TryGetProperty("name", out _)).Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("click", names);
        Assert.DoesNotContain("type", names);
        Assert.Contains("point", names);
        Assert.Contains("open_url", names);
        Assert.Contains("github__list_issues", names);
        var toolset = Assert.Single(declared, t => t.TryGetProperty("type", out var type) && type.GetString()!.StartsWith("computer_toolset", StringComparison.Ordinal));
        Assert.Contains("zoom", toolset.GetRawText(), StringComparison.Ordinal);
        var connector = Assert.Single(declared, t => t.TryGetProperty("name", out var n) && n.GetString() == "github__list_issues");
        Assert.False(connector.TryGetProperty("strict", out var strict) && strict.GetBoolean());

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        var use = messages[1].GetProperty("content").EnumerateArray().Single(c => c.GetProperty("type").GetString() == "tool_use");
        Assert.Equal("computer", use.GetProperty("toolset_name").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.Equal("computer", result.GetProperty("toolset_name").GetString());
    }

    [Fact]
    public void The_subscription_tool_catalog_is_sent_only_when_it_changes()
    {
        var tools = new[] { Tool("github__list_issues", readOnly: true), Tool("github__create_issue", readOnly: false) };

        var (text, key) = ClaudeCodeBackend.ToolCatalog(tools, shownKey: null);
        Assert.NotNull(text);
        Assert.Contains("use_tool", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("github__create_issue", StringComparison.Ordinal) < text.IndexOf("github__list_issues", StringComparison.Ordinal));
        Assert.Contains("github__list_issues (read-only)", text, StringComparison.Ordinal);

        Assert.Null(ClaudeCodeBackend.ToolCatalog(tools, key).Text);
        Assert.Contains("No connected app tools", ClaudeCodeBackend.ToolCatalog([], key).Text, StringComparison.Ordinal);
        Assert.Equal((null, null), ClaudeCodeBackend.ToolCatalog([], null));
    }
}
