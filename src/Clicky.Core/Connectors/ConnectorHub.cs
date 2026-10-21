using System.Text.Json;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Connectors;

/// <summary>
/// Runs every configured MCP connector: one <see cref="AccountConnection"/> per enabled account,
/// started, restarted and stopped as settings.connectors changes (live, including hand edits).
/// Publishes the merged tool list through <see cref="IConnectorTools"/> and routes calls back.
/// Also the management API behind Settings › Connectors.
/// </summary>
public sealed class ConnectorHub : IConnectorTools, IAsyncDisposable
{
    /// <summary>Tool results longer than this are cut, so one chatty server cannot flood the context.</summary>
    public const int MaxResultCharacters = 100_000;

    /// <summary>How long a browser sign-in may take.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    private static readonly JsonElement EmptyObjectSchema = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private readonly ISettingsStore _settings;
    private readonly IMcpSessionFactory _factory;
    private readonly IBrowserLauncher _browser;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<(string Connector, string Account), AccountConnection> _connections = [];

    private ConnectorSettings _applied = new();
    private ToolIndex _index = ToolIndex.Empty;
    private TaskCompletionSource _changed = NewSignal();

    // Published together with _index, so GetToolsAsync never sees "settled" from live statuses
    // before the index holds the tools of a connection that just connected.
    private bool _pending;
    private bool _starting;
    private bool _started;
    private bool _disposed;

    public ConnectorHub(
        ISettingsStore settings,
        IMcpSessionFactory factory,
        ConnectorSecrets secrets,
        IBrowserLauncher browser,
        ILoggerFactory loggerFactory,
        TimeProvider? time = null)
    {
        _settings = settings;
        _factory = factory;
        Secrets = secrets;
        _browser = browser;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ConnectorHub>();
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler? ToolsChanged;

    /// <summary>Raised on a background thread when any connection's state or tool list changes.</summary>
    public event EventHandler? StatusChanged;

    public ConnectorSecrets Secrets { get; }

    public IReadOnlyList<ConnectorTool> Tools => Volatile.Read(ref _index).Tools;

    /// <summary>Connectors as configured (enabled or not), in settings order.</summary>
    public IReadOnlyList<ConnectorConfig> Connectors => _settings.Current.Connectors.Servers;

    /// <summary>Connects every enabled connector in the background and follows settings changes. Returns at once.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
        }

