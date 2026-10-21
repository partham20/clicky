namespace Clicky.Core.Walkthroughs;

/// <summary>Walkthrough tuning. The defaults are what ships; tests shorten the times.</summary>
public sealed record WalkthroughOptions
{
    /// <summary>The most steps a walkthrough can have (build plan: 15).</summary>
    public int MaxSteps { get; init; } = 15;

    /// <summary>A click this close to the step's spot counts, in DIPs of the monitor it is on.</summary>
    public double ClickToleranceDip { get; init; } = 24;

    /// <summary>
    /// The same in screenshot pixels: a model reading a downscaled image is only this accurate, so on a
    /// big monitor this is the wider of the two.
    /// </summary>
    public double ClickToleranceScreenshotPx { get; init; } = 10;

    /// <summary>
    /// An element counts as "the thing at the spot" only if it is at most this share of the monitor in
    /// each direction; focus moving into a whole pane that happens to contain the spot doesn't.
    /// </summary>
    public double MaxElementShare { get; init; } = 0.4;

    /// <summary>Clicks away from the spot before Clicky takes a new look at the screen.</summary>
    public int StrayClicksBeforeReplan { get; init; } = 3;

    /// <summary>New looks at the screen per step because the user went another way; then it just waits.</summary>
    public int MaxReplansPerStep { get; init; } = 2;

    /// <summary>After the step is done, let the app react (menus open, dialogs appear) before the screenshot.</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>…and wait until the user has stopped clicking for this long (a double-click, a quick second click).</summary>
    public TimeSpan QuietAfterInput { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// When the user asked something in between, wait this long after the buddy goes quiet before
    /// asking for the next step, so a question still being transcribed gets in first.
    /// </summary>
    public TimeSpan AfterQuestion { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// The longest a question in between holds the walkthrough up, in case its reply never finishes
    /// (cut off) or the buddy never goes quiet.
    /// </summary>
    public TimeSpan UserBusyTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Longest settle, however busy the user is.</summary>
    public TimeSpan MaxSettle { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>A walkthrough with no progress for this long ends, so the hooks don't stay in forever.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    public static WalkthroughOptions Default { get; } = new();
}
