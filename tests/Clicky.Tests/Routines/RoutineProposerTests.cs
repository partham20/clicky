using System.Text.Json;
using Clicky.Core.Actions;
using Clicky.Core.Agent;
using Clicky.Core.Assistant;
using Clicky.Core.Routines;
using Clicky.Core.Geometry;
using Clicky.Tests.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using static Clicky.Tests.Routines.RoutineScheduleTests;

namespace Clicky.Tests.Routines;

public sealed class RoutineProposerTests
{
    private static readonly DateTimeOffset Monday7 = Utc(2026, 1, 5, 7, 0);

    private static RoutineAction Briefing(string schedule = """{"kind":"daily","time":"08:00"}""") =>
        new("Morning briefing", "Brief me on today's calendar.", JsonDocument.Parse(schedule).RootElement.Clone());

    /// <summary>A conversation whose last reply made the tool call <paramref name="id"/>.</summary>
    private static Conversation WithPendingCall(string id)
    {
        var conversation = new Conversation();
        conversation.Append(new ConversationTurn("brief me every morning", null, "I'll set that up.", [new ToolCallRecord(id, "routine", "{}")], Monday7), new HistoryLimits());
        return conversation;
    }

    private static async Task<(RoutineProposer Proposer, Presenter Presenter)> Create(EngineHarness h)
    {
        await h.StartAsync();
        var presenter = new Presenter();
        return (new RoutineProposer(h.Engine, presenter, NullLogger<RoutineProposer>.Instance, h.Time, () => h.Zone), presenter);
    }

