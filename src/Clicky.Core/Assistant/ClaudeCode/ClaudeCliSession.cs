using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Clicky.Core.Assistant.ClaudeCode;

/// <summary>
/// One running Claude Code process and the stream-json protocol on it: user messages and control
/// requests (initialize, set_model, interrupt…) go to stdin; a pump reads stdout, answers control
/// responses and queues everything else for the reply being read. Starts as a spare that belongs to
/// no conversation, then holds one conversation for the rest of its life.
/// </summary>
internal sealed class ClaudeCliSession : IDisposable
{
    private readonly IClaudeCliProcess _process;
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private int _nextRequest;

    public ClaudeCliSession(IClaudeCliProcess process, string model, ClaudeEffort? effort)
    {
        _process = process;
        Model = model;
        Effort = effort;
        Pump = Task.Run(PumpAsync);
    }

    /// <summary>The conversation this process holds, or null while it is a fresh spare that any conversation can take.</summary>
    public Guid? ConversationId { get; private set; }

    /// <summary><see cref="Conversation.Generation"/> when the process took the conversation; a trim since means Clicky's history is shorter than the process's.</summary>
    public int Generation { get; private set; }

    /// <summary>The model the process is using now.</summary>
    public string Model { get; set; }

    /// <summary>The effort level the process is using now; null for Claude Code's default.</summary>
    public ClaudeEffort? Effort { get; set; }

    /// <summary>Whether the model may think first; null for Claude Code's default (adaptive).</summary>
    public bool? Thinking { get; set; }

    /// <summary>The memory block the process was given (it can change after feedback).</summary>
    public string? Memory { get; set; }

    /// <summary>Turns of the conversation this process has seen (as a seed transcript or live).</summary>
    public int TurnsSeen { get; set; }

    /// <summary>Screenshots and pictures in the process's history; each one is uploaded again with every request.</summary>
    public int ImagesSeen { get; set; }

    /// <summary>Tokens of context the last request carried (input, cache reads and writes).</summary>
    public long ContextTokens { get; set; }

    /// <summary>The connector tool list this process was last shown (see ClaudeCodeBackend), or null for none yet.</summary>
    public string? ToolCatalogKey { get; set; }

    /// <summary>Completes when initialize was acknowledged; faults if the process failed first.</summary>
    public Task? Ready { get; set; }

    public bool IsAlive => !_process.HasExited && !Pump.IsCompleted;

    /// <summary>Started, initialized and still running.</summary>
    public bool IsReady => IsAlive && Ready is { IsCompletedSuccessfully: true };

    /// <summary>Gives a fresh process its conversation.</summary>
    public void Bind(Conversation conversation)
    {
        ConversationId = conversation.Id;
        Generation = conversation.Generation;
    }

    public string ErrorTail => _process.ErrorTail;

    public int? ExitCode => _process.ExitCode;

    public Task Pump { get; }

    /// <summary>Sends a control request and waits for its response.</summary>
    /// <exception cref="ClaudeBackendException">The process answered with an error or exited.</exception>
    public async Task<JsonElement> ControlAsync(JsonObject request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = $"clicky-{Interlocked.Increment(ref _nextRequest).ToString(CultureInfo.InvariantCulture)}";
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new JsonObject { ["type"] = "control_request", ["request_id"] = id, ["request"] = request }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new ClaudeBackendException(ClaudeFailure.Timeout, $"Claude Code did not answer a {request["subtype"]} request.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends one user message. <paramref name="content"/> is a Messages-API content array.</summary>
    public Task SendUserAsync(JsonArray content, CancellationToken cancellationToken) =>
        WriteAsync(
            new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
                ["parent_tool_use_id"] = null,
            },
            cancellationToken);

    /// <summary>The next non-control message from Claude Code; false when the process has ended.</summary>
    public async ValueTask<(bool Ok, JsonElement Message)> ReadAsync(CancellationToken cancellationToken)
    {
        if (await _messages.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) && _messages.Reader.TryRead(out var message))
        {
            return (true, message);
        }

        return (false, default);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _process.Dispose();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new ClaudeBackendException(ClaudeFailure.Failed, "Claude Code was closed."));
        }

        _stop.Dispose();
        _write.Dispose();
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.WriteLineAsync(message.ToJsonString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new ClaudeBackendException(ClaudeFailure.Failed, $"Claude Code stopped unexpectedly. {_process.ErrorTail}".Trim(), ex);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _process.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonElement message;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    message = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue; // Not protocol output (a stray log line); ignore it.
                }

                switch (message.TryGetProperty("type", out var type) ? type.GetString() : null)
                {
                    case "control_response":
                        CompleteControl(message);
                        break;
                    case "control_request":
                        // Claude Code asking us something (a tool permission). It has no tools here, so refuse.
                        await RefuseControlAsync(message).ConfigureAwait(false);
                        break;
                    default:
                        _messages.Writer.TryWrite(message);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            failure = ex is OperationCanceledException ? null : ex;
        }
        finally
        {
            _messages.Writer.TryComplete();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new ClaudeBackendException(ClaudeFailure.Failed, $"Claude Code exited. {_process.ErrorTail}".Trim(), failure));
            }
        }
    }

    private void CompleteControl(JsonElement message)
    {
        if (!message.TryGetProperty("response", out var response)
            || !response.TryGetProperty("request_id", out var idElement)
            || idElement.GetString() is not { } id
            || !_pending.TryGetValue(id, out var completion))
        {
            return;
        }

        if (response.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "error")
        {
            var error = response.TryGetProperty("error", out var e) ? e.ToString() : "unknown error";
            completion.TrySetException(new ClaudeBackendException(ClaudeFailure.Failed, $"Claude Code refused a request: {error}"));
        }
        else
        {
            completion.TrySetResult(response.TryGetProperty("response", out var body) ? body : default);
        }
    }

    private async Task RefuseControlAsync(JsonElement message)
    {
        if (!message.TryGetProperty("request_id", out var id))
        {
            return;
        }

        try
        {
            await WriteAsync(
                new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "error",
                        ["request_id"] = id.GetString(),
                        ["error"] = "Clicky does not handle this request.",
                    },
                },
                _stop.Token).ConfigureAwait(false);
        }
        catch (ClaudeBackendException)
        {
            // The process is going away; the reader will notice.
        }
    }
}
