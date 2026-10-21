using Clicky.Core.Assistant;
using Clicky.Core.Geometry;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;
using Clicky.Tests.Memory;

namespace Clicky.Tests.Assistant;

public sealed class ModelRouterTests
{
    private static readonly ClaudeSettings s_auto = new();

    [Theory]
    [InlineData("where's the share button")]
    [InlineData("what is this app")]
    [InlineData("how do I mute myself here")]
    [InlineData("is there a prefix setting")] // "prefix" must not match "fix"
    public void Quick_questions_go_fast(string text)
    {
        Assert.Equal(ModelTier.Fast, ModelRouter.Route(new ClaudeRequest(text, RequestSource.Voice), s_auto).Tier);
    }

    [Theory]
    [InlineData("explain what this graph shows", "asks to explain")]
    [InlineData("why is this cell red", "asks to why")]
    [InlineData("walk me through setting up a pivot table", "asks to walk me through")]
    [InlineData("can you fix this error", "asks to fix")]
    [InlineData("write a reply to this email", "asks to write")]
    [InlineData("summarize this page", "asks to summarize")]
    [InlineData("compare these two plans", "asks to compare")]
    public void Explanations_and_work_go_deep(string text, string reason)
    {
        var route = ModelRouter.Route(new ClaudeRequest(text, RequestSource.Voice), s_auto);

        Assert.Equal(new RouteDecision(ModelTier.Deep, reason), route);
    }

    [Fact]
    public void Long_questions_and_agent_steps_go_deep()
    {
        var longText = string.Join(' ', Enumerable.Repeat("word", ModelRouter.LongQuestionWords));

        Assert.Equal(ModelTier.Deep, ModelRouter.Route(new ClaudeRequest(longText, RequestSource.Typed), s_auto).Tier);
        Assert.Equal(new RouteDecision(ModelTier.Deep, "agent"), ModelRouter.Route(new ClaudeRequest("next", RequestSource.Agent), s_auto));
    }

    [Fact]
    public void Settings_and_the_request_can_pin_a_tier()
    {
        var request = new ClaudeRequest("explain this", RequestSource.Voice);

        Assert.Equal(ModelTier.Fast, ModelRouter.Route(request, s_auto with { Routing = ModelRouting.AlwaysFast }).Tier);
        Assert.Equal(ModelTier.Deep, ModelRouter.Route(new ClaudeRequest("hi", RequestSource.Voice), s_auto with { Routing = ModelRouting.AlwaysDeep }).Tier);
        Assert.Equal(new RouteDecision(ModelTier.Fast, "requested"), ModelRouter.Route(request with { Tier = ModelTier.Fast }, s_auto));
    }

    [Fact]
    public void Models_follow_the_backend()
    {
        var api = new ClaudeSettings();
        var subscription = api with { Backend = ClaudeBackendKind.Subscription };

        Assert.Equal("claude-haiku-5-5", api.ModelFor(ModelTier.Fast));
        Assert.Equal("claude-opus-5-5", api.ModelFor(ModelTier.Deep));
        Assert.Equal("haiku", subscription.ModelFor(ModelTier.Fast));
        Assert.Equal("opus", subscription.ModelFor(ModelTier.Deep));
        Assert.Equal(ClaudeEffort.Low, api.EffortFor(ModelTier.Fast));
        Assert.Equal(ClaudeEffort.Medium, api.EffortFor(ModelTier.Deep));
    }

    [Fact]
    public void Normalize_repairs_hand_edited_values()
    {
        var bad = new ClaudeSettings(ApiFastModel: " ", MaxTurns: 0, IdleResetMinutes: -5, CliPath: " \"C:\\x\\claude.exe\" ", Backend: (ClaudeBackendKind)9);

        var fixedUp = bad.Normalize();

        Assert.Equal(ClaudeSettings.DefaultApiFastModel, fixedUp.ApiFastModel);
        Assert.Equal(ClaudeSettings.MinMaxTurns, fixedUp.MaxTurns);
        Assert.Equal(1, fixedUp.IdleResetMinutes);
        Assert.Equal("C:\\x\\claude.exe", fixedUp.CliPath);
        Assert.Equal(ClaudeBackendKind.Api, fixedUp.Backend);
        Assert.Equal(new ClaudeSettings(), ClickySettings.Default.Claude);
    }
}