    [Fact]
    public async Task Saving_on_the_card_creates_the_routine_and_tells_Claude()
    {
        await using var h = new EngineHarness(Monday7);
        var (proposer, presenter) = await Create(h);
        await using var _ = proposer;
        var conversation = WithPendingCall("toolu_1");

        proposer.Propose(Briefing(), conversation, "toolu_1", near: null);
        await proposer.WhenIdleAsync();

        var routine = Assert.Single(h.Engine.Routines);
        Assert.Equal(("Morning briefing", "Brief me on today's calendar.", (string?)null), (routine.Name, routine.Prompt, routine.ClickyId));
        Assert.Equal(RoutineSchedule.Daily(new TimeOnly(8, 0)), routine.Schedule);
        Assert.Equal(Utc(2026, 1, 5, 8, 0), routine.NextDueAt);

        // Asked like the permission gate, but Save / Not now only.
        Assert.Equal(RoutineProposer.CardTask, Assert.Single(presenter.Tasks));
        var prompt = Assert.Single(((FakeCard)presenter.Last).Prompts);
        Assert.Equal(PermissionKind.Routine, prompt.Kind);
        Assert.False(prompt.OffersAlways);
        Assert.Equal(RoutineProposer.SaveLabel, prompt.AllowLabel);
        Assert.Contains("Morning briefing", prompt.Question, StringComparison.Ordinal);
        Assert.Contains("Brief me on today's calendar.", prompt.Detail, StringComparison.Ordinal);
        Assert.True(((FakeCard)presenter.Last).Finished!.Value.Success);
        Assert.True(((FakeCard)presenter.Last).Disposed);

        var result = Assert.Single(conversation.UnsentResults!);
        Assert.Equal(("toolu_1", "routine", false), (result.ToolCallId, result.Name, result.IsError));
        Assert.Contains("saved the routine", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_now_saves_nothing()
    {
        await using var h = new EngineHarness(Monday7);
        var (proposer, presenter) = await Create(h);
        await using var _ = proposer;
        presenter.NewCard = () =>
        {
            var card = new FakeCard();
            card.Answers.Enqueue(PermissionAnswer.NotNow);
            return card;
        };
        var conversation = WithPendingCall("toolu_1");

        proposer.Propose(Briefing(), conversation, "toolu_1", near: null);
        await proposer.WhenIdleAsync();

        Assert.Empty(h.Engine.Routines);
        Assert.Null(((FakeCard)presenter.Last).Finished);
        Assert.True(((FakeCard)presenter.Last).Disposed);
        Assert.Contains("chose not to save", Assert.Single(conversation.UnsentResults!).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_on_the_card_counts_as_not_now()
    {
        await using var h = new EngineHarness(Monday7);
        var (proposer, presenter) = await Create(h);
        await using var _ = proposer;
        presenter.NewCard = () => new StoppingCard();

        proposer.Propose(Briefing(), new Conversation(), toolCallId: null, near: null);
        await proposer.WhenIdleAsync();

        Assert.Empty(h.Engine.Routines);
    }

    [Fact]
    public async Task A_time_that_has_passed_is_refused_without_asking()
    {
        await using var h = new EngineHarness(Monday7);
        var (proposer, presenter) = await Create(h);
        await using var _ = proposer;
        var conversation = WithPendingCall("toolu_1");
        ToolOutcome? told = null;
        proposer.Answered += (_, outcome) => told = outcome;

        proposer.Propose(Briefing("""{"kind":"once","date":"2026-01-05","time":"06:00"}"""), conversation, "toolu_1", near: null);
        await proposer.WhenIdleAsync();

        Assert.Empty(presenter.Cards);
        Assert.Empty(h.Engine.Routines);
        Assert.True(told!.IsError);
        Assert.Contains("already passed", told.Text, StringComparison.Ordinal);
        Assert.Same(told, Assert.Single(conversation.UnsentResults!));
    }

    [Fact]
    public async Task An_answer_after_the_conversation_moved_on_is_not_sent()
    {
        await using var h = new EngineHarness(Monday7);
        var (proposer, _) = await Create(h);
        await using var __ = proposer;
        var conversation = WithPendingCall("toolu_1");
        conversation.Append(new ConversationTurn("what's this?", null, "A chart.", [], Monday7), new HistoryLimits());

        proposer.Propose(Briefing(), conversation, "toolu_1", near: null);
        await proposer.WhenIdleAsync();

        Assert.Single(h.Engine.Routines);
        Assert.Null(conversation.UnsentResults);
    }

    [Fact]
    public async Task Proposals_are_asked_one_at_a_time_in_order()
    {
        await using var h = new EngineHarness(Monday7);
        var (proposer, presenter) = await Create(h);
        await using var _ = proposer;
        var gate = new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        presenter.NewCard = () =>
        {
            IAgentCard card = first ? new WaitingCard(gate.Task) : new FakeCard();
            first = false;
            return card;
        };

        proposer.Propose(Briefing(), new Conversation(), null, null);
        proposer.Propose(new RoutineAction("Inbox", "Check my inbox.", JsonDocument.Parse("""{"kind":"every","every_minutes":180,"from":"09:00","until":"21:00"}""").RootElement.Clone()), new Conversation(), null, null);
        await EngineHarness.WaitUntil(() => presenter.Cards.Count == 1, "the first card");
        await Task.Delay(50);
        Assert.Single(presenter.Cards);

        gate.SetResult(PermissionAnswer.AllowOnce);
        await proposer.WhenIdleAsync();

        Assert.Equal(2, presenter.Cards.Count);
        Assert.Equal(["Inbox", "Morning briefing"], h.Engine.Routines.Select(r => r.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Unanswered_routine_calls_tell_Claude_the_user_has_not_answered()
    {
        var outcome = DefaultToolResults.For(new ToolCallRecord("toolu_1", "routine", "{}"));

        Assert.False(outcome.IsError);
        Assert.Equal(DefaultToolResults.Proposed, outcome.Text);
    }

    private sealed class StoppingCard : IAgentCard
    {
        private readonly CancellationTokenSource _stop = new();

        public CancellationToken StopRequested => _stop.Token;

        public async Task<PermissionAnswer> AskAsync(PermissionPrompt prompt, CancellationToken cancellationToken)
        {
            await _stop.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return PermissionAnswer.AllowOnce;
        }

        public void ShowStep(int number, string text)
        {
        }

        public Task<bool> CountdownAsync(string upcoming, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult(true);

        public void Avoid(PixelPoint point)
        {
        }

        public void Finish(string message, bool success)
        {
        }

        public void Dispose() => _stop.Dispose();
    }

    private sealed class WaitingCard(Task<PermissionAnswer> answer) : IAgentCard
    {
        public CancellationToken StopRequested => CancellationToken.None;

        public Task<PermissionAnswer> AskAsync(PermissionPrompt prompt, CancellationToken cancellationToken) => answer.WaitAsync(cancellationToken);

        public void ShowStep(int number, string text)
        {
        }

        public Task<bool> CountdownAsync(string upcoming, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult(true);

        public void Avoid(PixelPoint point)
        {
        }

        public void Finish(string message, bool success)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Presenter : IAgentPresenter
    {
        public List<IAgentCard> Cards { get; } = [];

        public List<string> Tasks { get; } = [];

        public Func<IAgentCard> NewCard { get; set; } = () => new FakeCard();

        public IAgentCard Last => Cards[^1];

        public IAgentCard Begin(string task, PixelRect? near)
        {
            var card = NewCard();
            lock (Cards)
            {
                Cards.Add(card);
                Tasks.Add(task);
            }

            return card;
        }
    }
}
