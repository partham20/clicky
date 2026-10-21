namespace Clicky.Core.Assistant;

/// <summary>What happened to one action or tool call of the previous reply, sent back to the model.</summary>
/// <param name="ToolCallId">The tool_use it answers (API backend); null for an action tag.</param>
/// <param name="Name">The action or tool name, e.g. "click".</param>
/// <param name="Text">What happened, for the model: "Clicked "Save" (UI Automation).", "The user said not now.", a connector's output…</param>
/// <param name="IsError">The action failed or was refused.</param>
public sealed record ToolOutcome(string? ToolCallId, string Name, string Text, bool IsError = false);

/// <summary>
/// A computer-use agent step: the results of the previous reply's actions, sent with a fresh
/// screenshot so the model can see what they did and carry on.
/// </summary>
/// <param name="Number">1 for the first step after the user's request.</param>
/// <param name="MaxSteps">The step budget, so the model can wrap up in time.</param>
public sealed record AgentStep(int Number, int MaxSteps, IReadOnlyList<ToolOutcome> Results);

/// <summary>Tool results for calls that nobody ran (the user moved on, or the reply only showed things).</summary>
public static class DefaultToolResults
{
    public const string Shown = "Shown to the user.";

    public const string NotRun = "Not done: Clicky did not run this (the user moved on). Ask before trying again.";

    /// <summary>The result for <paramref name="call"/> when no step answered it.</summary>
    public static ToolOutcome For(ToolCallRecord call)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!call.Accepted)
        {
            return new ToolOutcome(call.Id, call.Name, $"Rejected: {call.Error ?? "invalid input"}. Nothing was done.", IsError: true);
        }

        return call.Name is "point" or "highlight" or "draw" or "step" or "wait" or "done"
            ? new ToolOutcome(call.Id, call.Name, Shown)
            : new ToolOutcome(call.Id, call.Name, NotRun, IsError: true);
    }

    /// <summary>One result per pending call, in call order: from <paramref name="results"/> by id, else the default.</summary>
    public static IReadOnlyList<ToolOutcome> Resolve(IReadOnlyList<ToolCallRecord> pending, IReadOnlyList<ToolOutcome>? results)
    {
        if (pending.Count == 0)
        {
            return [];
        }

        var list = new List<ToolOutcome>(pending.Count);
        foreach (var call in pending)
        {
            list.Add(results?.FirstOrDefault(r => r.ToolCallId == call.Id) ?? For(call));
        }

        return list;
    }
}
