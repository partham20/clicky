using System.Runtime.CompilerServices;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Buddy;
using Clicky.Core.Capture;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;
using Clicky.Core.Skills;
using Clicky.Core.Walkthroughs;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Walkthroughs.WalkthroughTestKit;

namespace Clicky.Tests.Walkthroughs;

public sealed class WalkthroughTurnTests
{
    [Fact]
    public void Walkthrough_requests_go_to_the_deep_model()
    {
        var route = ModelRouter.Route(new ClaudeRequest("[Walkthrough] next", RequestSource.Walkthrough), new ClaudeSettings());
        Assert.Equal(new RouteDecision(ModelTier.Deep, "walkthrough"), route);
    }

    [Fact]
    public void Context_goes_between_the_screen_line_and_the_words()
    {
        var request = new ClaudeRequest("how do I sort", RequestSource.Voice, new RequestScreenshot([1], Shot), Context: "[App guide \"Excel\"]\nSort is on Data.\n[End of app guide]\n");
        var text = PromptLibrary.UserText(request);

        Assert.Equal(
            "[Screenshot: 1920 x 1080 pixels.]\n[App guide \"Excel\"]\nSort is on Data.\n[End of app guide]\nThe user said (speech-to-text): how do I sort",
            text);
        Assert.Equal("[No screenshot with this message.]\n[Walkthrough] go", PromptLibrary.UserText(new ClaudeRequest("[Walkthrough] go", RequestSource.Walkthrough)));
    }

