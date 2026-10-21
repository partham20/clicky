using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core.Actions;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Assistant.ClaudeCode;

/// <summary>
/// Asks Claude through Claude Code run headless (<c>claude -p</c>) on the user's subscription, so
/// there is no API bill. Claude Code takes no custom tools, so the model writes actions as inline
/// &lt;action&gt; tags that go through the same schema validation as the API backend's tools.
/// </summary>
/// <remarks>
/// <para>
/// Starting Claude Code takes 2-3 s, so one process is kept per conversation: <see cref="Prepare"/>
/// starts it when the user presses Talk (hidden behind their speaking), later turns reuse it, and
/// the router's model is switched with a control message (about 0.1 s) instead of a restart.
/// </para>
/// <para>
/// It runs with no tools, no MCP servers, no settings, CLAUDE.md, skills or hooks (--safe-mode), in
/// an empty folder, without saving the session to disk, and without the API key variables so it
/// bills the subscription. The screenshot goes in over stdin as base64; nothing is written to disk
/// except the system prompt.
/// </para>
/// </remarks>
public sealed class ClaudeCodeBackend : IClaudeBackend, IDisposable
{
    /// <summary>No output for this long and the reply is abandoned (a cold start can take a while).</summary>
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(30);

    /// <summary>After an interrupt, how long to wait for Claude Code to wrap up before killing it.</summary>
    public static readonly TimeSpan InterruptGrace = TimeSpan.FromSeconds(3);

    private static readonly string[] s_removeEnvironment = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT"];

    private readonly ISettingsStore _settings;
    private readonly string _directory;
    private readonly ILogger<ClaudeCodeBackend> _logger;
    private readonly Func<ClaudeCliStartInfo, IClaudeCliProcess> _start;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _idle;
    private ClaudeCliSession? _session;

    public ClaudeCodeBackend(ISettingsStore settings, ClickyPaths paths, ILogger<ClaudeCodeBackend> logger)
        : this(settings, paths.ClaudeCodeDirectory, logger, ClaudeCliProcess.Start)
    {
    }

    internal ClaudeCodeBackend(ISettingsStore settings, string directory, ILogger<ClaudeCodeBackend> logger, Func<ClaudeCliStartInfo, IClaudeCliProcess> start)
    {
        _settings = settings;
        _directory = directory;
        _logger = logger;
        _start = start;
        _idle = new Timer(_ => CloseIfIdle());
    }

    public ClaudeBackendKind Kind => ClaudeBackendKind.Subscription;

    public void Prepare(Conversation conversation)
    {
        try
        {
            lock (_gate)
            {
                if (IsUsable(_session, conversation))
                {
                    return;
                }
            }

            _ = Task.Run(() => SessionFor(conversation, _settings.Current.Claude.SubscriptionFastModel, CancellationToken.None))
                .ContinueWith(t => _logger.LogWarning(t.Exception?.GetBaseException(), "Could not start Claude Code ahead of time"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not start Claude Code ahead of time");
        }
    }

    public Task<BackendCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        var path = ClaudeCliLocator.Find(_settings.Current.Claude.CliPath);
        if (path is not null)
        {
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            return Task.FromResult(new BackendCheck(true, $"Claude Code found{(string.IsNullOrWhiteSpace(version) ? string.Empty : $" ({version})")}: {path}"));
        }

        return Task.FromResult(new BackendCheck(false, NotInstalledMessage()));
    }

    public async IAsyncEnumerable<ReplyEvent> ReplyAsync(
        Conversation conversation,
        ClaudeRequest request,
        string model,
        ClaudeEffort effort,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Claude Code sets its own thinking effort per model; effort applies to the API backend only.
        _ = effort;
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        var limits = new HistoryLimits(_settings.Current.Claude.MaxTurns);
        var userText = PromptLibrary.UserText(request);
        var reply = new TurnState(request.Screenshot);
        var stopwatch = Stopwatch.StartNew();
        ClaudeCliSession? session = null;
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            try
            {
                session = await SessionFor(conversation, model, stall.Token).ConfigureAwait(false);
                if (!string.Equals(session.Model, model, StringComparison.OrdinalIgnoreCase))
                {
                    await session.ControlAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = model }, ControlTimeout, stall.Token).ConfigureAwait(false);
                    _logger.LogDebug("Claude Code switched from {From} to {To}", session.Model, model);
                    session.Model = model;
                }

                await session.SendUserAsync(UserContent(conversation, session, request, userText), stall.Token).ConfigureAwait(false);
                session.TurnsSeen = conversation.Turns.Count + 1;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Close(session);
                throw new ClaudeBackendException(ClaudeFailure.Timeout, "Claude Code took too long to start.");
            }

