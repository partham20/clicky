using Clicky.Core.Assistant;
using Clicky.Core.Capture;
using Clicky.Core.Geometry;
using Clicky.Core.Onboarding;
using Clicky.Core.Settings;
using Clicky.Core.Talk;
using Clicky.Tests.Ink;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Onboarding;

public sealed class OnboardingFlowTests : IDisposable
{
    private static readonly ScreenshotGeometry OneToOne = new(new PixelRect(0, 0, 1000, 800), 1000, 800);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Steps_run_setup_then_the_four_practice_steps_then_finish()
    {
        var flow = new OnboardingFlow();
        var seen = new List<OnboardingStep> { flow.Step };
        while (!flow.IsLast)
        {
            flow.Next();
            seen.Add(flow.Step);
        }

        Assert.Equal(
            [
                OnboardingStep.Welcome, OnboardingStep.ConnectClaude, OnboardingStep.Voice, OnboardingStep.SayHello,
                OnboardingStep.WatchItDraw, OnboardingStep.CircleSomething, OnboardingStep.Dictate, OnboardingStep.Finish,
            ],
            seen);
        Assert.Equal(4, OnboardingFlow.PracticeCount);
        Assert.Equal(OnboardingFlow.StepCount, flow.Position);

        flow.Next();
        Assert.Equal(OnboardingStep.Finish, flow.Step);
        flow.GoTo(OnboardingStep.Welcome);
        Assert.False(flow.CanGoBack);
        flow.Back();
        Assert.Equal(OnboardingStep.Welcome, flow.Step);
    }

    [Fact]
    public void Talk_is_ignored_outside_the_talk_practice_steps()
    {
        var flow = new OnboardingFlow();
        flow.GoTo(OnboardingStep.ConnectClaude);

        flow.Observe(new TutorialTalk(true, "hello", "hi", 0, false));

        Assert.False(flow.IsStepDone);
        Assert.Equal(FeedbackKind.None, flow.FeedbackKind);
    }

    [Fact]
    public void Saying_hello_completes_on_any_answer()
    {
        var flow = At(OnboardingStep.SayHello);

        flow.Observe(new TutorialTalk(true, "Hello Clicky", "Hi there!", 0, false));

        Assert.True(flow.IsStepDone);
        Assert.Equal(FeedbackKind.Success, flow.FeedbackKind);
        Assert.Contains("Hello Clicky", flow.Feedback, StringComparison.Ordinal);
        Assert.Equal(1, flow.PracticeDone);
    }