        _settings.Changed += OnSettingsChanged;
        await ApplyAsync(_settings.Current.Connectors, cancellationToken).ConfigureAwait(false);
    }

    public bool IsConnectorTool(string toolName) => Volatile.Read(ref _index).ByName.ContainsKey(toolName);

    public async Task<IReadOnlyList<ConnectorTool>> GetToolsAsync(TimeSpan maxWait, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetUtcNow() + maxWait;
        while (true)
        {
            Task signal;
            lock (_gate)
            {
                if (!_pending)
                {
                    return _index.Tools;
                }

                signal = _changed.Task;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return Tools;
            }

            await Task.WhenAny(signal, Task.Delay(remaining, _time, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    public async Task<ConnectorToolResult> CallAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        if (!Volatile.Read(ref _index).ByName.TryGetValue(toolName, out var entry))
        {
            return ConnectorToolResult.Error($"There is no connector tool named {toolName}. Its connector may be off or disconnected.");
        }

        Dictionary<string, JsonElement> args;
        switch (arguments.ValueKind)
        {
            case JsonValueKind.Object:
                args = [];
                foreach (var property in arguments.EnumerateObject())
                {
                    args[property.Name] = property.Value.Clone();
                }

                break;
            case JsonValueKind.Undefined or JsonValueKind.Null:
                args = [];
                break;
            default:
                return ConnectorToolResult.Error($"Arguments for {toolName} must be a JSON object.");
        }

        _logger.LogInformation("Calling {Tool} on {Connector}/{Account}", entry.Tool.ServerToolName, entry.Tool.ConnectorId, entry.Tool.AccountId);
        var result = await entry.Connection.CallAsync(entry.Tool.ServerToolName, args, cancellationToken).ConfigureAwait(false);
        return Truncate(result, MaxResultCharacters);
    }

    public ConnectionStatus GetStatus(string connectorId, string accountId)
    {
        lock (_gate)
        {
            return _connections.TryGetValue((connectorId, accountId), out var connection)
                ? connection.Status
                : ConnectionStatus.Off(connectorId, accountId);
        }
    }

    /// <summary>Browser OAuth sign-in for one account. Waits until the user finishes or <see cref="SignInTimeout"/> passes.</summary>
    public async Task<ConnectionStatus> SignInAsync(string connectorId, string accountId, CancellationToken cancellationToken = default)
    {
        var connection = Find(connectorId, accountId) ?? throw new InvalidOperationException("Turn the connector and account on first.");
        var config = connection.Config;
        var tokens = new OAuthTokenStore(Secrets, connectorId, accountId);
        using var signIn = LoopbackOAuthSignIn.Start(
            config.OAuthRedirectPort > 0 ? config.OAuthRedirectPort : tokens.RedirectPort,
            requirePort: config.OAuthRedirectPort > 0,
            _browser,
            _loggerFactory.CreateLogger<LoopbackOAuthSignIn>());
        tokens.BeginSignIn(signIn.Port);

        using var timeout = new CancellationTokenSource(SignInTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            return await connection.SignInAsync(signIn, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return connection.Status with { Message = "Sign-in timed out. Try again." };
        }
    }

    /// <summary>Forgets the account's OAuth tokens and disconnects it.</summary>
    public async Task SignOutAsync(string connectorId, string accountId)
    {
        new OAuthTokenStore(Secrets, connectorId, accountId).Clear();
        _logger.LogInformation("Signed out of {Connector}/{Account}", connectorId, accountId);
        await ReconnectAsync(connectorId, accountId).ConfigureAwait(false);
    }

    /// <summary>Restarts one account's connection, or all of a connector's when <paramref name="accountId"/> is null.</summary>
    public async Task ReconnectAsync(string connectorId, string? accountId = null)
    {
        List<AccountConnection> targets;
        lock (_gate)
        {
            targets = [.. _connections.Where(p => p.Key.Connector == connectorId && (accountId is null || p.Key.Account == accountId)).Select(p => p.Value)];
        }

        foreach (var connection in targets)
        {
            await connection.StartAsync(connection.Config, connection.Account).ConfigureAwait(false);
        }
    }

    /// <summary>Adds a connector or replaces the one with the same id. Returns the id it was saved under.</summary>
    public async Task<string> SaveConnectorAsync(ConnectorConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var normalized = config.Normalize();
        var id = normalized.Id;
        await _settings.UpdateAsync(
            s =>
            {
                var servers = s.Connectors.Servers.ToList();
                var index = string.IsNullOrEmpty(config.Id) ? -1 : servers.FindIndex(c => c.Id == config.Id);
                if (index >= 0)
                {
                    servers[index] = normalized;
                }
                else
                {
                    id = ConnectorNames.Unique(normalized.Id, servers.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));
                    servers.Add(normalized with { Id = id });
                }

                return s with { Connectors = new ConnectorSettings(servers) };
            },
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// An unused id for a new connector called <paramref name="name"/>. Write its first account's
    /// secrets under this id before saving, so the first connect already finds them.
    /// </summary>
    public string NewConnectorId(string name) =>
        ConnectorNames.Unique(ConnectorNames.Slug(name, "server"), Connectors.Select(c => c.Id).ToHashSet(StringComparer.Ordinal));

    /// <summary>Adds imported servers (see <see cref="McpServersImport"/>) with their secrets. Returns the new ids.</summary>
    public async Task<IReadOnlyList<string>> ImportAsync(IReadOnlyList<McpServersImport.Imported> servers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var ids = new List<string>();
        foreach (var server in servers)
        {
            var id = NewConnectorId(server.Config.Name);
            foreach (var (variable, value) in server.SecretEnvironment)
            {
                Secrets.Write(ConnectorSecrets.EnvironmentName(id, ConnectorAccount.DefaultId, variable), value);
            }

            foreach (var (header, value) in server.SecretHeaders)
            {
                Secrets.Write(ConnectorSecrets.HeaderName(id, ConnectorAccount.DefaultId, header), value);
            }

            ids.Add(await SaveConnectorAsync(server.Config with { Id = id }, cancellationToken).ConfigureAwait(false));
        }

        _logger.LogInformation("Imported {Count} connectors", ids.Count);
        return ids;
    }

    /// <summary>Removes a connector and everything it stored in Credential Manager.</summary>
    public async Task RemoveConnectorAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        var config = _settings.Current.Connectors.Find(connectorId);
        if (config is null)
        {
            return;
        }

        await _settings.UpdateAsync(
            s => s with { Connectors = new ConnectorSettings([.. s.Connectors.Servers.Where(c => c.Id != connectorId)]) },
            cancellationToken).ConfigureAwait(false);
        Secrets.DeleteConnector(config);
        _logger.LogInformation("Removed connector {Connector} and its secrets", connectorId);
    }

    /// <summary>Adds an account (it starts disconnected until it has credentials). Returns its id.</summary>
    public async Task<string> AddAccountAsync(string connectorId, string label, CancellationToken cancellationToken = default)
    {
        var id = ConnectorAccount.DefaultId;
        await UpdateConnectorAsync(
            connectorId,
            c =>
            {
                id = ConnectorNames.Unique(ConnectorNames.Slug(label, "account"), c.Accounts.Select(a => a.Id).ToHashSet(StringComparer.Ordinal));
                return c with { Accounts = [.. c.Accounts, new ConnectorAccount(id, label.Trim())] };
            },
            cancellationToken).ConfigureAwait(false);
        return id;
    }

    /// <summary>Removes an account and its secrets. The last account cannot be removed; remove the connector instead.</summary>
    public async Task RemoveAccountAsync(string connectorId, string accountId, CancellationToken cancellationToken = default)
    {
        var config = _settings.Current.Connectors.Find(connectorId);
        if (config is null || config.Accounts.Count <= 1 || config.FindAccount(accountId) is null)
        {
            return;
        }

        await UpdateConnectorAsync(connectorId, c => c with { Accounts = [.. c.Accounts.Where(a => a.Id != accountId)] }, cancellationToken).ConfigureAwait(false);
        Secrets.DeleteAccount(config, accountId);
    }

    public Task UpdateConnectorAsync(string connectorId, Func<ConnectorConfig, ConnectorConfig> change, CancellationToken cancellationToken = default) =>
        _settings.UpdateAsync(
            s => s with { Connectors = new ConnectorSettings([.. s.Connectors.Servers.Select(c => c.Id == connectorId ? change(c) : c)]) },
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        List<AccountConnection> connections;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            connections = [.. _connections.Values];
            _connections.Clear();
        }

        _settings.Changed -= OnSettingsChanged;
        await Task.WhenAll(connections.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
        _logger.LogInformation("Closed {Count} connector connections", connections.Count);
    }

    internal static ConnectorToolResult Truncate(ConnectorToolResult result, int maxCharacters)
    {
        var total = result.Content.Sum(c => c is ConnectorContent.Text t ? t.Value.Length : c is ConnectorContent.Resource r ? r.Contents?.Length ?? 0 : 0);
        if (total <= maxCharacters)
        {
            return result;
        }

        var budget = maxCharacters;
        var content = new List<ConnectorContent>(result.Content.Count + 1);
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case ConnectorContent.Text text:
                    content.Add(text with { Value = Cut(text.Value, ref budget) });
                    break;
                case ConnectorContent.Resource { Contents: { } contents } resource:
                    content.Add(resource with { Contents = Cut(contents, ref budget) });
                    break;
                default:
                    content.Add(block);
                    break;
            }
        }

        content.Add(new ConnectorContent.Text($"[Result cut: {total - maxCharacters:N0} of {total:N0} characters left out.]"));
        return result with { Content = content };

        static string Cut(string value, ref int budget)
        {
            var keep = Math.Min(value.Length, Math.Max(0, budget));
            budget -= keep;
            return keep == value.Length ? value : value[..keep];
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private AccountConnection? Find(string connectorId, string accountId)
    {
        lock (_gate)
        {
            return _connections.GetValueOrDefault((connectorId, accountId));
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Previous.Connectors != e.Current.Connectors)
        {
            _ = ApplyLoggedAsync(e.Current.Connectors);
        }
    }

    private async Task ApplyLoggedAsync(ConnectorSettings settings)
    {
        try
        {
            await ApplyAsync(settings, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Could not apply connector settings");
        }
    }

    private async Task ApplyAsync(ConnectorSettings settings, CancellationToken cancellationToken)
    {
        await _apply.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var desired = new Dictionary<(string, string), (ConnectorConfig Config, ConnectorAccount Account)>();
            foreach (var config in settings.Servers.Where(c => c.Enabled))
            {
                foreach (var account in config.EnabledAccounts)
                {
                    desired[(config.Id, account.Id)] = (config, account);
                }
            }

            var toStop = new List<AccountConnection>();
            var toStart = new List<(AccountConnection Connection, ConnectorConfig Config, ConnectorAccount Account)>();
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var key in _connections.Keys.Where(k => !desired.ContainsKey(k)).ToList())
                {
                    toStop.Add(_connections[key]);
                    _connections.Remove(key);
                }

                foreach (var (key, (config, account)) in desired)
                {
                    if (_connections.TryGetValue(key, out var existing))
                    {
                        if (existing.Config.ConnectionEquals(config))
                        {
                            existing.Update(config, account);
                        }
                        else
                        {
                            toStart.Add((existing, config, account));
                        }
                    }
                    else
                    {
                        var connection = new AccountConnection(config, account, _factory, _loggerFactory.CreateLogger<AccountConnection>(), _time, OnConnectionChanged);
                        _connections[key] = connection;
                        toStart.Add((connection, config, account));
                    }
                }

                _applied = settings;
                if (toStart.Count > 0)
                {
                    // New connections sit at Off until StartAsync, so count them as pending meanwhile.
                    _starting = true;
                    _pending = true;
                }
            }

            try
            {
                await Task.WhenAll(toStop.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
                await Task.WhenAll(toStart.Select(s => s.Connection.StartAsync(s.Config, s.Account))).ConfigureAwait(false);
                if (toStop.Count + toStart.Count > 0)
                {
                    _logger.LogInformation("Connectors: {Started} (re)started, {Stopped} stopped", toStart.Count, toStop.Count);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _starting = false;
                }

                Republish();
            }
        }
        finally
        {
            _apply.Release();
        }
    }

    private void OnConnectionChanged(AccountConnection connection) => Republish();

    private void Republish()
    {
        bool toolsChanged;
        TaskCompletionSource signal;
        lock (_gate)
        {
            var index = BuildIndex();
            toolsChanged = !index.SameAs(_index);
            if (toolsChanged)
            {
                Volatile.Write(ref _index, index);
            }

            _pending = _starting || _connections.Values.Any(c => c.Status.IsPending);
            signal = _changed;
            _changed = NewSignal();
        }

        signal.TrySetResult();
        if (toolsChanged)
        {
            Raise(ToolsChanged, nameof(ToolsChanged));
        }

        Raise(StatusChanged, nameof(StatusChanged));
    }

    private void Raise(EventHandler? handlers, string name)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Event} handler {Handler} threw", name, handler.Method.Name);
            }
        }
    }

    /// <summary>Must hold _gate.</summary>
    private ToolIndex BuildIndex()
    {
        var tools = new List<ConnectorTool>();
        var byName = new Dictionary<string, (AccountConnection, ConnectorTool)>(StringComparer.Ordinal);
        foreach (var config in _applied.Servers.Where(c => c.Enabled))
        {
            var accounts = config.EnabledAccounts.ToList();
            var multiple = accounts.Count > 1;
            var disabled = config.DisabledTools.ToHashSet(StringComparer.Ordinal);
            foreach (var account in accounts)
            {
                if (!_connections.TryGetValue((config.Id, account.Id), out var connection) || connection.Status.State != ConnectionState.Connected)
                {
                    continue;
                }

                var prefix = ConnectorNames.Prefix(config.Id, account.Id, multiple);
                var label = multiple ? $"{config.Name} · {account.Label}" : config.Name;
                foreach (var info in connection.Status.Tools)
                {
                    if (disabled.Contains(info.Name))
                    {
                        continue;
                    }

                    var name = ConnectorNames.ToolName(prefix, info.Name);
                    for (var n = 2; byName.ContainsKey(name); n++)
                    {
                        name = ConnectorNames.ToolName(prefix, $"{info.Name}_{n}");
                    }

                    var readOnly = info.ReadOnlyHint == true;
                    var tool = new ConnectorTool(
                        name,
                        $"[{label}] {info.Description ?? info.Title ?? info.Name}".Trim(),
                        info.InputSchema.ValueKind == JsonValueKind.Object ? info.InputSchema : EmptyObjectSchema,
                        config.Id,
                        config.Name,
                        account.Id,
                        account.Label,
                        info.Name,
                        info.Title,
                        readOnly,
                        Destructive: !readOnly && (info.DestructiveHint ?? true),
                        OpenWorld: info.OpenWorldHint ?? true);
                    tools.Add(tool);
                    byName[name] = (connection, tool);
                }
            }
        }

        return new ToolIndex(tools, byName);
    }

    private sealed record ToolIndex(IReadOnlyList<ConnectorTool> Tools, IReadOnlyDictionary<string, (AccountConnection Connection, ConnectorTool Tool)> ByName)
    {
        public static ToolIndex Empty { get; } = new([], new Dictionary<string, (AccountConnection, ConnectorTool)>());

        public bool SameAs(ToolIndex other) =>
            Tools.Count == other.Tools.Count
            && Tools.Zip(other.Tools).All(p =>
                p.First.Name == p.Second.Name
                && p.First.Description == p.Second.Description
                && p.First.ReadOnly == p.Second.ReadOnly
                && p.First.Destructive == p.Second.Destructive
                && p.First.AccountLabel == p.Second.AccountLabel
                && p.First.InputSchema.GetRawText() == p.Second.InputSchema.GetRawText())
            && ByName.Values.Zip(other.ByName.Values).All(p => ReferenceEquals(p.First.Connection, p.Second.Connection));
    }
}
