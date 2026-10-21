using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Clicky.Core.Actions;
using Clicky.Core.Prompts;
using Clicky.Core.Secrets;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Assistant;

/// <summary>
/// Asks Claude through the Messages API with the user's key: streaming, the screenshot inline as
/// base64 (never written anywhere), every non-speech action as a strict tool, and prompt caching on
/// the fixed system prompt and tools.
/// </summary>
/// <remarks>
/// History is sent back append-only with thinking blocks unchanged. The occasional trim (see
/// <see cref="HistoryLimits"/>) edits the front, which would make the API reject later thinking
/// blocks on newer accounts, so requests ask it to drop such blocks instead of failing.
/// </remarks>
public sealed class AnthropicApiBackend(ISecretStore secrets, ISettingsStore settings, ILogger<AnthropicApiBackend> logger) : IClaudeBackend, IDisposable
{
    /// <summary>Room for adaptive thinking plus a spoken answer; replies stream, so a large cap costs nothing.</summary>
    public const int MaxTokens = 16_000;

    /// <summary>No text or event for this long and the reply is abandoned.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Refusals are retried by the server on its default fallback model, where the model has one.</summary>
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Lets a request ask for invalidated thinking blocks to be dropped rather than rejected.</summary>
    private const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";

    private const string ToolResultText = "Shown to the user.";

    private static readonly Lazy<List<BetaToolUnion>> s_tools = new(BuildTools);

    private readonly Lock _gate = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    public ClaudeBackendKind Kind => ClaudeBackendKind.Api;

    public void Prepare(Conversation conversation)
    {
        // Nothing slow to start: the client and its connection pool are created on first use and kept.
    }

    public Task<BackendCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return Task.FromResult(AnthropicKey.Read(secrets) is null
                ? new BackendCheck(false, "No Anthropic API key saved.")
                : new BackendCheck(true, "API key saved."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the Anthropic key");
            return Task.FromResult(new BackendCheck(false, "Could not read Credential Manager."));
        }
    }

