using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using Clicky.Core.Actions;
using Clicky.Core.Connectors;
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
/// Connected apps' tools (M16) are offered as plain tools after the actions, and on agent work the
/// click/type/key/scroll tools give way to Anthropic's computer toolset (see <see cref="ComputerToolset"/>).
/// History is sent back append-only with thinking blocks unchanged. The occasional trim (see
/// <see cref="HistoryLimits"/>) edits the front, which would make the API reject later thinking
/// blocks on newer accounts, so requests ask it to drop such blocks instead of failing.
/// </remarks>
public sealed class AnthropicApiBackend(ISecretStore secrets, ISettingsStore settings, ILogger<AnthropicApiBackend> logger, IConnectorTools? connectors = null)
    : IClaudeBackend, IDisposable
{
    /// <summary>Room for adaptive thinking plus a spoken answer; replies stream, so a large cap costs nothing.</summary>
    public const int MaxTokens = 16_000;

    /// <summary>No text or event for this long and the reply is abandoned.</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Refusals are retried by the server on its default fallback model, where the model has one.</summary>
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Lets a request ask for invalidated thinking blocks to be dropped rather than rejected.</summary>
    private const string ThinkingBindingBeta = "thinking-binding-controls-2026-08-01";

    /// <summary>How long the first request after launch waits for connectors that are still starting.</summary>
    private static readonly TimeSpan s_connectorWait = TimeSpan.FromSeconds(1.5);

    /// <summary>Clicky's own tools that the computer toolset replaces.</summary>
    private static readonly HashSet<string> s_toolsetReplaces = ["click", "type", "key", "scroll", "wait"];

    private static readonly Lazy<List<BetaToolUnion>> s_actionTools = new(() => BuildActionTools(toolset: false));
    private static readonly Lazy<List<BetaToolUnion>> s_toolsetActionTools = new(() => BuildActionTools(toolset: true));

    private readonly Lock _gate = new();
    private AnthropicClient? _client;
    private string? _clientKey;

    // Set when the API refused the computer toolset (an older model, or a platform without it); own tools from then on.
    private volatile bool _toolsetRefused;

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
        var current = settings.Current;
        var useToolset = current.Agent.ComputerToolset && current.Agent.Enabled && !_toolsetRefused;
        IReadOnlyList<ConnectorTool> connectorTools = connectors is null ? [] : await connectors.GetToolsAsync(s_connectorWait, cancellationToken).ConfigureAwait(false);
        var parameters = BuildParameters(conversation, request, userText, model, effort, connectorTools, useToolset);
        var results = DefaultToolResults.Resolve(conversation.PendingToolCalls, request.Step?.Results ?? conversation.UnsentResults);
        var limits = new HistoryLimits(current.Claude.MaxTurns);
        var reply = new StreamState(request.Screenshot, connectors);
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
                    if (useToolset && !reply.Started && IsToolsetRefusal(failure))
                    {
                        // Safety net: never let the toolset break Talk. Retry once with Clicky's own tools.
                        logger.LogWarning(ex, "The API refused the computer toolset for {Model}; using Clicky's own computer tools from now on", model);
                        _toolsetRefused = true;
                        useToolset = false;
                        parameters = BuildParameters(conversation, request, userText, model, effort, connectorTools, useToolset);
                        if (stream is not null)
                        {
                            await stream.DisposeAsync().ConfigureAwait(false);
                            stream = null;
                        }

                        continue;
                    }

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
                conversation.Append(reply.ToTurn(userText, request.Screenshot?.Png, conversation.Now, request.Attachments) with { Results = results.Count > 0 ? results : null }, limits);
            }
        }
    }

    public async Task<string> CompleteAsync(string system, string user, string model, CancellationToken cancellationToken = default)
    {
        var key = AnthropicKey.Read(secrets) ?? throw new ClaudeBackendException(ClaudeFailure.NotConfigured, "No Anthropic API key saved.");
        var parameters = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
            System = SystemBlocks(system, null),
            OutputConfig = new BetaOutputConfig { Effort = Effort.Low },
            Messages = [new BetaMessageParam { Role = Role.User, Content = new List<BetaContentBlockParam> { new BetaTextBlockParam { Text = user } } }],
        };
        try
        {
            var message = await Client(key).Beta.Messages.Create(parameters, cancellationToken).ConfigureAwait(false);
            var text = new StringBuilder();
            foreach (var block in message.Content)
            {
                if (block.TryPickText(out var part))
                {
                    text.Append(part.Text);
                }
            }

            return text.ToString();
        }
        catch (Exception ex) when (Describe(ex, model) is { } failure)
        {
            logger.LogWarning(ex, "Claude API completion failed ({Failure})", failure.Failure);
            throw failure;
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

    internal static MessageCreateParams BuildParameters(
        Conversation conversation,
        ClaudeRequest request,
        string userText,
        string model,
        ClaudeEffort effort,
        IReadOnlyList<ConnectorTool>? connectorTools = null,
        bool computerToolset = false)
    {
        var messages = new List<BetaMessageParam>();
        IReadOnlyList<ToolCallRecord> pending = [];
        foreach (var turn in conversation.Turns)
        {
            messages.Add(UserMessage(pending, DefaultToolResults.Resolve(pending, turn.Results), turn.Screenshot, turn.UserText, turn.Attachments));
            messages.Add(AssistantMessage(turn));
            pending = turn.ToolCalls;
        }

        messages.Add(UserMessage(pending, DefaultToolResults.Resolve(pending, request.Step?.Results ?? conversation.UnsentResults), request.Screenshot?.Png, userText, request.Attachments));

        return new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            Betas = [FallbackBeta, ThinkingBindingBeta],
            Fallbacks = new Default(),
            System = SystemBlocks(PromptLibrary.SystemPrompt(ActionDelivery.Tools), conversation.Memory),
            Tools = Tools(connectorTools ?? [], computerToolset),
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

    /// <summary>The fixed prompt (cached across conversations), then this conversation's memory.</summary>
    private static List<BetaTextBlockParam> SystemBlocks(string prompt, string? memory)
    {
        var blocks = new List<BetaTextBlockParam> { new() { Text = prompt, CacheControl = new BetaCacheControlEphemeral() } };
        if (memory is not null)
        {
            blocks.Add(new BetaTextBlockParam { Text = memory });
        }

        return blocks;
    }

    /// <param name="results">One per pending call, in the same order (see <see cref="DefaultToolResults.Resolve"/>).</param>
    private static BetaMessageParam UserMessage(IReadOnlyList<ToolCallRecord> pending, IReadOnlyList<ToolOutcome> results, byte[]? png, string text, IReadOnlyList<RequestAttachment>? attachments)
    {
        var content = new List<BetaContentBlockParam>();
        for (var i = 0; i < pending.Count; i++)
        {
            var result = results[i];
            content.Add(new BetaToolResultBlockParam
            {
                ToolUseID = pending[i].Id,
                Content = result.Text,
                IsError = result.IsError ? true : null,

                // Toolset results must name their toolset or the API rejects them.
                ToolsetName = pending[i].ToolsetName,
            });
        }

        // Documents and pictures before the screenshot and the question, like a person handing them over first.
        foreach (var attachment in attachments ?? [])
        {
            content.Add(new BetaTextBlockParam { Text = PromptLibrary.AttachmentText(attachment) });
            if (attachment is { Kind: AttachmentKind.Image, Image: { } image })
            {
                content.Add(new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(image), MediaType = ToMediaType(attachment.MediaType) },
                });
            }
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
                ToolsetName = call.ToolsetName,
                Input = input.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
            });
        }

        if (content.Count == 0 || content.All(c => c.TryPickThinking(out _) || c.TryPickRedactedThinking(out _)))
        {
            content.Add(new BetaTextBlockParam { Text = "(No answer.)" });
        }

        return new BetaMessageParam { Role = Role.Assistant, Content = content };
    }

    /// <summary>
    /// Actions first (fixed, so the cache prefix holds), then the toolset, then connector tools by
    /// name. Connector tools change only when connections do.
    /// </summary>
    private static List<BetaToolUnion> Tools(IReadOnlyList<ConnectorTool> connectorTools, bool computerToolset)
    {
        var tools = new List<BetaToolUnion>(computerToolset ? s_toolsetActionTools.Value : s_actionTools.Value);
        if (computerToolset)
        {
            tools.Add(ToolsetTool());
        }

        foreach (var tool in connectorTools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (tool.InputSchema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            tools.Add(new BetaTool
            {
                Name = tool.Name,
                Description = tool.Description,

                // Not strict: servers write schemas strict mode can't take. The server validates its own input.
                InputSchema = InputSchema.FromRawUnchecked(tool.InputSchema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())),
            });
        }

        return tools;
    }

    private static BetaComputerToolset20260801 ToolsetTool() => new()
    {
        Configs = new BetaComputerToolsetConfigs
        {
            Zoom = new BetaComputerZoomConfig { Enabled = false },
            MouseMove = new BetaComputerMouseMoveConfig { Enabled = false },
            LeftClickDrag = new BetaComputerLeftClickDragConfig { Enabled = false },
            LeftMouseDown = new BetaComputerLeftMouseDownConfig { Enabled = false },
            LeftMouseUp = new BetaComputerLeftMouseUpConfig { Enabled = false },
            HoldKey = new BetaComputerHoldKeyConfig { Enabled = false },
            CursorPosition = new BetaComputerCursorPositionConfig { Enabled = false },
        },
    };

    private static bool IsToolsetRefusal(ClaudeBackendException failure) =>
        failure.Failure == ClaudeFailure.BadRequest
        && (failure.Message.Contains("toolset", StringComparison.OrdinalIgnoreCase) || failure.Message.Contains("computer", StringComparison.OrdinalIgnoreCase));

    private static List<BetaToolUnion> BuildActionTools(bool toolset)
    {
        var schema = ActionSchema.Default;
        var tools = new List<BetaToolUnion>();

        // say is the plain text; use_tool is for action tags (here connector tools are real tools).
        foreach (var name in schema.Names.Where(n => n is not ("say" or "use_tool") && !(toolset && s_toolsetReplaces.Contains(n))))
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

    private static MediaType ToMediaType(string? mediaType) => mediaType switch
    {
        "image/jpeg" => MediaType.ImageJpeg,
        "image/gif" => MediaType.ImageGif,
        "image/webp" => MediaType.ImageWebP,
        _ => MediaType.ImagePng,
    };

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
    private sealed class StreamState(RequestScreenshot? screenshot, IConnectorTools? connectors)
    {
        private readonly Dictionary<long, Block> _blocks = [];
        private readonly List<ClickyAction> _actions = [];
        private readonly List<ToolCallRecord> _toolCalls = [];
        private readonly List<ReplyToolCall> _calls = [];
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

        /// <summary>The API accepted the request (the stream started).</summary>
        public bool Started { get; private set; }

        public IEnumerable<ReplyEvent> Handle(BetaRawMessageStreamEvent streamEvent, TimeSpan elapsed, ILogger logger)
        {
            Started = true;
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
                    block.ToolsetName = toolUse.ToolsetName;
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
                stop == ReplyStop.Refused ? _refusal : null,
                [.. _calls]);
        }

        public ConversationTurn ToTurn(string userText, byte[]? png, DateTimeOffset at, IReadOnlyList<RequestAttachment>? attachments) => new(
            userText,
            png,
            _spoken.ToString(),
            Completed ? [.. _toolCalls] : [],
            at,
            Interrupted: !Completed,
            Thinking: Completed ? [.. _thinking] : [],
            Attachments: attachments is { Count: > 0 } ? attachments : null);

        private IEnumerable<ReplyEvent> FinishTool(Block block, string name, ILogger logger)
        {
            FlushSay();
            var json = block.Json.Length == 0 ? "{}" : block.Json.ToString();
            var parsed = Parse(block, name, json);
            if (parsed.IsValid && screenshot is not null)
            {
                parsed = ActionBounds.Check(parsed.Action!, screenshot.Geometry.ImageWidth, screenshot.Geometry.ImageHeight);
            }

            // Every tool_use must be answered next turn, valid or not, so record it either way.
            var call = new ToolCallRecord(block.ToolId!, name, ValidJsonOrEmpty(json), parsed.Error is null, parsed.Error, block.ToolsetName);
            _toolCalls.Add(call);
            if (parsed.IsValid)
            {
                _actions.Add(parsed.Action!);
                yield return new ReplyAction(parsed.Action!);
            }
            else if (parsed.Error is not null)
            {
                logger.LogWarning("Dropped {Action} from Claude: {Reason}. Input: {Json}", name, parsed.Error, json);
                yield return new ReplyActionRejected(name, parsed.Error);
            }

            var toolCall = new ReplyToolCall(call, parsed.Action);
            _calls.Add(toolCall);
            yield return toolCall;
        }

        /// <summary>A toolset member, a connector tool or one of Clicky's actions. A toolset screenshot is neither valid nor an error.</summary>
        private ActionParseResult Parse(Block block, string name, string json)
        {
            if (block.ToolsetName == ComputerToolset.ToolsetName)
            {
                var (action, error) = ComputerToolset.Map(name, json);
                return action is not null ? ActionParseResult.Valid(action) : new ActionParseResult(null, name, error);
            }

            if (connectors?.IsConnectorTool(name) == true)
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        ? ActionParseResult.Valid(new UseToolAction(name, document.RootElement.Clone()))
                        : ActionParseResult.Invalid(name, "input must be an object");
                }
                catch (JsonException ex)
                {
                    return ActionParseResult.Invalid(name, $"input is not valid JSON ({ex.Message})");
                }
            }

            return ActionParser.ParseToolCall(name, json);
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

            public string? ToolsetName { get; set; }

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
