using System.Text.Json;
using System.Text.RegularExpressions;

namespace Clicky.Core.Suggestions;

/// <summary>One tool call the researcher wrote: <c>&lt;call tool="name"&gt;{json}&lt;/call&gt;</c>.</summary>
/// <param name="Arguments">The arguments, or null if they weren't a JSON object.</param>
public sealed record ResearchCall(string Tool, JsonElement? Arguments, string RawArguments);

/// <param name="Findings">The notes, when the researcher finished (possibly empty); null while it still wants calls.</param>
public sealed record ResearchTurn(IReadOnlyList<ResearchCall> Calls, string? Findings);

/// <summary>
/// The researcher's text protocol. Tool calls are written as tags rather than native tool use so
/// the same loop runs on both backends (Claude Code takes no custom tools), the way actions do.
/// </summary>
public static partial class ResearchProtocol
{
    public static ResearchTurn Parse(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        // Anything after the calls is the model imagining their results.
        var imagined = reply.IndexOf("<result", StringComparison.Ordinal);
        if (imagined >= 0)
        {
            reply = reply[..imagined];
        }

        if (FindingsPattern().Match(reply) is { Success: true } findings)
        {
            return new ResearchTurn([], findings.Groups["body"].Value.Trim());
        }

        var calls = new List<ResearchCall>();
        foreach (Match match in CallPattern().Matches(reply))
        {
            var raw = match.Groups["args"].Value.Trim();
            calls.Add(new ResearchCall(match.Groups["tool"].Value.Trim(), ParseObject(raw.Length == 0 ? "{}" : raw), raw));
        }

        // Neither calls nor findings: take the text as the notes rather than lose the morning.
        return calls.Count > 0 ? new ResearchTurn(calls, null) : new ResearchTurn([], reply.Trim());
    }

    /// <summary>A result block for the next round, cut to <paramref name="maxChars"/>.</summary>
    public static string Result(int number, string tool, string text, bool isError, int maxChars)
    {
        var body = text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), $"\n[... cut, {text.Length - maxChars} more characters]");
        var error = isError ? " error=\"true\"" : string.Empty;
        return $"<result call=\"{number}\" tool=\"{tool}\"{error}>\n{body.Trim()}\n</result>";
    }

    private static JsonElement? ParseObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<call\s+tool\s*=\s*[""'](?<tool>[^""']+)[""']\s*>(?<args>.*?)</call>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CallPattern();

    // An unclosed tag still counts: the reply may have hit its length limit.
    [GeneratedRegex(@"<findings>(?<body>.*?)(</findings>|\z)", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FindingsPattern();
}
