using System.Text.Json;
using Clicky.Core.Connectors;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;

namespace Clicky.Tests.Connectors;

/// <summary>Behaves like Credential Manager: one entry holds at most 1,280 characters.</summary>
internal sealed class LimitedSecretStore : ISecretStore
{
    public const int MaxCharacters = 1280;

    public Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);

    public string? Read(string name)
    {
        lock (Entries)
        {
            return Entries.TryGetValue(name, out var value) ? value : null;
        }
    }

    public void Write(string name, string value)
    {
        if (value.Length > MaxCharacters)
        {
            throw new ArgumentException($"Secrets are limited to {MaxCharacters} characters.", nameof(value));
        }

        lock (Entries)
        {
            Entries[name] = value;
        }
    }

    public void Delete(string name)
    {
        lock (Entries)
        {
            Entries.Remove(name);
        }
    }
}

internal sealed class FakeSessionFactory : IMcpSessionFactory
{
    private int _attempts;

    /// <summary>Default: a session with one read-only "echo" tool.</summary>
    public Func<ConnectorConfig, ConnectorAccount, IOAuthSignIn?, FakeSession> Connect { get; set; } =
        (_, _, _) => new FakeSession([FakeSession.Tool("echo")]);

    public int Attempts => Volatile.Read(ref _attempts);

    public List<FakeSession> Sessions { get; } = [];

    public Task<IMcpSession> ConnectAsync(ConnectorConfig config, ConnectorAccount account, IOAuthSignIn? signIn, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        var session = Connect(config, account, signIn);
        lock (Sessions)
        {
            Sessions.Add(session);
        }

        return Task.FromResult<IMcpSession>(session);
    }
}

internal sealed class FakeSession(IReadOnlyList<McpToolInfo> tools) : IMcpSession
{
    private static readonly JsonElement ObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{"text":{"type":"string"}}}""").RootElement.Clone();
    private readonly TaskCompletionSource<SessionEnd> _end = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public event EventHandler? ToolsChanged;

    public IReadOnlyList<McpToolInfo> ToolList { get; set; } = tools;

    public Func<string, IReadOnlyDictionary<string, JsonElement>, CancellationToken, Task<ConnectorToolResult>> OnCall { get; set; } =
        (name, args, _) => Task.FromResult(new ConnectorToolResult(false, [new ConnectorContent.Text($"{name}:{string.Join(',', args.Select(a => $"{a.Key}={a.Value}"))}")]));

    public bool Disposed { get; private set; }

    public string ServerDescription => "fake 1.0";

    public string? Instructions => null;

    public Task<SessionEnd> Completion => _end.Task;

    public static McpToolInfo Tool(string name, bool readOnly = true, bool? destructive = null) =>
        new(name, null, $"Does {name}.", ObjectSchema, readOnly, destructive, false);

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken) => Task.FromResult(ToolList);

    public Task<ConnectorToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken) =>
        OnCall(name, arguments, cancellationToken);

    public void Drop(string reason, bool needsSignIn = false) => _end.TrySetResult(new SessionEnd(reason, needsSignIn));

    public void ChangeTools(IReadOnlyList<McpToolInfo> tools)
    {
        ToolList = tools;
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _end.TrySetResult(new SessionEnd(null));
        return ValueTask.CompletedTask;
    }
}

internal sealed class RecordingBrowser(Func<Uri, Task>? onOpen = null) : IBrowserLauncher
{
    public List<Uri> Opened { get; } = [];

    public void Open(Uri uri)
    {
        Opened.Add(uri);
        if (onOpen is not null)
        {
            _ = Task.Run(() => onOpen(uri));
        }
    }
}

internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition, string because, int timeoutMs = 10_000, Func<object?>? state = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting: " + because + (state is null ? string.Empty : $" (last: {state()})"));
            }

            await Task.Delay(20);
        }
    }
}
