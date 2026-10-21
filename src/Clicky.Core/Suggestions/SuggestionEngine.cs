using Clicky.Core.Assistant;
using Clicky.Core.Memory;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Suggestions;

/// <summary>
/// Proactive suggestions (M17): once a day the read-only research pass and the editor pass pick a
/// few ideas; the morning card shows them one at a time; what the user does with each is
/// remembered so tomorrow's editor knows their taste and never repeats an idea. The app calls
/// <see cref="TickAsync"/> about once a minute and shows the card when it returns true.
/// </summary>
/// <remarks>Thread-safe. Research runs inside <see cref="TickAsync"/> or <see cref="FindNowAsync"/>, one at a time.</remarks>
public sealed class SuggestionEngine(
    SuggestionStore store,
    SuggestionResearcher researcher,
    SuggestionEditor editor,
    IMemoryService memory,
    ISettingsStore settings,
    TimeProvider time,
    ILogger<SuggestionEngine> logger) : IDisposable
{
    /// <summary>A failed research pass is tried again after this long, up to <see cref="MaxAttempts"/> times a day.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(20);

    public const int MaxAttempts = 3;

    private readonly SemaphoreSlim _research = new(1, 1);
    private readonly Lock _gate = new();
    private DateOnly _attemptDay;
    private int _attempts;
    private DateTimeOffset _retryAt;

    public DateOnly Today => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    /// <summary>
    /// Researches when it's due; true when the morning card should be shown now (then call
    /// <see cref="MarkShown"/> once it is up). Never throws for model or connector problems.
    /// <paramref name="presence"/> is only called when everything else says show.
    /// </summary>
    public async Task<bool> TickAsync(Func<SuggestionPresence> presence, CancellationToken cancellationToken)
    {
        var options = settings.Current.Suggestions;
        var step = Plan(options, presence);
        if (step == MorningStep.Research && MayAttempt())
        {
            await ResearchAsync(Today, cancellationToken).ConfigureAwait(false);
            step = Plan(options, presence);
        }

        return step == MorningStep.Show;
    }

    /// <summary>
    /// Today's ideas not acted on yet (still on the card, or ignored), for the card and the tray's
    /// "Today's ideas". Researches first if today has not been researched (any time of day).
    /// </summary>
    public async Task<FoundIdeas> FindNowAsync(CancellationToken cancellationToken)
    {
        var day = Today;
        string? problem = null;
        if (store.Morning(day) is not { Researched: true })
        {
            problem = await ResearchAsync(day, cancellationToken).ConfigureAwait(false);
        }

        return new FoundIdeas(Open(day), problem);
    }

    public void Dispose() => _research.Dispose();

    /// <summary>Ideas of <paramref name="day"/> with no decision yet, or ignored on the card; newest batch order.</summary>
    public IReadOnlyList<Suggestion> Open(DateOnly day) =>
        [.. store.ForDay(day).Where(s => s.Decision is SuggestionDecision.Pending or SuggestionDecision.Ignored)];

    /// <summary>The card for <paramref name="day"/> is on screen.</summary>
    public void MarkShown(DateOnly day)
    {
        var morning = store.Morning(day) ?? new MorningRecord(day, Researched: true);
        if (morning.ShownAt is null)
        {
            store.SetMorning(morning with { ShownAt = time.GetLocalNow() });
        }
    }

    /// <summary>Records what the user did with one idea; any reaction counts as engaging with that morning.</summary>
    public Suggestion? Decide(string id, SuggestionDecision decision)
    {
        var decided = store.Update(id, s => s with { Decision = decision, DecidedAt = time.GetLocalNow() });
        if (decided is null)
        {
            return null;
        }

        logger.LogInformation("Suggestions: {Decision} \"{Title}\"", decision, decided.Title);
        if (decision != SuggestionDecision.Ignored)
        {
            Engage(decided.Day);
        }

        return decided;
    }

    /// <summary>The card went away with these ideas still on it and no reaction.</summary>
    public void MarkIgnored(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        foreach (var id in ids)
        {
            store.Update(id, s => s.Decision == SuggestionDecision.Pending ? s with { Decision = SuggestionDecision.Ignored, DecidedAt = time.GetLocalNow() } : s);
        }
    }

    /// <summary>Rewrites an idea as the user asked ("Adjust"). Null if it couldn't be rewritten; the reason is logged.</summary>
    public async Task<Suggestion?> AdjustAsync(string id, string request, CancellationToken cancellationToken)
    {
        var current = store.Suggestions.FirstOrDefault(s => s.Id == id);
        if (current is null)
        {
            return null;
        }

        Engage(current.Day);
        try
        {
            var revised = await editor.ReviseAsync(current, request, cancellationToken).ConfigureAwait(false);
            if (revised is null)
            {
                return null;
            }

            logger.LogInformation("Suggestions: adjusted \"{Old}\" to \"{New}\"", current.Title, revised.Title);
            return store.Update(id, _ => revised with { Decision = SuggestionDecision.Pending, DecidedAt = null });
        }
        catch (ClaudeBackendException ex)
        {
            logger.LogWarning("Suggestions: could not adjust \"{Title}\" ({Failure}: {Message})", current.Title, ex.Failure, ex.Message);
            throw;
        }
    }

    private MorningStep Plan(SuggestionSettings options, Func<SuggestionPresence> presence) =>
        SuggestionPlanner.Next(time.GetLocalNow(), options, store.Mornings, Open(Today).Any(s => s.Decision == SuggestionDecision.Pending), presence);

    private void Engage(DateOnly day)
    {
        var morning = store.Morning(day) ?? new MorningRecord(day, Researched: true);
        if (!morning.Engaged)
        {
            store.SetMorning(morning with { Engaged = true });
        }
    }

    private bool MayAttempt()
    {
        lock (_gate)
        {
            if (_attemptDay != Today)
            {
                _attemptDay = Today;
                _attempts = 0;
                _retryAt = default;
            }

            return _attempts < MaxAttempts && time.GetLocalNow() >= _retryAt;
        }
    }

    /// <returns>Null, or why it failed (for the user).</returns>
    private async Task<string?> ResearchAsync(DateOnly day, CancellationToken cancellationToken)
    {
        await _research.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (store.Morning(day) is { Researched: true })
            {
                return null;
            }

            var options = settings.Current.Suggestions;
            var started = time.GetTimestamp();
            var remembered = await memory.GetPromptAsync(cancellationToken).ConfigureAwait(false);
            var outcome = await researcher.ResearchAsync(remembered, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<Suggestion> ideas = [];
            if (outcome is { Findings.Length: > 0 })
            {
                ideas = await editor.PickAsync(outcome.Findings, remembered, store.Suggestions, options.MaxIdeas, options.NoRepeatDays, cancellationToken).ConfigureAwait(false);
            }

            store.Add(ideas);
            store.SetMorning((store.Morning(day) ?? new MorningRecord(day)) with { Researched = true });
            logger.LogInformation("Suggestions: {Count} ideas for {Day:yyyy-MM-dd} in {Seconds:0} s", ideas.Count, day, time.GetElapsedTime(started).TotalSeconds);
            return outcome is null ? "Connect an app in Settings › Connectors, or turn on web search for suggestions." : null;
        }
        catch (ClaudeBackendException ex)
        {
            int attempts;
            lock (_gate)
            {
                attempts = ++_attempts;
                _retryAt = time.GetLocalNow() + RetryAfter;
            }

            var giveUp = ex.Failure is ClaudeFailure.NotConfigured or ClaudeFailure.Unauthorized || attempts >= MaxAttempts;
            logger.LogWarning(
                "Suggestions: research failed ({Failure}: {Message}); {Next}",
                ex.Failure,
                ex.Message,
                giveUp ? "no more tries today" : $"trying again in {RetryAfter.TotalMinutes:0} min");
            if (giveUp)
            {
                store.SetMorning((store.Morning(day) ?? new MorningRecord(day)) with { Researched = true });
            }

            return ex.Message;
        }
        finally
        {
            _research.Release();
        }
    }
}

/// <param name="Problem">Why research failed or couldn't run, for the user; null if it ran (or had already run today).</param>
public sealed record FoundIdeas(IReadOnlyList<Suggestion> Ideas, string? Problem);