            while (true)
            {
                (bool Ok, JsonElement Message) next;
                try
                {
                    next = await session.ReadAsync(stall.Token).ConfigureAwait(false);
                    stall.CancelAfter(StallTimeout);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    Close(session);
                    throw new ClaudeBackendException(ClaudeFailure.Timeout, $"Claude Code went quiet for {StallTimeout.TotalSeconds:0} s, so Clicky gave up on that answer.");
                }

                if (!next.Ok)
                {
                    var tail = session.ErrorTail;
                    Close(session);
                    _logger.LogWarning("Claude Code exited mid-reply (code {Code}): {Errors}", session.ExitCode, tail);
                    throw new ClaudeBackendException(ClaudeFailure.Failed, $"Claude Code stopped unexpectedly.{(tail.Length > 0 ? " " + LastLine(tail) : string.Empty)}");
                }

                foreach (var replyEvent in reply.Handle(next.Message, stopwatch.Elapsed, _logger))
                {
                    yield return replyEvent;
                }

                if (reply.Result is not null)
                {
                    break;
                }
            }

            if (reply.Failure is { } failure)
            {
                if (failure.Failure is ClaudeFailure.Unauthorized or ClaudeFailure.NotConfigured)
                {
                    Close(session);
                }

                throw failure;
            }