    public async IAsyncEnumerable<ReplyEvent> ReplyAsync(
        Conversation conversation,
        ClaudeRequest request,
        string model,
        ClaudeEffort effort,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = AnthropicKey.Read(secrets) ?? throw new ClaudeBackendException(
            ClaudeFailure.NotConfigured, "Clicky needs an Anthropic API key. Add one in Settings › Claude, or switch to your Claude subscription there.");
        var client = Client(key);
        var userText = PromptLibrary.UserText(request);
        var parameters = BuildParameters(conversation, request, userText, model, effort);
        var limits = new HistoryLimits(settings.Current.Claude.MaxTurns);
        var reply = new StreamState(request.Screenshot);
        var stopwatch = Stopwatch.StartNew();

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        IAsyncEnumerator<BetaRawMessageStreamEvent>? stream = null;
        try
        {
            while (true)
            {
                BetaRawMessageStreamEvent streamEvent;
                try
                {
                    stream ??= client.Beta.Messages.CreateStreaming(parameters, stall.Token).GetAsyncEnumerator(stall.Token);
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    streamEvent = stream.Current;
                    stall.CancelAfter(StallTimeout);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ClaudeBackendException(ClaudeFailure.Timeout, $"Claude went quiet for {StallTimeout.TotalSeconds:0} s, so Clicky gave up on that answer.");
                }
                catch (Exception ex) when (Describe(ex, model) is { } failure)
                {
                    logger.LogWarning(ex, "Claude API request failed ({Failure})", failure.Failure);
                    throw failure;
                }

                foreach (var replyEvent in reply.Handle(streamEvent, stopwatch.Elapsed, logger))
                {
                    yield return replyEvent;
                }
            }

            reply.Completed = true;
            var completed = reply.Complete(stopwatch.Elapsed, model);
            logger.LogInformation(
                "Claude {Model} replied in {Elapsed} ms (first text {FirstText} ms): stop {Stop}, {Actions} actions, tokens in {In} (cache read {CacheRead}, write {CacheWrite}) out {Out}",
                completed.Model,
                (long)completed.Elapsed.TotalMilliseconds,
                completed.FirstText is { } first ? (long)first.TotalMilliseconds : -1,
                completed.Stop,
                completed.Actions.Count(a => a is not SayAction),
                completed.Usage.InputTokens,
                completed.Usage.CacheReadTokens,
                completed.Usage.CacheWriteTokens,
                completed.Usage.OutputTokens);
            yield return completed;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            // A cancelled reply (barge-in) still happened as far as the user is concerned: keep what was said.
            if (reply.Completed || reply.HasText)
            {
                conversation.Append(reply.ToTurn(userText, request.Screenshot?.Png, conversation.Now), limits);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    internal static MessageCreateParams BuildParameters(Conversation conversation, ClaudeRequest request, string userText, string model, ClaudeEffort effort)
    {
        var messages = new List<BetaMessageParam>();
        IReadOnlyList<ToolCallRecord> pending = [];
        foreach (var turn in conversation.Turns)
        {
            messages.Add(UserMessage(pending, turn.Screenshot, turn.UserText));
            messages.Add(AssistantMessage(turn));
            pending = turn.ToolCalls;
        }

        messages.Add(UserMessage(pending, request.Screenshot?.Png, userText));

        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            Betas = [FallbackBeta, ThinkingBindingBeta],
            Fallbacks = new Default(),
            System = new List<BetaTextBlockParam>
            {
                new() { Text = PromptLibrary.SystemPrompt(ActionDelivery.Tools), CacheControl = new BetaCacheControlEphemeral() },
            },
            Tools = s_tools.Value,
            ToolChoice = new BetaToolChoiceAuto(),
            Thinking = new BetaThinkingConfigAdaptive
            {
                BlockBinding = new() { PrefixMismatchBehavior = BetaThinkingPrefixMismatchBehavior.DropBlock },
            },
            OutputConfig = new BetaOutputConfig { Effort = ToEffort(effort) },

            // Automatic caching: the breakpoint moves to the end of the history each turn.
            CacheControl = new BetaCacheControlEphemeral(),
            Messages = messages,
        };
    }

    private static BetaMessageParam UserMessage(IReadOnlyList<ToolCallRecord> pending, byte[]? png, string text)
    {
        var content = new List<BetaContentBlockParam>();
        foreach (var call in pending)
        {
            content.Add(new BetaToolResultBlockParam { ToolUseID = call.Id, Content = ToolResultText });
        }

        if (png is not null)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(png), MediaType = MediaType.ImagePng },
            });
        }

        content.Add(new BetaTextBlockParam { Text = text });
        return new BetaMessageParam { Role = Role.User, Content = content };
    }

    private static BetaMessageParam AssistantMessage(ConversationTurn turn)
    {
        var content = new List<BetaContentBlockParam>();
        foreach (var thinking in turn.Thinking ?? [])
        {
            content.Add(thinking.RedactedData is { } data
                ? new BetaRedactedThinkingBlockParam { Data = data }
                : new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature ?? string.Empty });
        }

        var text = turn.AssistantText.TrimEnd();
        if (turn.Interrupted)
        {
            text = text.Length == 0 ? "(The user interrupted before I said anything.)" : $"{text} (The user interrupted me here.)";
        }

        if (text.Length > 0)
        {
            content.Add(new BetaTextBlockParam { Text = text });
        }

        foreach (var call in turn.ToolCalls)
        {
            using var input = JsonDocument.Parse(call.InputJson);
            content.Add(new BetaToolUseBlockParam
            {
                ID = call.Id,
                Name = call.Name,
                Input = input.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            });
        }

        if (content.Count == 0 || content.All(c => c.TryPickThinking(out _) || c.TryPickRedactedThinking(out _)))
        {
            content.Add(new BetaTextBlockParam { Text = "(No answer.)" });
        }

        return new BetaMessageParam { Role = Role.Assistant, Content = content };
    }

    private static List<BetaToolUnion> BuildTools()
    {
        var schema = ActionSchema.Default;
        var tools = new List<BetaToolUnion>();
        foreach (var name in schema.Names.Where(n => n != "say"))
        {
            var input = schema.ToolInputSchema(name);
            using var document = JsonDocument.Parse(input.ToJsonString());
            tools.Add(new BetaTool
            {
                Name = name,
                Description = schema.Description(name),
                InputSchema = InputSchema.FromRawUnchecked(document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())),

                // Tiny inputs, so buffering costs nothing and the API keeps validating them for us.
                Strict = true,
            });
        }

        return tools;
    }

    private static Effort ToEffort(ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => Effort.Low,
        ClaudeEffort.Medium => Effort.Medium,
        ClaudeEffort.High => Effort.High,
        ClaudeEffort.XHigh => Effort.Xhigh,
        _ => Effort.Max,
    };

    /// <summary>Most specific first: each status means something different to the user.</summary>
    private static ClaudeBackendException? Describe(Exception ex, string model) => ex switch
    {
        AnthropicUnauthorizedException => new(ClaudeFailure.Unauthorized, "Anthropic rejected the API key. Check it in Settings › Claude.", ex),
        AnthropicForbiddenException => new(ClaudeFailure.Unauthorized, "This API key isn't allowed to do that. Check its workspace in the Claude Console.", ex),
        AnthropicNotFoundException => new(ClaudeFailure.BadRequest, $"Anthropic doesn't know the model \"{model}\". Check the model names in Settings › Claude.", ex),
        AnthropicRateLimitException => new(ClaudeFailure.RateLimited, "Anthropic is rate limiting this key. Try again in a moment.", ex),
        AnthropicBadRequestException when ex.Message.Contains("credit balance", StringComparison.OrdinalIgnoreCase) =>
            new(ClaudeFailure.RateLimited, "The Anthropic account is out of credit. Top it up in the Claude Console, or switch to your Claude subscription in Settings › Claude.", ex),
        AnthropicBadRequestException => new(ClaudeFailure.BadRequest, $"Anthropic rejected the request: {FirstLine(ex.Message)}", ex),
        Anthropic5xxException => new(ClaudeFailure.Unavailable, "Claude is overloaded or having trouble right now. Try again in a moment.", ex),
        AnthropicSseException when ex.Message.Contains("overloaded", StringComparison.OrdinalIgnoreCase) =>
            new(ClaudeFailure.Unavailable, "Claude is overloaded right now. Try again in a moment.", ex),
        AnthropicIOException or HttpRequestException => new(ClaudeFailure.Network, "Could not reach Anthropic. Check the internet connection.", ex),
        AnthropicException => new(ClaudeFailure.Failed, $"The Claude request failed: {FirstLine(ex.Message)}", ex),
        _ => null,
    };

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    private AnthropicClient Client(string key)
    {
        lock (_gate)
        {
            if (_client is null || _clientKey != key)
            {
                _client?.Dispose();
                _client = new AnthropicClient { ApiKey = key, MaxRetries = 2, Timeout = TimeSpan.FromSeconds(60) };
                _clientKey = key;
            }

            return _client;
        }
    }

    /// <summary>Turns stream events into reply events and remembers what to store in history.</summary>
    private sealed class StreamState(RequestScreenshot? screenshot)
    {
        private readonly Dictionary<long, Block> _blocks = [];
        private readonly List<ClickyAction> _actions = [];
        private readonly List<ToolCallRecord> _toolCalls = [];
        private readonly List<ThinkingRecord> _thinking = [];
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _spoken = new();
        private TimeSpan? _firstText;
        private string? _model;
        private string? _stopReason;
        private string? _refusal;
        private long _inputTokens;
        private long _outputTokens;
        private long _cacheRead;
        private long _cacheWrite;

        public bool Completed { get; set; }

        public bool HasText => _spoken.Length > 0;

        public IEnumerable<ReplyEvent> Handle(BetaRawMessageStreamEvent streamEvent, TimeSpan elapsed, ILogger logger)
        {
            if (streamEvent.TryPickStart(out var start))
            {
                _model = start.Message.Model;
                _inputTokens = start.Message.Usage.InputTokens;
                _cacheRead = start.Message.Usage.CacheReadInputTokens ?? 0;
                _cacheWrite = start.Message.Usage.CacheCreationInputTokens ?? 0;
            }
            else if (streamEvent.TryPickContentBlockStart(out var blockStart))
            {
                var block = new Block();
                if (blockStart.ContentBlock.TryPickBetaToolUse(out var toolUse))
                {
                    block.ToolId = toolUse.ID;
                    block.ToolName = toolUse.Name;
                }
                else if (blockStart.ContentBlock.TryPickBetaThinking(out _))
                {
                    block.IsThinking = true;
                }
                else if (blockStart.ContentBlock.TryPickBetaRedactedThinking(out var redacted))
                {
                    _thinking.Add(new ThinkingRecord(string.Empty, null, redacted.Data));
                }

                _blocks[blockStart.Index] = block;
            }
            else if (streamEvent.TryPickContentBlockDelta(out var delta))
            {
                if (delta.Delta.TryPickText(out var text) && text.Text.Length > 0)
                {
                    _firstText ??= elapsed;
                    _text.Append(text.Text);
                    _spoken.Append(text.Text);
                    yield return new ReplyText(text.Text);
                }
                else if (_blocks.TryGetValue(delta.Index, out var block))
                {
                    if (delta.Delta.TryPickInputJson(out var json))
                    {
                        block.Json.Append(json.PartialJson);
                    }
                    else if (delta.Delta.TryPickThinking(out var thinking))
                    {
                        block.Json.Append(thinking.Thinking);
                    }
                    else if (delta.Delta.TryPickSignature(out var signature))
                    {
                        block.Signature = signature.Signature;
                    }
                }
            }
            else if (streamEvent.TryPickContentBlockStop(out var stop) && _blocks.Remove(stop.Index, out var finished))
            {
                if (finished.IsThinking)
                {
                    _thinking.Add(new ThinkingRecord(finished.Json.ToString(), finished.Signature));
                }
                else if (finished.ToolName is { } name)
                {
                    foreach (var replyEvent in FinishTool(finished, name, logger))
                    {
                        yield return replyEvent;
                    }
                }
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                _stopReason = messageDelta.Delta.StopReason?.Raw();
                if (messageDelta.Delta.StopDetails is { } details)
                {
                    _refusal = details.Explanation ?? details.Category?.Raw();
                }

                _outputTokens = messageDelta.Usage.OutputTokens;
                _inputTokens = Math.Max(_inputTokens, messageDelta.Usage.InputTokens ?? 0);
                _cacheRead = Math.Max(_cacheRead, messageDelta.Usage.CacheReadInputTokens ?? 0);
                _cacheWrite = Math.Max(_cacheWrite, messageDelta.Usage.CacheCreationInputTokens ?? 0);
            }
        }

        public ReplyCompleted Complete(TimeSpan elapsed, string requestedModel)
        {
            FlushSay();
            var stop = _stopReason switch
            {
                "tool_use" => ReplyStop.Actions,
                "max_tokens" => ReplyStop.MaxTokens,
                "refusal" => ReplyStop.Refused,
                _ => ReplyStop.EndTurn,
            };
            return new ReplyCompleted(
                stop,
                [.. _actions],
                new ReplyUsage(_inputTokens, _outputTokens, _cacheRead, _cacheWrite),
                _firstText,
                elapsed,
                _model ?? requestedModel,
                stop == ReplyStop.Refused ? _refusal : null);
        }

        public ConversationTurn ToTurn(string userText, byte[]? png, DateTimeOffset at) => new(
            userText,
            png,
            _spoken.ToString(),
            Completed ? [.. _toolCalls] : [],
            at,
            Interrupted: !Completed,
            Thinking: Completed ? [.. _thinking] : []);

        private IEnumerable<ReplyEvent> FinishTool(Block block, string name, ILogger logger)
        {
            FlushSay();
            var json = block.Json.Length == 0 ? "{}" : block.Json.ToString();

            // Every tool_use must be answered next turn, valid or not, so record it either way.
            _toolCalls.Add(new ToolCallRecord(block.ToolId!, name, ValidJsonOrEmpty(json)));
            var parsed = ActionParser.ParseToolCall(name, json);
            if (parsed.IsValid && screenshot is not null)
            {
                parsed = ActionBounds.Check(parsed.Action!, screenshot.Geometry.ImageWidth, screenshot.Geometry.ImageHeight);
            }

            if (parsed.IsValid)
            {
                _actions.Add(parsed.Action!);
                yield return new ReplyAction(parsed.Action!);
            }
            else
            {
                logger.LogWarning("Dropped {Action} from Claude: {Reason}. Input: {Json}", name, parsed.Error, json);
                yield return new ReplyActionRejected(name, parsed.Error!);
            }
        }

        private void FlushSay()
        {
            if (_text.Length > 0)
            {
                _actions.Add(new SayAction(_text.ToString()));
                _text.Clear();
            }
        }

        private static string ValidJsonOrEmpty(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind == JsonValueKind.Object ? json : "{}";
            }
            catch (JsonException)
            {
                return "{}";
            }
        }

        private sealed class Block
        {
            public string? ToolId { get; set; }

            public string? ToolName { get; set; }

            public bool IsThinking { get; set; }

            public string? Signature { get; set; }

            public StringBuilder Json { get; } = new();
        }
    }
}

/// <summary>The Anthropic key, kept in Credential Manager.</summary>
public static class AnthropicKey
{
    /// <summary>Credential Manager first, then the ANTHROPIC_API_KEY environment variable (development).</summary>
    public static string? Read(ISecretStore secrets)
    {
        var key = secrets.Read(SecretNames.AnthropicApiKey);
        if (string.IsNullOrWhiteSpace(key))
        {
            key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        }

        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }
}
