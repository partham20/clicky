using System.Text.Json;
using Clicky.Core.Assistant;
using Clicky.Core.Geometry;

namespace Clicky.Tests.Assistant;

public sealed class AnthropicApiBackendTests
{
    [Fact]
    public void Request_carries_history_answers_tool_calls_and_caches_the_fixed_prefix()
    {
        var conversation = new Conversation();
        conversation.Append(
            new ConversationTurn(
                "first",
                [9],
                "That's it.",
                [new ToolCallRecord("toolu_1", "point", """{"x":1,"y":2}""")],
                DateTimeOffset.UnixEpoch,
                Thinking: [new ThinkingRecord(string.Empty, "sig-1")]),
            new HistoryLimits());
        var shot = new RequestScreenshot([1, 2, 3], new ScreenshotGeometry(new PixelRect(0, 0, 10, 10), 10, 10));

        var parameters = AnthropicApiBackend.BuildParameters(conversation, new ClaudeRequest("next", RequestSource.Voice, shot), "user text", "claude-haiku-5-5", ClaudeEffort.Low);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters.RawBodyData));
        var root = json.RootElement;

        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("drop_block", root.GetProperty("thinking").GetProperty("block_binding").GetProperty("prefix_mismatch_behavior").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.Equal("ephemeral", root.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", root.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());

        var tools = root.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(13, tools.Count); // Every action except say and use_tool (connector tools are real tools here).
        Assert.All(tools, t => Assert.True(t.GetProperty("strict").GetBoolean()));

        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()!));
        Assert.Equal(["image", "text"], Types(messages[0]));
        Assert.Equal(["thinking", "text", "tool_use"], Types(messages[1]));
        Assert.Equal("sig-1", messages[1].GetProperty("content")[0].GetProperty("signature").GetString());

        // The new question first answers the tool call left open by the last reply.
        Assert.Equal(["tool_result", "image", "text"], Types(messages[2]));
        Assert.Equal("toolu_1", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("user text", messages[2].GetProperty("content")[2].GetProperty("text").GetString());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), messages[2].GetProperty("content")[1].GetProperty("source").GetProperty("data").GetString());
    }

    [Fact]
    public void Interrupted_replies_are_marked_so_the_model_knows_it_was_cut_off()
    {
        var conversation = new Conversation();
        conversation.Append(new ConversationTurn("q", null, "Once upon", [], DateTimeOffset.UnixEpoch, Interrupted: true), new HistoryLimits());

        var parameters = AnthropicApiBackend.BuildParameters(conversation, new ClaudeRequest("stop", RequestSource.Voice), "u", "m", ClaudeEffort.Low);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(parameters.RawBodyData));

        Assert.Equal("Once upon (The user interrupted me here.)", json.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    private static string[] Types(JsonElement message) =>
        [.. message.GetProperty("content").EnumerateArray().Select(c => c.GetProperty("type").GetString()!)];
}
