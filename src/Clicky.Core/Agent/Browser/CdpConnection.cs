using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Clicky.Core.Agent.Browser;

/// <summary>
/// A Chrome DevTools Protocol connection over one WebSocket (the browser endpoint, with sessions
/// flattened onto it): JSON-RPC requests matched by id, and events. Thread-safe.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    private const int MaxMessageBytes = 64 * 1024 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _receive;
    private int _nextId;

    private CdpConnection(ClientWebSocket socket)
    {
        _socket = socket;
        _receive = Task.Run(ReceiveAsync);
    }

    /// <summary>Raised on the receive thread for every event: method, params, and session id (null for the browser).</summary>
    public event Action<string, JsonElement, string?>? EventReceived;

    public bool IsOpen => _socket.State == WebSocketState.Open && !_receive.IsCompleted;

    public static async Task<CdpConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var socket = new ClientWebSocket();

        // Loopback only, never through the user's proxy.
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DefaultTimeout);
            await socket.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new CdpConnection(socket);
    }

    /// <summary>Sends a command and returns its "result".</summary>
    /// <exception cref="CdpException">The browser answered with an error, the connection closed, or it timed out.</exception>
    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
        if (sessionId is not null)
        {
            message["sessionId"] = sessionId;
        }

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
            await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException)
            {
                throw new CdpException($"The browser connection is closed ({method}).", ex);
            }
            finally
            {
                _send.Release();
            }

            return await completion.Task.WaitAsync(timeout ?? DefaultTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new CdpException($"The browser did not answer {method}.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var close = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, close.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Closing anyway.
        }

        _socket.Abort();
        try
        {
            await _receive.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        _socket.Dispose();
        _send.Dispose();
        _stop.Dispose();
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    throw new CdpException("A browser message was too large.");
                }

                if (!result.EndOfMessage)
                {
                    continue;
                }

                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or CdpException)
        {
            // The browser went away; pending requests fail below.
        }
        finally
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new CdpException("The browser connection closed."));
            }
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> utf8)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(utf8);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }

        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
        {
            if (_pending.TryGetValue(id, out var completion))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                    completion.TrySetException(new CdpException(string.Create(CultureInfo.InvariantCulture, $"Browser error: {text}")));
                }
                else
                {
                    completion.TrySetResult(root.TryGetProperty("result", out var result) ? result : default);
                }
            }

            return;
        }

        if (root.TryGetProperty("method", out var method) && method.GetString() is { } name)
        {
            var session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            EventReceived?.Invoke(name, root.TryGetProperty("params", out var p) ? p : default, session);
        }
    }
}

public sealed class CdpException : Exception
{
    public CdpException()
    {
    }

    public CdpException(string message)
        : base(message)
    {
    }

    public CdpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
