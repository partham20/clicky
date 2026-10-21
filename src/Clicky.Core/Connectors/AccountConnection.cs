using System.Text.Json;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Connectors;

/// <summary>
/// Keeps one account of one connector connected: connects in the background, lists tools,
/// follows tools/list_changed, and reconnects with backoff when the process exits or the network
/// drops. Never opens a browser on its own; OAuth waits in <see cref="ConnectionState.SignInRequired"/>.
/// </summary>
internal sealed class AccountConnection : IAsyncDisposable
{
    /// <summary>Delays between reconnect attempts; the last one repeats.</summary>
    internal static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60),
    ];

    /// <summary>A server that never connected is given up on after this many tries (a wrong command will not fix itself).</summary>
    internal const int MaxAttemptsBeforeFirstConnect = 3;

    /// <summary>A session that lasted this long resets the backoff.</summary>
    internal static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan ListToolsTimeout = TimeSpan.FromSeconds(30);

    private readonly IMcpSessionFactory _factory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Action<AccountConnection> _changed;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _refresh = new(1, 1);

    private ConnectorConfig _config;
    private ConnectorAccount _account;
    private ConnectionStatus _status;
    private CancellationTokenSource? _loopCts;
    private Task _loop = Task.CompletedTask;
    private volatile IMcpSession? _session;
    private volatile bool _signInNeeded;

    public AccountConnection(ConnectorConfig config, ConnectorAccount account, IMcpSessionFactory factory, ILogger logger, TimeProvider time, Action<AccountConnection> changed)
    {
        _config = config;
        _account = account;
        _factory = factory;
        _logger = logger;
        _time = time;
        _changed = changed;
        _status = new ConnectionStatus(config.Id, account.Id, ConnectionState.Connecting, null, null, [], null);
    }

    public ConnectorConfig Config => Volatile.Read(ref _config);

    public ConnectorAccount Account => Volatile.Read(ref _account);

    public ConnectionStatus Status => Volatile.Read(ref _status);

    private string Name => $"{Config.Id}/{Account.Id}";

    /// <summary>Applies changes that need no reconnect: display names, tool filters, timeouts.</summary>
    public void Update(ConnectorConfig config, ConnectorAccount account)
    {
        Volatile.Write(ref _config, config);
        Volatile.Write(ref _account, account);
    }

    /// <summary>(Re)starts the background loop, optionally with a session that is already open.</summary>
    public async Task StartAsync(ConnectorConfig config, ConnectorAccount account, IMcpSession? initial = null)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            Update(config, account);
            StartLoop(initial);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Interactive OAuth: stops the background loop, signs in through the browser, then keeps that session.</summary>
    public async Task<ConnectionStatus> SignInAsync(IOAuthSignIn signIn, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
            Set(ConnectionState.Connecting, "Finish signing in in your browser…");
            IMcpSession session;
            try
            {
                session = await _factory.ConnectAsync(Config, Account, signIn, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Set(ConnectionState.SignInRequired, "Sign-in was cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Connection}] Sign-in failed", Name);
                Set(ConnectionState.SignInRequired, "Sign-in failed: " + Describe(ex));
                return Status;
            }

            _logger.LogInformation("[{Connection}] Signed in", Name);
            StartLoop(session);
            return Status;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopLoopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<ConnectorToolResult> CallAsync(string serverToolName, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
    {
        var config = Config;
        var session = _session;
        if (session is null)
        {
            var status = Status;
            return ConnectorToolResult.Error($"{config.Name} is not connected{(status.Message is { } m ? ": " + m : ".")}");
        }

        var timeout = TimeSpan.FromSeconds(config.ToolTimeoutSeconds);
        using var timeoutCts = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await session.CallToolAsync(serverToolName, arguments, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ConnectorToolResult.Error($"{serverToolName} on {config.Name} took longer than {config.ToolTimeoutSeconds} s and was stopped.");
        }
        catch (SignInRequiredException)
        {
            // Ends the session; the loop sees the flag and waits for Sign in.
            _signInNeeded = true;
            _ = session.DisposeAsync().AsTask();
            return ConnectorToolResult.Error($"{config.Name} needs the user to sign in again (Settings › Connectors).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{Connection}] Tool {Tool} failed", Name, serverToolName);
            return ConnectorToolResult.Error($"{config.Name} could not run {serverToolName}: {Describe(ex)}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _refresh.Dispose();
    }

    internal static string Describe(Exception ex)
    {
        var message = ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;
        message = message.Trim();
        return message.Length > 600 ? message[..600] + "…" : message;
    }

    private void StartLoop(IMcpSession? initial)
    {
        _loopCts = new CancellationTokenSource();
        var token = _loopCts.Token;
        _loop = Task.Run(() => RunAsync(initial, token), CancellationToken.None);
    }

    private async Task StopLoopAsync()
    {
        if (_loopCts is null)
        {
            return;
        }

        await _loopCts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "[{Connection}] Connection loop failed", Name);
        }

        _loopCts.Dispose();
        _loopCts = null;
    }

    private async Task RunAsync(IMcpSession? initial, CancellationToken cancellationToken)
    {
        var failures = 0;
        var everConnected = initial is not null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var session = initial;
                initial = null;
                if (session is null)
                {
                    Set(ConnectionState.Connecting, null);
                    try
                    {
                        session = await _factory.ConnectAsync(Config, Account, signIn: null, cancellationToken).ConfigureAwait(false);
                    }
                    catch (SignInRequiredException)
                    {
                        Set(ConnectionState.SignInRequired, "Sign in to connect.");
                        return;
                    }
                    catch (MissingSecretsException ex)
                    {
                        Set(ConnectionState.NeedsSecrets, ex.Message);
                        return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failures++;
                        _logger.LogWarning("[{Connection}] Could not connect (attempt {Attempt}): {Error}", Name, failures, Describe(ex));
                        if (!everConnected && failures >= MaxAttemptsBeforeFirstConnect)
                        {
                            Set(ConnectionState.Failed, Describe(ex));
                            return;
                        }

                        await WaitToRetryAsync(failures, Describe(ex), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                var ended = await RunSessionAsync(session, cancellationToken).ConfigureAwait(false);
                if (ended is null)
                {
                    return;
                }

                var (end, lasted) = ended.Value;
                if ((end.NeedsSignIn || _signInNeeded) && Config.Auth == ConnectorAuth.OAuth)
                {
                    Set(ConnectionState.SignInRequired, "Sign in again to reconnect.");
                    return;
                }

                everConnected |= lasted is not null;
                failures = lasted >= StableAfter ? 1 : failures + 1;
                var reason = end.Reason ?? "The connection closed.";
                _logger.LogWarning("[{Connection}] Disconnected: {Reason}", Name, reason);
                if (!everConnected && failures >= MaxAttemptsBeforeFirstConnect)
                {
                    Set(ConnectionState.Failed, reason);
                    return;
                }

                await WaitToRetryAsync(failures, reason, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped.
        }
    }

    /// <summary>Lists tools, publishes Connected, and waits for the session to end. Null when stopped.</summary>
    private async Task<(SessionEnd End, TimeSpan? Lasted)?> RunSessionAsync(IMcpSession session, CancellationToken cancellationToken)
    {
        _signInNeeded = false;
        session.ToolsChanged += OnToolsChanged;
        try
        {
            IReadOnlyList<McpToolInfo> tools;
            try
            {
                tools = await ListToolsAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (SignInRequiredException)
            {
                return (new SessionEnd("Sign in again.", NeedsSignIn: true), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                return (new SessionEnd("Could not list tools: " + Describe(ex)), null);
            }

            var connectedAt = _time.GetUtcNow();
            _session = session;
            Set(ConnectionState.Connected, null, session.ServerDescription, tools);
            _logger.LogInformation("[{Connection}] Connected to {Server} with {Count} tools", Name, session.ServerDescription, tools.Count);

            var end = await session.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            return (end, _time.GetUtcNow() - connectedAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            session.ToolsChanged -= OnToolsChanged;
            _session = null;
            await DisposeQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private async Task WaitToRetryAsync(int failures, string reason, CancellationToken cancellationToken)
    {
        var delay = Backoff[Math.Clamp(failures - 1, 0, Backoff.Length - 1)];

        // Start the timer before publishing RetryAt, so whoever sees the retry time (a test
        // advancing a fake clock) finds the wait already running.
        var wait = Task.Delay(delay, _time, cancellationToken);
        Set(ConnectionState.Failed, reason, retryAt: _time.GetUtcNow() + delay);
        await wait.ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(IMcpSession session, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ListToolsTimeout);
        return await session.ListToolsAsync(timeout.Token).ConfigureAwait(false);
    }

    private void OnToolsChanged(object? sender, EventArgs e)
    {
        if (sender is IMcpSession session)
        {
            _ = RefreshToolsAsync(session);
        }
    }

    private async Task RefreshToolsAsync(IMcpSession session)
    {
        try
        {
            await _refresh.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(_session, session))
            {
                return;
            }

            var tools = await ListToolsAsync(session, CancellationToken.None).ConfigureAwait(false);
            if (ReferenceEquals(_session, session))
            {
                Set(ConnectionState.Connected, null, session.ServerDescription, tools);
                _logger.LogInformation("[{Connection}] Tool list changed; now {Count} tools", Name, tools.Count);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning("[{Connection}] Could not refresh tools: {Error}", Name, Describe(ex));
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task DisposeQuietlyAsync(IMcpSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "[{Connection}] Closing the session threw", Name);
        }
    }

    private void Set(ConnectionState state, string? message, string? server = null, IReadOnlyList<McpToolInfo>? tools = null, DateTimeOffset? retryAt = null)
    {
        Volatile.Write(ref _status, new ConnectionStatus(Config.Id, Account.Id, state, message, server, tools ?? [], retryAt));
        _changed(this);
    }
}
