using System.Text.RegularExpressions;
using Clicky.Core.Settings;

namespace Clicky.Core.Assistant;

/// <param name="Reason">Short, for the log: "agent", "long question", "asks to explain", …</param>
public readonly record struct RouteDecision(ModelTier Tier, string Reason);

/// <summary>
/// Picks the fast or deep model for a question. Rules, not a model call: a classifier request would
/// add a round trip to every voice question, which is the latency the fast model exists to avoid.
/// Quick spoken questions ("where's the share button?") go fast; asking for an explanation, a
/// walkthrough, a fix, writing, long typed questions and agent work go deep.
/// </summary>
public static partial class ModelRouter
{
    /// <summary>A question this long is rarely a quick one.</summary>
    public const int LongQuestionWords = 40;

    public static RouteDecision Route(ClaudeRequest request, ClaudeSettings settings)
    {
        if (request.Tier is { } forced)
        {
            return new(forced, "requested");
        }

        switch (settings.Routing)
        {
            case ModelRouting.AlwaysFast:
                return new(ModelTier.Fast, "always fast");
            case ModelRouting.AlwaysDeep:
                return new(ModelTier.Deep, "always deep");
        }

        if (request.Source == RequestSource.Agent)
        {
            return new(ModelTier.Deep, "agent");
        }

        // The next step has to land on the right control in a screen that just changed.
        if (request.Source == RequestSource.Walkthrough)
        {
            return new(ModelTier.Deep, "walkthrough");
        }

        var text = request.Text ?? string.Empty;
        var words = WordPattern().Count(text);
        if (words >= LongQuestionWords)
        {
            return new(ModelTier.Deep, "long question");
        }

        if (DeepPattern().Match(text) is { Success: true } match)
        {
            return new(ModelTier.Deep, $"asks to {match.Value.ToLowerInvariant()}");
        }

        return new(ModelTier.Fast, request.Source == RequestSource.Typed ? "short typed question" : "short question");
    }

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordPattern();

    // Whole words only: "why" but not "whyever"; "fix" but not "prefix".
    [GeneratedRegex(
        @"\b(explain\w*|why|teach|walk me through|step[- ]by[- ]step|in detail|go deeper|think (harder|carefully)|" +
        @"debug\w*|fix\w*|troubleshoot\w*|compar\w+|difference between|analy[sz]\w*|summari[sz]\w*|review\w*|" +
        @"rewrite|write|draft|plan|proofread|translate)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeepPattern();
}