    [Fact]
    public void A_failed_answer_shows_the_problem_and_does_not_complete()
    {
        var flow = At(OnboardingStep.SayHello);

        flow.Observe(new TutorialTalk(false, "hello", string.Empty, 0, false, "No API key saved."));

        Assert.False(flow.IsStepDone);
        Assert.Equal(FeedbackKind.Problem, flow.FeedbackKind);
        Assert.Contains("No API key saved.", flow.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public void Watch_it_draw_needs_a_mark()
    {
        var flow = At(OnboardingStep.WatchItDraw);

        flow.Observe(new TutorialTalk(true, "where is the star", "Top left.", 0, false));
        Assert.False(flow.IsStepDone);
        Assert.Equal(FeedbackKind.Hint, flow.FeedbackKind);

        flow.Observe(new TutorialTalk(true, "point at the blue star", "There.", 1, false));
        Assert.True(flow.IsStepDone);

        // A later answer without marks keeps the success.
        flow.Observe(new TutorialTalk(true, "thanks", "Sure.", 0, false));
        Assert.Equal(FeedbackKind.Success, flow.FeedbackKind);
    }

    [Fact]
    public void Circle_step_needs_a_circle()
    {
        var flow = At(OnboardingStep.CircleSomething);

        flow.Observe(new TutorialTalk(true, "what's this", "A heart.", 1, Circled: false));
        Assert.False(flow.IsStepDone);
        Assert.Equal(FeedbackKind.Hint, flow.FeedbackKind);

        flow.Observe(new TutorialTalk(true, "what's this", "A red heart.", 0, Circled: true));
        Assert.True(flow.IsStepDone);
    }

    [Fact]
    public void Dictation_completes_when_text_arrives_and_moving_on_clears_feedback()
    {
        var flow = At(OnboardingStep.Dictate);
        var changes = 0;
        flow.Changed += (_, _) => changes++;

        flow.ObserveDictation("   ");
        Assert.False(flow.IsStepDone);

        flow.ObserveDictation("Hello from my voice.");
        flow.ObserveDictation("Hello from my voice. More.");
        Assert.True(flow.IsStepDone);
        Assert.Equal(1, changes);

        flow.Next();
        Assert.Equal(OnboardingStep.Finish, flow.Step);
        Assert.Equal(FeedbackKind.None, flow.FeedbackKind);
        Assert.Equal(string.Empty, flow.Feedback);

        flow.Back();
        Assert.True(flow.IsStepDone);
    }

    [Fact]
    public void Tutorial_talk_reads_a_circle_from_the_ink_trail()
    {
        var circling = new TrailScript()
            .MoveTo(100, 100)
            .LineTo(350, 300, TimeSpan.FromMilliseconds(200))
            .Circle(400, 300, 50, turns: 1.08, TimeSpan.FromMilliseconds(900))
            .Build();
        var straight = new TrailScript().MoveTo(100, 100).LineTo(700, 300, TimeSpan.FromMilliseconds(400)).Build();
        var capture = new CaptureResult([], OneToOne, null, TimeSpan.Zero);
        var answered = new TalkResult(TalkOutcome.Answered, " It's a heart. ", 1);

        var circled = TutorialTalk.From(new TalkQuestion(" what's this ", RequestSource.Voice, capture, Ink: circling), answered);
        var moved = TutorialTalk.From(new TalkQuestion("what's this", RequestSource.Voice, capture, Ink: straight), answered);
        var noCapture = TutorialTalk.From(new TalkQuestion("what's this", RequestSource.Voice, null, Ink: circling), answered);

        Assert.True(circled.Circled);
        Assert.Equal("what's this", circled.Heard);
        Assert.Equal("It's a heart.", circled.Said);
        Assert.Equal(1, circled.Marks);
        Assert.False(moved.Circled);
        Assert.False(noCapture.Circled);
    }

    [Fact]
    public void Tutorial_talk_carries_the_failure_reason()
    {
        var failed = TutorialTalk.From(new TalkQuestion("hi", RequestSource.Voice), new TalkResult(TalkOutcome.Failed, string.Empty, 0, Problem: "Claude Code isn't signed in."));
        var cancelled = TutorialTalk.From(new TalkQuestion("hi", RequestSource.Voice), new TalkResult(TalkOutcome.Cancelled, string.Empty, 0));

        Assert.False(failed.Answered);
        Assert.Equal("Claude Code isn't signed in.", failed.Problem);
        Assert.False(cancelled.Answered);
        Assert.Null(cancelled.Problem);
    }

    [Theory]
    [InlineData("{ }", true)]
    [InlineData("{ \"onboarding\": null }", true)]
    [InlineData("{ \"onboarding\": { \"completed\": true, \"tutorialVersion\": 1 } }", false)]
    [InlineData("{ \"onboarding\": { \"completed\": true } }", true)]
    [InlineData("{ \"onboarding\": { \"completed\": true, \"tutorialVersion\": 0 } }", true)]
    [InlineData("{ \"onboarding\": { \"completed\": false, \"tutorialVersion\": 1 } }", true)]
    public void Onboarding_shows_until_the_current_tutorial_was_finished(string json, bool shows)
    {
        Assert.Equal(shows, Load(json).Onboarding.ShouldShow);
    }

    [Fact]
    public async Task Finishing_onboarding_persists()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        using (var store = JsonSettingsStore.Open(path, NullLogger<JsonSettingsStore>.Instance))
        {
            Assert.True(store.Current.Onboarding.ShouldShow);
            await store.UpdateAsync(s => s with { Onboarding = OnboardingSettings.Done });
        }

        using var reopened = JsonSettingsStore.Open(path, NullLogger<JsonSettingsStore>.Instance);
        Assert.False(reopened.Current.Onboarding.ShouldShow);
        Assert.Equal(OnboardingSettings.CurrentTutorialVersion, reopened.Current.Onboarding.TutorialVersion);
    }

    private static OnboardingFlow At(OnboardingStep step)
    {
        var flow = new OnboardingFlow();
        flow.GoTo(step);
        return flow;
    }

    private ClickySettings Load(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        using var store = JsonSettingsStore.Open(path, NullLogger<JsonSettingsStore>.Instance);
        return store.Current;
    }
}
