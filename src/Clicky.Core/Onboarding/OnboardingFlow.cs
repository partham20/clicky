using Clicky.Core.Ink;
using Clicky.Core.Talk;

namespace Clicky.Core.Onboarding;

/// <summary>The pages of first-run onboarding, in order.</summary>
public enum OnboardingStep
{
    Welcome,

    /// <summary>Pick the backend: the Claude subscription (Claude Code) or an API key.</summary>
    ConnectClaude,

    /// <summary>Microphone and speech-to-text (the Whisper model downloads meanwhile).</summary>
    Voice,

    /// <summary>Practice: hold Talk and say hello.</summary>
    SayHello,

    /// <summary>Practice: ask Clicky to point at something and watch it draw.</summary>
    WatchItDraw,

    /// <summary>Practice: circle something with the pointer while asking about it.</summary>
    CircleSomething,

    /// <summary>Practice: dictate a sentence into a text box.</summary>
    Dictate,

    Finish,
}

/// <summary>What the tutorial needs to know about one Talk question and its answer.</summary>
/// <param name="Answered">Claude answered (not cancelled or failed).</param>
/// <param name="Heard">What the user said.</param>
/// <param name="Said">What Clicky said back.</param>
/// <param name="Marks">Point, highlight and draw marks put on screen.</param>
/// <param name="Circled">The pointer drew a loop around something while the user talked.</param>
/// <param name="Problem">Why it failed, for the user.</param>
public sealed record TutorialTalk(bool Answered, string Heard, string Said, int Marks, bool Circled, string? Problem = null)
{
    public static TutorialTalk From(TalkQuestion question, TalkResult result)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(result);

        var circled = question is { Ink: { IsEmpty: false } trail, Capture: { } capture }
            && InkAnalyzer.Analyze(trail, capture.Geometry) is { } ink
            && ink.Gestures.Any(g => g.Kind == InkGestureKind.Circle);

        return new TutorialTalk(
            result.Outcome == TalkOutcome.Answered,
            question.Text.Trim(),
            result.SpokenText.Trim(),
            result.Marks,
            circled,
            result.Outcome == TalkOutcome.Failed ? result.Problem ?? "Claude couldn't answer." : null);
    }
}

/// <summary>How the current practice step is going, for the line under its instructions.</summary>
public enum FeedbackKind
{
    None,

    /// <summary>Close, try again (it answered, but did not draw, or no circle was seen).</summary>
    Hint,

    /// <summary>Something is broken (Claude not connected, no speech heard).</summary>
    Problem,

    Success,
}

/// <summary>
/// First-run onboarding: three setup pages, then a hands-on tutorial (say hello, watch it draw,
/// circle something, dictate) where each practice step completes when the real feature is seen
/// working, then a finish page. Every step can be skipped; nothing here touches settings.
/// </summary>
/// <remarks>Not thread-safe: use from one thread (the UI thread) and marshal observations to it.</remarks>
public sealed class OnboardingFlow
{
    private static readonly OnboardingStep[] Steps = Enum.GetValues<OnboardingStep>();
    private readonly HashSet<OnboardingStep> _done = [];

    public OnboardingStep Step { get; private set; } = OnboardingStep.Welcome;

    public string Feedback { get; private set; } = string.Empty;

    public FeedbackKind FeedbackKind { get; private set; }

    public bool CanGoBack => Step != Steps[0];

    public bool IsLast => Step == Steps[^1];

    /// <summary>The current practice step was done for real (not skipped).</summary>
    public bool IsStepDone => _done.Contains(Step);

    /// <summary>Practice steps done for real, out of <see cref="PracticeCount"/>.</summary>
    public int PracticeDone => _done.Count(IsPractice);

    public static int PracticeCount => Steps.Count(IsPractice);

    /// <summary>1-based position, for "Step 3 of 8".</summary>
    public int Position => Array.IndexOf(Steps, Step) + 1;

    public static int StepCount => Steps.Length;

    /// <summary>Raised after <see cref="Step"/>, <see cref="Feedback"/> or <see cref="IsStepDone"/> changes.</summary>
    public event EventHandler? Changed;

    public static bool IsPractice(OnboardingStep step) =>
        step is OnboardingStep.SayHello or OnboardingStep.WatchItDraw or OnboardingStep.CircleSomething or OnboardingStep.Dictate;

    /// <summary>Practice steps that listen for Talk (the rest wait for typing or a button).</summary>
    public static bool UsesTalk(OnboardingStep step) =>
        step is OnboardingStep.SayHello or OnboardingStep.WatchItDraw or OnboardingStep.CircleSomething;

    public void Next() => GoTo(Math.Min(Array.IndexOf(Steps, Step) + 1, Steps.Length - 1));

    public void Back() => GoTo(Math.Max(Array.IndexOf(Steps, Step) - 1, 0));

    public void GoTo(OnboardingStep step) => GoTo(Array.IndexOf(Steps, step));

    /// <summary>A Talk question was answered (or failed) while onboarding was open.</summary>
    public void Observe(TutorialTalk talk)
    {
        ArgumentNullException.ThrowIfNull(talk);
        if (!UsesTalk(Step))
        {
            return;
        }

        if (!talk.Answered)
        {
            // A failure after the step was done does not undo it, but still says what went wrong.
            Show(talk.Problem is null ? string.Empty : $"Clicky couldn't answer: {talk.Problem}", talk.Problem is null ? FeedbackKind.None : FeedbackKind.Problem);
            return;
        }

        var heard = talk.Heard.Length == 0 ? string.Empty : $"Clicky heard “{Shorten(talk.Heard)}”. ";
        switch (Step)
        {
            case OnboardingStep.SayHello:
                Complete($"{heard}Nice, you're talking to Clicky.");
                break;

            case OnboardingStep.WatchItDraw when talk.Marks > 0:
                Complete($"{heard}That's how Clicky shows you where things are.");
                break;

            case OnboardingStep.WatchItDraw:
                Show($"{heard}It answered but didn't draw. Try asking it to point: “Point at the blue star.”", FeedbackKind.Hint);
                break;

            case OnboardingStep.CircleSomething when talk.Circled:
                Complete($"{heard}Clicky saw what you circled.");
                break;

            case OnboardingStep.CircleSomething:
                Show($"{heard}It didn't see a circle. While you hold the keys, loop the pointer once around the heart, then ask.", FeedbackKind.Hint);
                break;
        }
    }

    /// <summary>The practice box's text changed during the Dictate step.</summary>
    public void ObserveDictation(string text)
    {
        if (Step != OnboardingStep.Dictate || IsStepDone || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Complete("That's dictation. It works in any app with a text box.");
    }

    private static string Shorten(string text)
    {
        var line = text.ReplaceLineEndings(" ");
        return line.Length <= 60 ? line : string.Concat(line.AsSpan(0, 59).TrimEnd(), "…");
    }

    private void Complete(string feedback)
    {
        _done.Add(Step);
        Show(feedback, FeedbackKind.Success);
    }

    private void GoTo(int index)
    {
        if (Steps[index] == Step)
        {
            return;
        }

        Step = Steps[index];
        Feedback = string.Empty;
        FeedbackKind = FeedbackKind.None;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Show(string feedback, FeedbackKind kind)
    {
        // Once done, a later hint ("it didn't draw this time") would only confuse; keep the success.
        if (IsStepDone && kind == FeedbackKind.Hint)
        {
            return;
        }

        Feedback = feedback;
        FeedbackKind = kind;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
