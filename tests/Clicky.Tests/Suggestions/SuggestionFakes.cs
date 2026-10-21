using System.Text.Json;
using Clicky.Core.Assistant;
using Clicky.Core.Connectors;
using Clicky.Core.Memory;
using Clicky.Core.Suggestions;

namespace Clicky.Tests.Suggestions;

/// <summary>Answers each request with the next scripted reply for its tier; records what was asked.</summary>
internal sealed class ScriptedModel : ISuggestionModel
{
    private readonly Queue<Func<string, string>> _fast = new();
    private readonly Queue<Func<string, string>> _deep = new();

    public List<(string System, string User, ModelTier Tier)> Requests { get; } = [];

    public List<string> Searches { get; } = [];

    public Func<string, string> SearchReply { get; set; } = q => $"- Result for {q} https://example.com/{q.Length}";

    public Exception? Failure { get; set; }

    public ScriptedModel Fast(string reply) => Fast(_ => reply);

    public ScriptedModel Fast(Func<string, string> reply)
    {
        _fast.Enqueue(reply);
        return this;
    }

    public ScriptedModel Deep(string reply) => Deep(_ => reply);

    public ScriptedModel Deep(Func<string, string> reply)
    {
        _deep.Enqueue(reply);
        return this;
    }

    public Task<string> CompleteAsync(string system, string user, ModelTier tier, CancellationToken cancellationToken)
    {
        Requests.Add((system, user, tier));
        if (Failure is { } failure)
        {
            throw failure;
        }

        var queue = tier == ModelTier.Fast ? _fast : _deep;
        return Task.FromResult(queue.Count > 0 ? queue.Dequeue()(user) : tier == ModelTier.Fast ? "<findings></findings>" : """{"ideas": []}""");
    }

    public Task<string> SearchWebAsync(string query, CancellationToken cancellationToken)
    {
        Searches.Add(query);
        return Task.FromResult(SearchReply(query));
    }
}

internal sealed class FakeConnectorTools(params ConnectorTool[] tools) : IConnectorTools
{
    public event EventHandler? ToolsChanged;

    public IReadOnlyList<ConnectorTool> Tools => tools;

    public List<(string Name, string Arguments)> Calls { get; } = [];

    public Func<string, JsonElement, ConnectorToolResult> Reply { get; set; } = (name, _) => new(false, [new ConnectorContent.Text($"output of {name}")]);

    public Task<IReadOnlyList<ConnectorTool>> GetToolsAsync(TimeSpan maxWait, CancellationToken cancellationToken = default) => Task.FromResult(Tools);

    public bool IsConnectorTool(string toolName) => tools.Any(t => t.Name == toolName);

    public Task<ConnectorToolResult> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, arguments.GetRawText()));
        return Task.FromResult(Reply(toolName, arguments));
    }

    public void RaiseChanged() => ToolsChanged?.Invoke(this, EventArgs.Empty);

    public static ConnectorTool Tool(string name, bool readOnly, string schema = """{"type":"object","properties":{"query":{"type":"string","description":"What to look for"}},"required":["query"]}""", string connector = "Gmail", string account = "Work")
    {
        using var document = JsonDocument.Parse(schema);
        return new ConnectorTool(name, $"{connector} ({account}): {name}", document.RootElement.Clone(), connector.ToLowerInvariant(), connector, account.ToLowerInvariant(), account, name, null, readOnly, !readOnly, false);
    }
}

internal sealed class FakeMemoryService(string prompt = "") : IMemoryService
{
    public event EventHandler<MemoryUpdatedEventArgs>? Updated;

    public Task<string> GetPromptAsync(CancellationToken cancellationToken = default) => Task.FromResult(prompt);

    public void RecordTurn(MemoryTurn turn)
    {
    }

    public Task EndConversationAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void RaiseUpdated(MergeResult result) => Updated?.Invoke(this, new MemoryUpdatedEventArgs(result, false));
}
