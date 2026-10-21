namespace Clicky.Core.Suggestions;

/// <summary>Where an idea came from: an app (and account) or the web, with the item it is about.</summary>
/// <param name="App">For the card: "Gmail (Work)", "Linear", "Web".</param>
/// <param name="Reference">The item: a message or issue ID, a URL. Used to spot an idea about the same thing again.</param>
public sealed record SuggestionSource(string App, string? Reference = null);

/// <summary>What the user did with an idea. Written to suggestions.json; do not rename.</summary>
public enum SuggestionDecision
{
    /// <summary>Not shown yet, or shown and still on the card.</summary>
    Pending,

    /// <summary>"Do it".</summary>
    Approved,

    /// <summary>"Next": not now. Counts as turned down for this idea.</summary>
    Passed,

    /// <summary>Closed the card on it.</summary>
    Declined,

    /// <summary>Shown, and the card went away without any reaction.</summary>
    Ignored,
}

/// <summary>One idea for the user, as the editor wrote it.</summary>
/// <param name="Title">One line, on the card in bold: "Reply to Priya about the PCB rev".</param>
/// <param name="Detail">Two or three sentences: what exactly, and what "Do it" will do.</param>
/// <param name="Why">Why now, citing what was found.</param>
/// <param name="Task">What Clicky is asked to do on "Do it", as a request in the user's voice.</param>
/// <param name="Topic">A few words naming the subject, for spotting repeats ("priya pcb rev reply").</param>
public sealed record Suggestion(
    string Id,
    DateOnly Day,
    DateTimeOffset CreatedAt,
    string Title,
    string Detail,
    string Why,
    string Task,
    string Topic,
    IReadOnlyList<SuggestionSource> Sources)
{
    public SuggestionDecision Decision { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>What the user asked to change with "Adjust", newest last.</summary>
    public IReadOnlyList<string> Adjustments { get; init; } = [];

    /// <summary>"From Gmail (Work) and Linear".</summary>
    public string SourceLine
    {
        get
        {
            var apps = Sources.Select(s => s.App).Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return apps.Count switch
            {
                0 => string.Empty,
                1 => $"From {apps[0]}",
                _ => $"From {string.Join(", ", apps.Take(apps.Count - 1))} and {apps[^1]}",
            };
        }
    }
}

/// <summary>One morning: whether research ran, whether the card was shown and whether the user reacted to it.</summary>
/// <param name="ShownAt">When the card first appeared; null if it never did.</param>
/// <param name="Engaged">Pressed anything on the card (Do it, Next, Adjust or close).</param>
/// <param name="Researched">The research and editor passes ran (even if they found nothing), so they don't run again that day.</param>
public sealed record MorningRecord(DateOnly Day, DateTimeOffset? ShownAt = null, bool Engaged = false, bool Researched = false);
