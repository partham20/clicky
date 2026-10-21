using System.Globalization;
using System.Text;
using System.Text.Json;
using Clicky.Core.Assistant;
using Clicky.Core.Connectors;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Suggestions;

/// <param name="Findings">The researcher's notes, one line per item with its source; empty if it found nothing.</param>
/// <param name="ToolCalls">Connector tool calls made.</param>
/// <param name="WebSearches">Web searches made.</param>
public sealed record ResearchOutcome(string Findings, int ToolCalls, int WebSearches, int Rounds);

/// <summary>
/// The daily read-only research pass: the fast model looks through the user's connected apps for
/// the last settings.suggestions.lookbackHours (and the web, if allowed) and writes notes for the
/// editor. Only tools the server marks read-only are offered, and every call is checked against
/// that list again before it runs, so this pass can never send, change or delete anything.
/// </summary>
public sealed class SuggestionResearcher(
    ISuggestionModel model,
    IConnectorTools connectors,
    ISettingsStore settings,
    TimeProvider time,
    ILogger<SuggestionResearcher> logger)
{
    public const int MaxRounds = 6;
    public const int MaxCallsPerRound = 6;
    public const int MaxWebSearches = 4;
    public const int MaxResultChars = 6_000;

    /// <summary>Past this, the next round is the last.</summary>
    public const int MaxTranscriptChars = 90_000;

    /// <summary>More tools than this and the prompt gets too long to be useful; the rest are left out.</summary>
    public const int MaxTools = 60;

    internal const string WebSearchTool = "web_search";

    private const string WebUnavailable = "Web search isn't available";

    /// <summary>How long to wait for connectors still connecting at startup.</summary>
    public static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20);

    /// <summary>Researches now. Null when there is nothing to research with (no read-only connector tools and no web).</summary>
    /// <param name="memory">What Clicky remembers about the user (the memory prompt block), to know what matters to them.</param>
    /// <exception cref="ClaudeBackendException">The model failed.</exception>
    public async Task<ResearchOutcome?> ResearchAsync(string memory, CancellationToken cancellationToken)
    {
        var options = settings.Current.Suggestions;
        var tools = (await connectors.GetToolsAsync(ConnectWait, cancellationToken).ConfigureAwait(false))
            .Where(t => t.ReadOnly)
            .Take(MaxTools)
            .ToList();
        var web = options.SearchWeb;
        if (tools.Count == 0 && !web)
        {
            logger.LogInformation("Suggestions: no read-only connector tools and web search is off, so nothing to research");
            return null;
        }

        var allowed = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var now = time.GetLocalNow();
        var system = SystemPrompt(now, options.LookbackHours, tools, web);
        var transcript = new StringBuilder(Opening(now, memory, tools));
        var calls = 0;
        var searches = 0;
        logger.LogInformation("Suggestions: researching with {Tools} read-only tools{Web}", tools.Count, web ? " and the web" : string.Empty);

        for (var round = 1; round <= MaxRounds; round++)
        {
            var last = round == MaxRounds || transcript.Length > MaxTranscriptChars;
            transcript.Append(last
                ? "\n\nThis is the last round: write your <findings> now, with no more calls."
                : $"\n\nWrite your calls, or your <findings> if you have enough. (Round {round} of {MaxRounds}.)");

            var reply = await model.CompleteAsync(system, transcript.ToString(), ModelTier.Fast, cancellationToken).ConfigureAwait(false);
            var turn = ResearchProtocol.Parse(reply);
            if (turn.Findings is not null || last)
            {
                var notes = turn.Findings ?? string.Empty;
                logger.LogInformation(
                    "Suggestions: research done in {Rounds} rounds, {Calls} tool calls, {Searches} web searches, {Lines} notes",
                    round,
                    calls,
                    searches,
                    notes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
                return new ResearchOutcome(notes, calls, searches, round);
            }

            transcript.Append(CultureInfo.InvariantCulture, $"\n\nRound {round}, you wrote:\n");
            foreach (var call in turn.Calls)
            {
                transcript.Append(CultureInfo.InvariantCulture, $"<call tool=\"{call.Tool}\">{call.RawArguments}</call>\n");
            }

            var batch = turn.Calls.Take(MaxCallsPerRound).ToList();
            var results = new string[batch.Count];
            var running = new List<Task<(int Index, string Text, bool IsError)>>();
            for (var i = 0; i < batch.Count; i++)
            {
                var call = batch[i];
                if (call.Tool == WebSearchTool)
                {
                    // Searches run one after another so the limit holds.
                    var (text, isError, ran) = await SearchAsync(call, web && searches < MaxWebSearches, cancellationToken).ConfigureAwait(false);
                    searches += ran ? 1 : 0;
                    web &= !(isError && text.StartsWith(WebUnavailable, StringComparison.Ordinal));
                    results[i] = ResearchProtocol.Result(i + 1, call.Tool, text, isError, MaxResultChars);
                }
                else
                {
                    calls++;
                    running.Add(CallAsync(i, call, allowed, cancellationToken));
                }
            }

            foreach (var (index, text, isError) in await Task.WhenAll(running).ConfigureAwait(false))
            {
                results[index] = ResearchProtocol.Result(index + 1, batch[index].Tool, text, isError, MaxResultChars);
            }

            transcript.Append(CultureInfo.InvariantCulture, $"\nRound {round} results:\n");
            foreach (var result in results)
            {
                transcript.Append(result).Append('\n');
            }

            if (turn.Calls.Count > batch.Count)
            {
                transcript.Append(CultureInfo.InvariantCulture, $"(Only the first {MaxCallsPerRound} calls ran.)\n");
            }
        }

        return new ResearchOutcome(string.Empty, calls, searches, MaxRounds);
    }

    private async Task<(int Index, string Text, bool IsError)> CallAsync(int index, ResearchCall call, Dictionary<string, ConnectorTool> allowed, CancellationToken cancellationToken)
    {
        if (!allowed.TryGetValue(call.Tool, out var tool) || !tool.ReadOnly)
        {
            logger.LogWarning("Suggestions: the researcher asked for {Tool}, which is not one of its read-only tools; refused", call.Tool);
            return (index, $"\"{call.Tool}\" is not one of your tools. Use only the tools listed.", true);
        }

        if (call.Arguments is not { } arguments)
        {
            return (index, "The arguments were not a JSON object.", true);
        }

        var started = time.GetTimestamp();
        var result = await connectors.CallAsync(tool.Name, arguments, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Suggestions: {Tool} ({Account}) in {Ms:0} ms{Error}",
            tool.Name,
            tool.AccountLabel,
            time.GetElapsedTime(started).TotalMilliseconds,
            result.IsError ? ", error" : string.Empty);
        return (index, result.ToText(), result.IsError);
    }

    private async Task<(string Text, bool IsError, bool Ran)> SearchAsync(ResearchCall call, bool allowed, CancellationToken cancellationToken)
    {
        if (!allowed)
        {
            return ($"{WebUnavailable} any more today.", true, false);
        }

        var query = call.Arguments is { } args && args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() : null;
        if (string.IsNullOrWhiteSpace(query))
        {
            return ("web_search needs {\"query\": \"...\"}.", true, false);
        }

        try
        {
            var started = time.GetTimestamp();
            var text = await model.SearchWebAsync(query, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Suggestions: web search in {Ms:0} ms", time.GetElapsedTime(started).TotalMilliseconds);
            return (text.Length == 0 ? "No results." : text, false, true);
        }
        catch (ClaudeBackendException ex) when (ex.Failure is ClaudeFailure.NotConfigured or ClaudeFailure.Unauthorized or ClaudeFailure.BadRequest)
        {
            logger.LogInformation("Suggestions: web search unavailable ({Failure}: {Message})", ex.Failure, ex.Message);
            return ($"{WebUnavailable}: {ex.Message}", true, true);
        }
        catch (ClaudeBackendException ex)
        {
            logger.LogInformation("Suggestions: web search failed ({Failure}: {Message})", ex.Failure, ex.Message);
            return ($"The search failed: {ex.Message}", true, true);
        }
    }

    internal static string SystemPrompt(DateTimeOffset now, int hours, IReadOnlyList<ConnectorTool> tools, bool web)
    {
        var list = new StringBuilder();
        foreach (var tool in tools)
        {
            list.Append("- ").Append(tool.Name).Append(": ").Append(Shorten(tool.Description, 300)).Append('\n');
            var inputs = Inputs(tool.InputSchema);
            list.Append("  input: ").Append(inputs.Length == 0 ? "{}" : inputs).Append('\n');
        }

        if (web)
        {
            list.Append("- ").Append(WebSearchTool).Append(": Searches the web and returns recent results with URLs. ")
                .Append(CultureInfo.InvariantCulture, $"At most {MaxWebSearches} searches in all.\n")
                .Append("  input: query (string, required)\n");
        }

        return PromptLibrary.Read("suggestions-research.md")
            .Replace("{{now}}", now.ToString("dddd d MMMM yyyy, HH:mm (zzz)", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{since}}", now.AddHours(-hours).ToString("dddd d MMMM, HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{hours}}", hours.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{max_calls}}", MaxCallsPerRound.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{rounds}}", MaxRounds.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{tools}}", list.Length == 0 ? "(none)" : list.ToString().TrimEnd(), StringComparison.Ordinal);
    }

    private static string Opening(DateTimeOffset now, string memory, IReadOnlyList<ConnectorTool> tools)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"Good morning. It is {now:dddd d MMMM, HH:mm}. Research for today's suggestions.\n");
        var apps = tools.Select(t => string.IsNullOrWhiteSpace(t.AccountLabel) ? t.ConnectorName : $"{t.ConnectorName} ({t.AccountLabel})")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        text.Append(apps.Count == 0 ? "No apps are connected; use the web only.\n" : $"Connected apps: {string.Join(", ", apps)}.\n");
        if (!string.IsNullOrWhiteSpace(memory))
        {
            text.Append("\nWhat Clicky remembers about the user:\n").Append(memory.Trim()).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>"query (string, required): Gmail search syntax; max_results (integer)".</summary>
    internal static string Inputs(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var required = schema.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
            : [];
        var parts = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            var type = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("type", out var t)
                ? (t.ValueKind == JsonValueKind.String ? t.GetString() : t.ValueKind == JsonValueKind.Array ? string.Join("|", t.EnumerateArray().Select(x => x.ToString())) : null)
                : null;
            var description = property.Value.ValueKind == JsonValueKind.Object && property.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()
                : null;
            var part = new StringBuilder(property.Name);
            var tags = new List<string>();
            if (!string.IsNullOrEmpty(type))
            {
                tags.Add(type);
            }

            if (required.Contains(property.Name))
            {
                tags.Add("required");
            }

            if (tags.Count > 0)
            {
                part.Append(" (").Append(string.Join(", ", tags)).Append(')');
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                part.Append(": ").Append(Shorten(description, 100));
            }

            parts.Add(part.ToString());
        }

        return string.Join("; ", parts);
    }

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : string.Concat(line.AsSpan(0, max - 1).TrimEnd(), "…");
    }
}