public sealed class ConversationTests
{
    private static readonly HistoryLimits s_limits = new(MaxTurns: 4, TrimSlack: 2, MaxScreenshots: 3, ScreenshotsAfterDrop: 1);

    [Fact]
    public void History_grows_to_max_plus_slack_then_trims_back_to_max()
    {
        var conversation = new Conversation();
        for (var i = 0; i < 5; i++)
        {
            conversation.Append(Turn(i), s_limits);
        }

        Assert.Equal(5, conversation.Turns.Count);
        Assert.Equal(0, conversation.Generation);

        conversation.Append(Turn(5), s_limits);

        Assert.Equal(["q2", "q3", "q4", "q5"], conversation.Turns.Select(t => t.UserText));
        Assert.Equal(1, conversation.Generation);
    }

    [Fact]
    public void Old_screenshots_drop_in_one_go_when_there_are_too_many()
    {
        var conversation = new Conversation();
        for (var i = 0; i < 3; i++)
        {
            conversation.Append(Turn(i, withScreenshot: true), new HistoryLimits(MaxTurns: 40, MaxScreenshots: 3, ScreenshotsAfterDrop: 1));
        }

        Assert.All(conversation.Turns, t => Assert.NotNull(t.Screenshot));

        conversation.Append(Turn(3, withScreenshot: true), new HistoryLimits(MaxTurns: 40, MaxScreenshots: 3, ScreenshotsAfterDrop: 1));

        Assert.Equal([false, false, false, true], conversation.Turns.Select(t => t.Screenshot is not null));
        Assert.Equal(1, conversation.Generation);
    }

    [Fact]
    public void Idle_and_pending_tool_calls_follow_the_last_turn()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-21T09:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var conversation = new Conversation(time);
        conversation.Append(Turn(0) with { ToolCalls = [new("toolu_1", "point", "{}")], At = time.GetUtcNow() }, s_limits);

        Assert.Single(conversation.PendingToolCalls);
        Assert.False(conversation.IsIdle(TimeSpan.FromMinutes(10)));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(conversation.IsIdle(TimeSpan.FromMinutes(10)));
    }

    private static ConversationTurn Turn(int i, bool withScreenshot = false) =>
        new($"q{i}", withScreenshot ? [1, 2, 3] : null, $"a{i}", [], DateTimeOffset.UnixEpoch);
}

public sealed class PromptLibraryTests
{
    [Fact]
    public void Both_system_prompts_share_the_base_and_differ_in_how_actions_are_given()
    {
        var tools = PromptLibrary.SystemPrompt(ActionDelivery.Tools);
        var tags = PromptLibrary.SystemPrompt(ActionDelivery.Tags);

        Assert.StartsWith("You are Clicky", tools, StringComparison.Ordinal);
        Assert.StartsWith("You are Clicky", tags, StringComparison.Ordinal);
        Assert.Contains("through the tools", tools, StringComparison.Ordinal);
        Assert.DoesNotContain("<action>", tools, StringComparison.Ordinal);
        Assert.Contains("<action>", tags, StringComparison.Ordinal);
        Assert.Contains(Clicky.Core.Actions.ActionSchema.Default.Json, tags, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", tags, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", tags, StringComparison.Ordinal);
    }

    [Fact]
    public void User_text_carries_the_screen_context_and_how_the_question_arrived()
    {
        var shot = new RequestScreenshot([1], new ScreenshotGeometry(new PixelRect(0, 0, 2560, 1440), 1429, 804), new ScreenshotPoint(700.4, 400.6));

        Assert.Equal(
            "[Screenshot: 1429 x 804 pixels. The user's pointer is at (700, 401).]\nThe user said (speech-to-text): where is share",
            PromptLibrary.UserText(new ClaudeRequest(" where is share ", RequestSource.Voice, shot)));
        Assert.Equal(
            "[No screenshot with this message.]\nThe user typed: hello",
            PromptLibrary.UserText(new ClaudeRequest("hello", RequestSource.Typed)));
    }
}