    [Fact]
    public void Prompts_name_the_step_and_what_comes_next()
    {
        var offTrack = WalkthroughPrompts.For(WalkthroughTurnReason.OffTrack, 3, "Click Insert", 15, "switched to chrome");
        Assert.StartsWith("[Walkthrough] The user was on step 3 (\"Click Insert\") but switched to chrome.", offTrack, StringComparison.Ordinal);
        Assert.Contains("numbered 3", offTrack, StringComparison.Ordinal);
        Assert.Contains("as step 4", WalkthroughPrompts.For(WalkthroughTurnReason.StepSkipped, 3, "Click Insert", 15), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stock_turn_captures_asks_speaks_draws_and_passes_events_back()
    {
        var renderer = new FakeRenderer();
        var presence = new BuddyPresence(NullLogger<BuddyPresence>.Instance);
        var speech = new FakeSpeech();
        var skills = new FakeSkills("[App guide \"Excel\"]\ntips\n[End of app guide]");
        var observed = new List<ReplyEvent>();
        ClaudeRequest? asked = null;
        var conversation = new Conversation();

        async IAsyncEnumerable<ReplyEvent> Ask(Conversation c, ClaudeRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            Assert.Same(conversation, c);
            asked = request;
            yield return new ReplyStarted(ClaudeBackendKind.Api, "m", ModelTier.Deep, "walkthrough");
            await Task.Yield();
            yield return new ReplyText("Now click Export.");
            yield return new ReplyAction(new PointAction(10, 10));
            yield return new ReplyAction(Step(2));
            yield return new ReplyCompleted(ReplyStop.EndTurn, [new SayAction("Now click Export."), Step(2)], ReplyUsage.None, null, TimeSpan.Zero, "m");
        }

        var turns = new AssistantWalkthroughTurns(
            new FakeCapture(),
            new FakeSettingsStore(new ClickySettings()),
            renderer,
            presence,
            skills,
            new FakeForeground(),
            () => conversation,
            Ask,
            () => speech,
            NullLogger<AssistantWalkthroughTurns>.Instance);
        var turn = new WalkthroughTurn(Guid.NewGuid(), WalkthroughTurnReason.StepDone, "[Walkthrough] next please", 1);

        await turns.RunAsync(turn, new RecordingWalkthroughs(observed), CancellationToken.None);

        Assert.NotNull(asked);
        Assert.Equal(RequestSource.Walkthrough, asked.Source);
        Assert.Equal("[Walkthrough] next please", asked.Text);
        Assert.Equal(skills.Context, asked.Context);
        Assert.Equal(Shot, asked.Screenshot?.Geometry);
        Assert.Equal("Now click Export.", speech.Text);
        Assert.True(speech.Completed);
        Assert.IsType<PointAction>(Assert.Single(renderer.Shown).Action); // the step is the engine's to draw
        Assert.Equal(5, observed.Count);
        Assert.Equal(BuddyState.Thinking, presence.State); // the presenter takes it from here
    }

    [Fact]
    public async Task A_cancelled_turn_stops_speaking_and_leaves_the_buddy_idle()
    {
        var presence = new BuddyPresence(NullLogger<BuddyPresence>.Instance);
        var speech = new FakeSpeech();
        using var cancel = new CancellationTokenSource();

        async IAsyncEnumerable<ReplyEvent> Ask(Conversation c, ClaudeRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new ReplyText("Now ");
            await cancel.CancelAsync();
            ct.ThrowIfCancellationRequested();
            yield return new ReplyText("never");
        }

        var turns = new AssistantWalkthroughTurns(
            new FakeCapture(),
            new FakeSettingsStore(new ClickySettings()),
            new FakeRenderer(),
            presence,
            new FakeSkills(null),
            new FakeForeground(),
            () => new Conversation(),
            Ask,
            () => speech,
            NullLogger<AssistantWalkthroughTurns>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            turns.RunAsync(new WalkthroughTurn(Guid.NewGuid(), WalkthroughTurnReason.StepDone, "x", 1), new RecordingWalkthroughs([]), cancel.Token));
        Assert.True(speech.Interrupted);
        Assert.False(speech.Completed);

        var silent = new AssistantWalkthroughTurns(
            new FakeCapture(fail: true),
            new FakeSettingsStore(new ClickySettings()),
            new FakeRenderer(),
            presence,
            new FakeSkills(null),
            new FakeForeground(),
            () => new Conversation(),
            Ask,
            () => speech,
            NullLogger<AssistantWalkthroughTurns>.Instance);
        await Assert.ThrowsAsync<CaptureException>(() =>
            silent.RunAsync(new WalkthroughTurn(Guid.NewGuid(), WalkthroughTurnReason.StepDone, "x", 1), new RecordingWalkthroughs([]), CancellationToken.None));
        Assert.Equal(BuddyState.Idle, presence.State);
    }

    private sealed class FakeCapture(bool fail = false) : ICaptureService
    {
        public Task<CaptureResult> CaptureAsync(CaptureTarget target, CancellationToken cancellationToken = default) =>
            fail ? throw new CaptureException("Screen capture is not available.") : Task.FromResult(new CaptureResult([1, 2, 3], Shot, Screen, TimeSpan.Zero));

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSkills(string? context) : IAppSkills
    {
        public string? Context => context;

        public IReadOnlyList<AppSkill> All => [];

        public IReadOnlyList<AppSkill> Matching(ForegroundApp? app) => [];

        public string? ContextFor(Conversation conversation, ForegroundApp? app) => context;
    }

    private sealed class FakeSpeech : IReplySpeech
    {
        public string Text { get; private set; } = string.Empty;

        public bool Completed { get; private set; }

        public bool Interrupted { get; private set; }

        public void Append(string text) => Text += text;

        public void Complete() => Completed = true;

        public void Interrupt() => Interrupted = true;
    }

    private sealed class RecordingWalkthroughs(List<ReplyEvent> observed) : IWalkthroughs
    {
        public WalkthroughSnapshot? Current => null;

        public event EventHandler<WalkthroughChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public void Observe(ReplyEvent replyEvent, Core.Geometry.ScreenshotGeometry? geometry) => observed.Add(replyEvent);

        public void MarkDone()
        {
        }

        public void Skip()
        {
        }

        public void Back()
        {
        }

        public void Retry()
        {
        }

        public void Cancel()
        {
        }
    }
}
