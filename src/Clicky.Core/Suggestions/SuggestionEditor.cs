using System.Globalization;
using System.Text;
using System.Text.Json;
using Clicky.Core.Assistant;
using Clicky.Core.Dictation;
using Clicky.Core.Prompts;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Suggestions;

/// <summary>
/// The editor pass on the deep model: turns the researcher's notes into a few distinct ideas,
/// steered by what the user approved and turned down before, then drops any that repeat an idea
/// from the last settings.suggestions.noRepeatDays (<see cref="RepeatFilter"/>, which doesn't rely
/// on the model having listened). Also rewrites one idea when the user presses "Adjust".
/// </summary>
public sealed class SuggestionEditor(ISuggestionModel model, TimeProvider time, ILogger<SuggestionEditor> logger)
{
    public const int MaxTitleChars = 120;
    public const int MaxDetailChars = 600;

    /// <summary>How far back the user's reactions are shown to the editor.</summary>
    public const int TasteDays = 45;

    /// <exception cref="ClaudeBackendException">The model failed.</exception>
    public async Task<IReadOnlyList<Suggestion>> PickAsync(
        string findings,
        string memory,
        IReadOnlyList<Suggestion> history,
        int maxIdeas,
        int noRepeatDays,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);
        var now = time.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var recent = history.Where(s => s.Day > today.AddDays(-noRepeatDays)).ToList();
        var system = PromptLibrary.Read("suggestions-editor.md").Replace("{{max_ideas}}", maxIdeas.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        var user = EditorInput(findings, memory, recent, history.Where(s => s.Day > today.AddDays(-TasteDays)).ToList(), maxIdeas, now);

        var reply = await model.CompleteAsync(system, user, ModelTier.Deep, cancellationToken).ConfigureAwait(false);
        var candidates = SuggestionJson.ParseIdeas(reply).Select(idea => ToSuggestion(idea, today, now)).ToList();
        var kept = RepeatFilter.KeepNew(candidates, recent).Take(maxIdeas).ToList();
        if (candidates.Count > kept.Count)
        {
            logger.LogInformation("Suggestions: the editor wrote {Candidates} ideas; {Dropped} repeated earlier ones or each other", candidates.Count, candidates.Count - kept.Count);
        }

        return kept;
    }

    /// <summary>The idea rewritten as the user asked; null if the model's answer couldn't be used.</summary>
    /// <exception cref="ClaudeBackendException">The model failed.</exception>
    public async Task<Suggestion?> ReviseAsync(Suggestion suggestion, string request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        var current = JsonSerializer.Serialize(
            new IdeaJson(suggestion.Title, suggestion.Detail, suggestion.Why, suggestion.Task, suggestion.Topic, [.. suggestion.Sources.Select(x => new SourceJson(x.App, x.Reference))]),
            SuggestionIdeaJsonContext.Default.IdeaJson);
        var user = $"The suggestion:\n{current}\n\nThe user asked: {request.Trim()}";
        var reply = await model.CompleteAsync(PromptLibrary.Read("suggestions-revise.md"), user, ModelTier.Deep, cancellationToken).ConfigureAwait(false);
        if (SuggestionJson.ParseIdea(reply) is not { } idea)
        {
            logger.LogWarning("Suggestions: the revised idea was not valid JSON");
            return null;
        }

        var revised = ToSuggestion(idea, suggestion.Day, time.GetLocalNow());
        return suggestion with
        {
            Title = revised.Title,
            Detail = revised.Detail,
            Why = revised.Why.Length > 0 ? revised.Why : suggestion.Why,
            Task = revised.Task,
            Topic = revised.Topic.Length > 0 ? revised.Topic : suggestion.Topic,
            Sources = revised.Sources.Count > 0 ? revised.Sources : suggestion.Sources,
            Adjustments = [.. suggestion.Adjustments, request.Trim()],
        };
    }

    internal static string EditorInput(string findings, string memory, IReadOnlyList<Suggestion> recent, IReadOnlyList<Suggestion> taste, int maxIdeas, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"It is {now:dddd d MMMM yyyy, HH:mm}.\n\n");
        text.Append("The researcher's notes:\n").Append(string.IsNullOrWhiteSpace(findings) ? "(nothing found)" : findings.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(memory))
        {
            text.Append("What Clicky remembers about the user:\n").Append(memory.Trim()).Append("\n\n");
        }

        text.Append("Already shown recently (never suggest these again, however worded):\n");
        if (recent.Count == 0)
        {
            text.Append("(none)\n");
        }

        foreach (var s in recent.OrderByDescending(s => s.CreatedAt))
        {
            text.Append(CultureInfo.InvariantCulture, $"- {s.Day:ddd d MMM}: {s.Title} (topic: {s.Topic})\n");
        }

        var approved = taste.Where(s => s.Decision == SuggestionDecision.Approved).ToList();
        var turnedDown = taste.Where(s => s.Decision is SuggestionDecision.Declined or SuggestionDecision.Passed).ToList();
        var adjusted = taste.Where(s => s.Adjustments.Count > 0).ToList();
        if (approved.Count + turnedDown.Count + adjusted.Count > 0)
        {
            text.Append("\nHow the user reacted to earlier ideas:\n");
            foreach (var s in approved)
            {
                text.Append("- Did it: ").Append(s.Title).Append('\n');
            }

            foreach (var s in turnedDown)
            {
                text.Append(s.Decision == SuggestionDecision.Declined ? "- Closed it: " : "- Skipped it: ").Append(s.Title).Append('\n');
            }

            foreach (var s in adjusted)
            {
                text.Append("- Asked to change \"").Append(s.Title).Append("\": ").Append(string.Join("; ", s.Adjustments)).Append('\n');
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"\nPick at most {maxIdeas} ideas for this morning.");
        return text.ToString();
    }

    private static Suggestion ToSuggestion(IdeaJson idea, DateOnly day, DateTimeOffset now) => new(
        Guid.NewGuid().ToString("N")[..12],
        day,
        now,
        Clean(idea.Title, MaxTitleChars),
        Clean(idea.Detail, MaxDetailChars),
        Clean(idea.Why, MaxDetailChars),
        string.IsNullOrWhiteSpace(idea.Task) ? Clean(idea.Title, MaxTitleChars) : idea.Task!.Trim(),
        Clean(idea.Topic, MaxTitleChars).ToLowerInvariant(),
        [.. (idea.Sources ?? []).Where(s => !string.IsNullOrWhiteSpace(s.App)).Select(s => new SuggestionSource(s.App.Trim(), string.IsNullOrWhiteSpace(s.Reference) ? null : s.Reference.Trim()))]);

    private static string Clean(string? text, int max)
    {
        var line = DictationText.RemoveDashes((text ?? string.Empty).Trim());
        return line.Length <= max ? line : string.Concat(line.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}