            reply.Completed = true;
            var completed = reply.Complete(stopwatch.Elapsed, model);
            _logger.LogInformation(
                "Claude Code {Model} replied in {Elapsed} ms (first text {FirstText} ms): stop {Stop}, {Actions} actions, tokens in {In} (cache read {CacheRead}) out {Out}, ${Cost}",
                completed.Model,
                (long)completed.Elapsed.TotalMilliseconds,
                completed.FirstText is { } first ? (long)first.TotalMilliseconds : -1,
                completed.Stop,
                completed.Actions.Count(a => a is not SayAction),
                completed.Usage.InputTokens,
                completed.Usage.CacheReadTokens,
                completed.Usage.OutputTokens,
                completed.Usage.CostUsd);
            yield return completed;
        }
        finally
        {
            if (!reply.Completed && session is not null && cancellationToken.IsCancellationRequested)
            {
                await InterruptAsync(session, reply).ConfigureAwait(false);
            }

            if (reply.Completed || reply.HasText)
            {
                conversation.Append(new ConversationTurn(userText, request.Screenshot?.Png, reply.SpokenText, [], conversation.Now, Interrupted: !reply.Completed), limits);
                if (session is not null)
                {
                    session.TurnsSeen = conversation.Turns.Count;
                }
            }

            _idle.Change(TimeSpan.FromMinutes(_settings.Current.Claude.IdleResetMinutes), Timeout.InfiniteTimeSpan);
            _turn.Release();
        }
    }

    public Task<string> CompleteAsync(string system, string user, string model, CancellationToken cancellationToken = default) =>
        CompleteAsync(system, user, model, tools: string.Empty, cancellationToken);

    /// <summary>
    /// <see cref="CompleteAsync(string, string, string, CancellationToken)"/> with some of Claude
    /// Code's built-in tools allowed, e.g. "WebSearch" for the suggestions research (M17).
    /// </summary>
    /// <param name="tools">Comma-separated built-in tool names; empty for none.</param>
    public async Task<string> CompleteAsync(string system, string user, string model, string tools, CancellationToken cancellationToken = default)
    {
        // A one-off process of its own, so it never waits behind (or disturbs) a spoken reply.
        var executable = ClaudeCliLocator.Find(_settings.Current.Claude.CliPath)
            ?? throw new ClaudeBackendException(ClaudeFailure.NotConfigured, NotInstalledMessage());
        Directory.CreateDirectory(_directory);
        var arguments = Arguments(model, string.Empty).ToList();
        var promptAt = arguments.IndexOf("--system-prompt-file");
        arguments[promptAt] = "--system-prompt";
        arguments[promptAt + 1] = system;
        arguments.Remove("--include-partial-messages");
        if (!string.IsNullOrEmpty(tools))
        {
            arguments[arguments.IndexOf("--tools") + 1] = tools;
            arguments.AddRange(["--allowedTools", tools]);
        }

        IClaudeCliProcess process;
        try
        {
            process = _start(new ClaudeCliStartInfo(executable, arguments, _directory, s_removeEnvironment));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new ClaudeBackendException(ClaudeFailure.NotConfigured, $"Could not start Claude Code ({executable}): {ex.Message}", ex);
        }

        using var session = new ClaudeCliSession(process, Guid.Empty, 0, model);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await session.SendUserAsync([new JsonObject { ["type"] = "text", ["text"] = user }], timeout.Token).ConfigureAwait(false);
            string? synthetic = null;
            while (true)
            {
                var next = await session.ReadAsync(timeout.Token).ConfigureAwait(false);
                if (!next.Ok)
                {
                    throw new ClaudeBackendException(ClaudeFailure.Failed, $"Claude Code stopped unexpectedly. {LastLine(session.ErrorTail)}".Trim());
                }

                var message = next.Message;
                var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "assistant" && message.TryGetProperty("message", out var assistant)
                    && assistant.TryGetProperty("model", out var m) && m.GetString() == "<synthetic>")
                {
                    synthetic = assistant.GetProperty("content").EnumerateArray().Select(b => b.TryGetProperty("text", out var x) ? x.GetString() : null).FirstOrDefault(x => x is not null);
                }
                else if (type == "result")
                {
                    var text = message.TryGetProperty("result", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                    if (message.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
                    {
                        throw TurnState.Classify(synthetic ?? text);
                    }

                    return text;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ClaudeBackendException(ClaudeFailure.Timeout, "Claude Code took too long to answer.");
        }
    }

    public void Dispose()
    {
        _idle.Dispose();
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }

        _turn.Dispose();
    }

    internal static IReadOnlyList<string> Arguments(string model, string systemPromptFile) =>
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--model", model,
        "--tools", string.Empty,
        "--system-prompt-file", systemPromptFile,
        "--safe-mode",
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--no-session-persistence",
    ];

    /// <summary>
    /// Earlier turns as text, for a fresh process joining a conversation that already has history
    /// (Claude Code restarted, or history was trimmed). Old screenshots are not resent.
    /// </summary>
    internal static string Transcript(IReadOnlyList<ConversationTurn> turns)
    {
        var text = new StringBuilder("(Earlier in this conversation, for context. Screenshots from these turns are not shown again.)\n");
        foreach (var turn in turns)
        {
            text.Append("\nUser: ").Append(turn.UserText).Append("\nClicky: ").Append(turn.AssistantText.Trim());
            if (turn.Interrupted)
            {
                text.Append(" (interrupted)");
            }

            text.Append('\n');
        }

        return text.Append("\n(End of earlier conversation.)").ToString();
    }

    private static JsonArray UserContent(Conversation conversation, ClaudeCliSession session, ClaudeRequest request, string userText)
    {
        var content = new JsonArray();
        var turns = conversation.Turns;

        // The system prompt is fixed when Claude Code starts, so memory rides on its first message.
        if (session.TurnsSeen == 0 && conversation.Memory is { } memory)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = memory });
        }

        if (session.TurnsSeen < turns.Count)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = Transcript(turns) });
        }

        if (request.Screenshot is { } shot)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(shot.Png) },
            });
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = userText });
        return content;
    }

    private static bool IsUsable(ClaudeCliSession? session, Conversation conversation) =>
        session is { IsAlive: true } && session.ConversationId == conversation.Id && session.Generation == conversation.Generation;

    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    private async Task<ClaudeCliSession> SessionFor(Conversation conversation, string model, CancellationToken cancellationToken)
    {
        ClaudeCliSession session;
        lock (_gate)
        {
            if (!IsUsable(_session, conversation))
            {
                _session?.Dispose();
                _session = StartSession(conversation, model);
            }

            session = _session!;
        }

        try
        {
            await session.Ready!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ClaudeBackendException ex)
        {
            Close(session);
            _logger.LogWarning("Claude Code failed to start (code {Code}): {Errors}", session.ExitCode, session.ErrorTail);
            throw new ClaudeBackendException(ex.Failure, $"Claude Code failed to start. {LastLine(session.ErrorTail)}".Trim(), ex);
        }

        return session;
    }

    private ClaudeCliSession StartSession(Conversation conversation, string model)
    {
        var executable = ClaudeCliLocator.Find(_settings.Current.Claude.CliPath)
            ?? throw new ClaudeBackendException(ClaudeFailure.NotConfigured, NotInstalledMessage());
        var promptFile = WriteSystemPrompt();
        var stopwatch = Stopwatch.StartNew();
        IClaudeCliProcess process;
        try
        {
            process = _start(new ClaudeCliStartInfo(executable, Arguments(model, promptFile), _directory, s_removeEnvironment));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new ClaudeBackendException(ClaudeFailure.NotConfigured, $"Could not start Claude Code ({executable}): {ex.Message}", ex);
        }

        var session = new ClaudeCliSession(process, conversation.Id, conversation.Generation, model);
        session.Ready = InitializeAsync(session, stopwatch);
        _logger.LogInformation("Started Claude Code for conversation {Conversation} with {Model}", conversation.Id, model);
        return session;
    }

    private async Task InitializeAsync(ClaudeCliSession session, Stopwatch stopwatch)
    {
        await session.ControlAsync(new JsonObject { ["subtype"] = "initialize" }, StallTimeout, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Claude Code ready in {Elapsed} ms", stopwatch.ElapsedMilliseconds);
    }

    private string WriteSystemPrompt()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "system-prompt.md");
        var prompt = PromptLibrary.SystemPrompt(ActionDelivery.Tags);
        if (!File.Exists(path) || File.ReadAllText(path) != prompt)
        {
            File.WriteAllText(path, prompt, new UTF8Encoding(false));
        }

        return path;
    }

    private async Task InterruptAsync(ClaudeCliSession session, TurnState reply)
    {
        try
        {
            using var grace = new CancellationTokenSource(InterruptGrace);
            await session.ControlAsync(new JsonObject { ["subtype"] = "interrupt" }, InterruptGrace, grace.Token).ConfigureAwait(false);

            // Read to the end of the cancelled turn so the next one starts clean.
            while (reply.Result is null)
            {
                var next = await session.ReadAsync(grace.Token).ConfigureAwait(false);
                if (!next.Ok)
                {
                    break;
                }

                reply.Observe(next.Message);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ClaudeBackendException)
        {
            _logger.LogDebug("Claude Code did not wind down after an interrupt; restarting it next time");
            Close(session);
        }
    }

    private void Close(ClaudeCliSession? session)
    {
        if (session is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }
        }

        session.Dispose();
    }

    private void CloseIfIdle()
    {
        ClaudeCliSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            _logger.LogInformation("Closing idle Claude Code");
            session.Dispose();
        }
    }

    private static string NotInstalledMessage() =>
        ClaudeCliLocator.FindNpmShim() is { } npm
            ? $"Found Claude Code installed with npm ({npm}), which Clicky can't start directly. Install the native build (claude install) or set claude.cliPath to claude.exe."
            : "Claude Code isn't installed. Install it from claude.com/code and sign in once by running claude in a terminal.";

    /// <summary>Turns Claude Code's stream-json output for one turn into reply events.</summary>
    private sealed class TurnState(RequestScreenshot? screenshot)
    {
        private readonly ActionTagReader _tags = new();
        private readonly List<ClickyAction> _actions = [];
        private readonly StringBuilder _say = new();
        private readonly StringBuilder _spoken = new();
        private readonly HashSet<string> _streamedMessages = [];
        private string? _currentMessage;
        private TimeSpan? _firstText;
        private string? _model;
        private string? _syntheticError;

        public JsonElement? Result { get; private set; }

        public ClaudeBackendException? Failure { get; private set; }

        public bool Completed { get; set; }

        public bool HasText => _spoken.Length > 0;

        public string SpokenText => _spoken.ToString();

        /// <summary>Records the end of a turn without producing events (used while winding down).</summary>
        public void Observe(JsonElement message)
        {
            if (Type(message) == "result")
            {
                Result = message;
            }
        }

        public IEnumerable<ReplyEvent> Handle(JsonElement message, TimeSpan elapsed, ILogger logger)
        {
            switch (Type(message))
            {
                case "system" when Str(message, "subtype") == "init":
                    _model = Str(message, "model");
                    break;
                case "stream_event" when message.TryGetProperty("event", out var streamEvent):
                    foreach (var replyEvent in HandleStreamEvent(streamEvent, elapsed, logger))
                    {
                        yield return replyEvent;
                    }

                    break;
                case "assistant" when message.TryGetProperty("message", out var assistant):
                    foreach (var replyEvent in HandleAssistant(assistant, elapsed, logger))
                    {
                        yield return replyEvent;
                    }

                    break;
                case "result":
                    foreach (var replyEvent in Push(null, elapsed, logger, flush: true))
                    {
                        yield return replyEvent;
                    }

                    Result = message;
                    if (message.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                    {
                        Failure = Classify(_syntheticError ?? Str(message, "result") ?? Str(message, "subtype") ?? "unknown error");
                    }

                    break;
            }
        }

        public ReplyCompleted Complete(TimeSpan elapsed, string requestedModel)
        {
            FlushSay();
            var result = Result!.Value;
            var usage = ReplyUsage.None;
            if (result.TryGetProperty("usage", out var u))
            {
                usage = new ReplyUsage(Long(u, "input_tokens"), Long(u, "output_tokens"), Long(u, "cache_read_input_tokens"), Long(u, "cache_creation_input_tokens"));
            }

            if (result.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
            {
                usage = usage with { CostUsd = cost.GetDecimal() };
            }

            var stop = Str(result, "stop_reason") switch
            {
                "max_tokens" => ReplyStop.MaxTokens,
                "refusal" => ReplyStop.Refused,
                _ => _actions.Any(a => a.ControlsComputer || a is AskPermissionAction) ? ReplyStop.Actions : ReplyStop.EndTurn,
            };
            return new ReplyCompleted(stop, [.. _actions], usage, _firstText, elapsed, _model ?? requestedModel);
        }

        internal static ClaudeBackendException Classify(string text)
        {
            var lower = text.ToLowerInvariant();
            if (lower.Contains("authenticat", StringComparison.Ordinal) || lower.Contains("/login", StringComparison.Ordinal)
                || lower.Contains("oauth", StringComparison.Ordinal) || lower.Contains("not logged in", StringComparison.Ordinal)
                || lower.Contains("invalid api key", StringComparison.Ordinal))
            {
                return new(ClaudeFailure.Unauthorized, "Claude Code isn't signed in on this PC. Open a terminal, run claude, and sign in with /login.");
            }

            if (lower.Contains("usage limit", StringComparison.Ordinal) || lower.Contains("limit reached", StringComparison.Ordinal) || lower.Contains("rate limit", StringComparison.Ordinal))
            {
                return new(ClaudeFailure.RateLimited, $"Your Claude subscription hit its usage limit: {text.Trim()}");
            }

            if (lower.Contains("overloaded", StringComparison.Ordinal))
            {
                return new(ClaudeFailure.Unavailable, "Claude is overloaded right now. Try again in a moment.");
            }

            return new(ClaudeFailure.Failed, $"Claude Code failed: {text.Trim()}");
        }

        private IEnumerable<ReplyEvent> HandleStreamEvent(JsonElement streamEvent, TimeSpan elapsed, ILogger logger)
        {
            switch (Str(streamEvent, "type"))
            {
                case "message_start" when streamEvent.TryGetProperty("message", out var message):
                    _currentMessage = Str(message, "id");
                    _model = Str(message, "model") ?? _model;
                    break;
                case "content_block_delta" when streamEvent.TryGetProperty("delta", out var delta) && Str(delta, "type") == "text_delta":
                    if (_currentMessage is not null)
                    {
                        _streamedMessages.Add(_currentMessage);
                    }

                    foreach (var replyEvent in Push(Str(delta, "text"), elapsed, logger, flush: false))
                    {
                        yield return replyEvent;
                    }

                    break;
                case "content_block_stop":
                    // A text block ended: release any held-back tail (a lone "<").
                    foreach (var replyEvent in Push(null, elapsed, logger, flush: true))
                    {
                        yield return replyEvent;
                    }

                    break;
            }
        }

        private IEnumerable<ReplyEvent> HandleAssistant(JsonElement assistant, TimeSpan elapsed, ILogger logger)
        {
            var text = assistant.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? string.Concat(content.EnumerateArray().Where(b => Str(b, "type") == "text").Select(b => Str(b, "text")))
                : string.Empty;
            if (Str(assistant, "model") == "<synthetic>")
            {
                // Claude Code's own error (sign-in, limits), dressed up as a reply. Not spoken.
                _syntheticError = text;
                yield break;
            }

            // Without partial messages (older Claude Code) the whole text only arrives here.
            if (Str(assistant, "id") is { } id && !_streamedMessages.Contains(id) && text.Length > 0)
            {
                foreach (var replyEvent in Push(text, elapsed, logger, flush: true))
                {
                    yield return replyEvent;
                }
            }
        }

        private IEnumerable<ReplyEvent> Push(string? chunk, TimeSpan elapsed, ILogger logger, bool flush)
        {
            var tokens = new List<TagReaderToken>();
            if (!string.IsNullOrEmpty(chunk))
            {
                tokens.AddRange(_tags.Push(chunk));
            }

            if (flush)
            {
                tokens.AddRange(_tags.Flush());
            }

            foreach (var token in tokens)
            {
                if (!token.IsAction)
                {
                    if (token.Text.Length == 0)
                    {
                        continue;
                    }

                    _firstText ??= elapsed;
                    _say.Append(token.Text);
                    _spoken.Append(token.Text);
                    yield return new ReplyText(token.Text);
                    continue;
                }

                FlushSay();
                var parsed = ActionParser.Parse(token.Text);
                if (parsed.IsValid && parsed.Action is SayAction say)
                {
                    // A say tag is just speech written the long way.
                    _firstText ??= elapsed;
                    _say.Append(say.Text);
                    _spoken.Append(say.Text);
                    yield return new ReplyText(say.Text);
                    continue;
                }

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
                    logger.LogWarning("Dropped {Action} from Claude Code: {Reason}. Tag: {Json}", parsed.Name, parsed.Error, token.Text);
                    yield return new ReplyActionRejected(parsed.Name, parsed.Error!);
                }
            }
        }

        private void FlushSay()
        {
            if (_say.Length > 0)
            {
                _actions.Add(new SayAction(_say.ToString()));
                _say.Clear();
            }
        }

        private static string? Type(JsonElement message) => Str(message, "type");

        private static string? Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static long Long(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    }
}
