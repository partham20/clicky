namespace Clicky.Core.Settings;

/// <summary>settings.onboarding: whether the first-run setup and tutorial have been shown.</summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record OnboardingSettings(bool Completed = false, int TutorialVersion = 0)
{
    /// <summary>Bump when the tutorial changes enough to be worth showing again; older versions see it once more.</summary>
    public const int CurrentTutorialVersion = 1;

    /// <summary>Finished or dismissed. False opens onboarding at startup.</summary>
    public bool Completed { get; init; } = Completed;

    /// <summary>The tutorial version last finished or dismissed.</summary>
    public int TutorialVersion { get; init; } = TutorialVersion;

    /// <summary>True when onboarding should open by itself at startup.</summary>
    public bool ShouldShow => !Completed;

    public OnboardingSettings Normalize() =>
        Completed && TutorialVersion < CurrentTutorialVersion ? this with { Completed = false } : this;

    /// <summary>What to save once the user finishes or closes onboarding.</summary>
    public static OnboardingSettings Done { get; } = new(Completed: true, TutorialVersion: CurrentTutorialVersion);
}
