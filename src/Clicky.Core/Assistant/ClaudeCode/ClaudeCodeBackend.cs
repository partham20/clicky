using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core.Actions;
using Clicky.Core.Capture;
using Clicky.Core.Connectors;
using Clicky.Core.Prompts;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Clicky.Core.Assistant.ClaudeCode;

/// <summary>
/// Asks Claude through Claude Code run headless (<c>claude -p</c>) on the user's subscription, so
/// there is no API bill. Claude Code takes no custom tools, so the model writes actions as inline
/// &lt;action&gt; tags that go through the same schema validation as the API backend's tools.
/// Connected apps' tools (M16) are listed in the conversation and called with use_tool tags, which
/// the agent loop runs behind the permission gate.
/// </summary>
/// <remarks>
/// <para>
/// Starting Claude Code takes 2-6 s, so nobody should wait for it. One process holds the
/// conversation and later turns reuse it; the router's model, effort and thinking are switched with
/// control messages (a few ms) instead of a restart. A fresh spare is started ahead of need: at
/// startup and after an idle conversation is closed (settings.claude.keepWarm), when the user presses
/// Talk without one, and when the process's history has grown heavy (every screenshot in it is
/// uploaded again with each request). A new conversation, or the next new question after the
/// history grew heavy, moves to the spare once it is ready, with the earlier turns as text. A process
/// is never replaced in the middle of an answer or an agent's steps.
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

    /// <summary>The connector tool list is capped so a server with huge schemas can't crowd out the conversation.</summary>
    internal const int MaxCatalogChars = 40_000;

    internal const int MaxSchemaChars = 2_000;

    /// <summary>Screenshots and pictures a process may hold before the next new question moves to a fresh one.</summary>
    internal const int MaxSessionImages = 6;

    /// <summary>Context a process may carry before the next new question moves to a fresh one.</summary>
    internal const long MaxSessionTokens = 60_000;

    private static readonly string[] s_removeEnvironment = ["ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT"];

    private static readonly TimeSpan s_connectorWait = TimeSpan.FromSeconds(1.5);

    private readonly ISettingsStore _settings;
    private readonly string _directory;
    private readonly ILogger<ClaudeCodeBackend> _logger;
    private readonly Func<ClaudeCliStartInfo, IClaudeCliProcess> _start;
    private readonly IConnectorTools? _connectors;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Timer _idle;

    // The process holding the conversation, and a spare starting up for the next one. Under _gate.
    private ClaudeCliSession? _session;
    private ClaudeCliSession? _spare;

    public ClaudeCodeBackend(ISettingsStore settings, ClickyPaths paths, ILogger<ClaudeCodeBackend> logger, IConnectorTools? connectors = null)
        : this(settings, paths.ClaudeCodeDirectory, logger, ClaudeCliProcess.Start, connectors)
    {
    }

    internal ClaudeCodeBackend(
        ISettingsStore settings,
        string directory,
        ILogger<ClaudeCodeBackend> logger,
        Func<ClaudeCliStartInfo, IClaudeCliProcess> start,
        IConnectorTools? connectors = null)
    {
        _settings = settings;
        _directory = directory;
        _logger = logger;
        _start = start;
        _connectors = connectors;
        _idle = new Timer(_ => CloseIfIdle());
    }

    public ClaudeBackendKind Kind => ClaudeBackendKind.Subscription;

    /// <summary>A spare process is started and initialized (for tests).</summary>
    internal bool SpareReady
    {
        get
        {
            lock (_gate)
            {
                return _spare is { IsReady: true };
            }
        }
    }

    public void Prepare(Conversation conversation)
    {
        try
        {
            lock (_gate)
            {
                var current = _session;
                var ready = Holds(current, conversation) ? !Worn(current!, conversation) : IsFresh(current);
                if (ready)
                {
                    return;
                }
            }

            // Out of the caller's way: finding and starting claude.exe touches the disk.
            _ = Task.Run(() =>
            {
                lock (_gate)
                {
                    EnsureSpare();
                }
            }).ContinueWith(t => _logger.LogWarning(t.Exception?.GetBaseException(), "Could not start Claude Code ahead of time"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Could not start Claude Code ahead of time");
        }
    }

    /// <summary>
    /// Keeps a Claude Code process started while Claude Code is the backend and settings.claude.keepWarm
    /// is on, or lets the idle spare go when either is off. Call at startup and after settings change.
    /// Never throws.
    /// </summary>
    public void WarmUp()
    {
        try
        {
            ClaudeCliSession? released = null;
            lock (_gate)
            {
                if (WantsWarm())
                {
                    if (!(_session?.IsAlive ?? false))
                    {
                        EnsureSpare();
                    }
                }
                else if (_spare is not null)
                {
                    released = _spare;
                    _spare = null;
                }
            }

            if (released is not null)
            {
                _logger.LogInformation("Closing the spare Claude Code (keepWarm is off or Claude Code is not the backend)");
                released.Dispose();
            }
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
        bool thinking,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _turn.WaitAsync(cancellationToken).ConfigureAwait(false);
        var limits = new HistoryLimits(_settings.Current.Claude.MaxTurns);
        var userText = PromptLibrary.UserText(request, ActionDelivery.Tags);
        var reply = new TurnState(request.Screenshot);
        var stopwatch = Stopwatch.StartNew();
        var sent = TimeSpan.Zero;
        ClaudeCliSession? session = null;
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(StallTimeout);
            try
            {
                // Connected apps settle while the process is found and set up.
                var connectorTools = _connectors?.GetToolsAsync(s_connectorWait, stall.Token);
                _ = connectorTools?.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

                // An agent's next step stays in the process that holds its earlier screenshots.
                session = await SessionFor(conversation, newQuestion: request.Step is null, stall.Token).ConfigureAwait(false);
                await AlignAsync(session, model, effort, thinking, stall.Token).ConfigureAwait(false);

                IReadOnlyList<ConnectorTool> tools = connectorTools is null ? [] : await connectorTools.ConfigureAwait(false);
                if (request.Tools is { Count: > 0 } extra)
                {
                    tools = [.. tools, .. extra];
                }

                await session.SendUserAsync(UserContent(conversation, session, request, userText, tools), stall.Token).ConfigureAwait(false);
                session.TurnsSeen = conversation.Turns.Count + 1;
                sent = stopwatch.Elapsed;
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
            session.ContextTokens = completed.Usage.InputTokens + completed.Usage.CacheReadTokens + completed.Usage.CacheWriteTokens;
            _logger.LogInformation(
                "Claude Code {Model} replied in {Elapsed} ms (sent {Sent} ms, first token {FirstToken} ms, first text {FirstText} ms, thinking {Thinking}): stop {Stop}, {Actions} actions, tokens in {In} (cache read {CacheRead}) out {Out}, ${Cost}",
                completed.Model,
                (long)completed.Elapsed.TotalMilliseconds,
                (long)sent.TotalMilliseconds,
                reply.FirstToken is { } token ? (long)token.TotalMilliseconds : -1,
                completed.FirstText is { } first ? (long)first.TotalMilliseconds : -1,
                session.Thinking switch { true => "on", false => "off", null => "default" },
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
                conversation.Append(
                    new ConversationTurn(
                        userText,
                        request.Screenshot?.Image,
                        reply.SpokenText,
                        [],
                        conversation.Now,
                        Interrupted: !reply.Completed,
                        Attachments: request.Attachments is { Count: > 0 } attachments ? attachments : null),
                    limits);
                if (session is not null)
                {
                    session.TurnsSeen = conversation.Turns.Count;
                }
            }

            if (session is { IsAlive: true } && Worn(session, conversation))
            {
                // Ready by the next question, which moves to it.
                StartSpareSafely();
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
        var arguments = Arguments(model, ClaudeEffort.Low, string.Empty).ToList();
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

        using var session = new ClaudeCliSession(process, model, ClaudeEffort.Low);
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
            _spare?.Dispose();
            _spare = null;
        }

        _turn.Dispose();
    }

    internal static IReadOnlyList<string> Arguments(string model, ClaudeEffort effort, string systemPromptFile) =>
    [
        "-p",
        "--input-format", "stream-json",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages",
        "--model", model,
        "--effort", EffortLevel(effort),
        "--tools", string.Empty,
        "--system-prompt-file", systemPromptFile,
        "--safe-mode",
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--no-session-persistence",
    ];

    /// <summary>
    /// Earlier turns as text, for a fresh process joining a conversation that already has history
    /// (Claude Code restarted, or history was trimmed). Old screenshots and pictures are not resent;
    /// documents history still holds are.
    /// </summary>
    internal static string Transcript(IReadOnlyList<ConversationTurn> turns)
    {
        var text = new StringBuilder("(Earlier in this conversation, for context. Screenshots from these turns are not shown again.)\n");
        foreach (var turn in turns)
        {
            foreach (var attachment in turn.Attachments ?? [])
            {
                text.Append('\n').Append(PromptLibrary.AttachmentText(attachment.Kind == AttachmentKind.Image ? attachment.WithoutContent() : attachment)).Append('\n');
            }

            text.Append("\nUser: ").Append(turn.UserText).Append("\nClicky: ").Append(turn.AssistantText.Trim());
            if (turn.Interrupted)
            {
                text.Append(" (interrupted)");
            }

            text.Append('\n');
        }

        return text.Append("\n(End of earlier conversation.)").ToString();
    }

    /// <summary>
    /// The connected apps' tools as text for the model, and a key that changes when they do. Null
    /// text with a null key: nothing to say (no tools now, none shown before).
    /// </summary>
    internal static (string? Text, string? Key) ToolCatalog(IReadOnlyList<ConnectorTool> tools, string? shownKey)
    {
        if (tools.Count == 0)
        {
            return shownKey is null ? (null, null) : ("[No connected app tools are available any more.]", string.Empty);
        }

        var ordered = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var key = string.Join('\n', ordered.Select(t => $"{t.Name}\u0001{t.Description}\u0001{t.InputSchema.GetRawText()}"));
        if (key == shownKey)
        {
            return (null, key);
        }

        var text = new StringBuilder(
            "[Connected app tools you can call. Write <action>{\"type\":\"use_tool\",\"name\":\"<tool name>\",\"input\":{...}}</action> " +
            "with input matching the tool's schema. Clicky runs it (asking the user first when it can change anything) and sends you the result. " +
            "These replace any tool list shown earlier.\n");
        foreach (var tool in ordered)
        {
            var schema = tool.InputSchema.GetRawText();
            if (schema.Length > MaxSchemaChars)
            {
                schema = schema[..MaxSchemaChars] + "…(schema cut short)";
            }

            var line = $"- {tool.Name}{(tool.ReadOnly ? " (read-only)" : string.Empty)}: {tool.Description.ReplaceLineEndings(" ").Trim()} Input schema: {schema}\n";
            if (text.Length + line.Length > MaxCatalogChars)
            {
                text.Append("- (more tools left out: the list is too long)\n");
                break;
            }

            text.Append(line);
        }

        return (text.Append(']').ToString(), key);
    }

    private static JsonArray UserContent(Conversation conversation, ClaudeCliSession session, ClaudeRequest request, string userText, IReadOnlyList<ConnectorTool> tools)
    {
        var content = new JsonArray();
        var turns = conversation.Turns;

        // The system prompt is fixed when Claude Code starts, so memory rides on its first message,
        // and a change after feedback ("you talk too much") on the next one.
        if (!string.Equals(session.Memory, conversation.Memory, StringComparison.Ordinal))
        {
            var memory = conversation.Memory ?? "(none)";
            var text = session.TurnsSeen == 0 ? memory : $"[Your notes about the user changed. These replace the earlier ones:]\n{memory}";
            if (session.TurnsSeen > 0 || conversation.Memory is not null)
            {
                content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }

            session.Memory = conversation.Memory;
        }

        var (catalog, key) = ToolCatalog(tools, session.ToolCatalogKey);
        if (catalog is not null)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = catalog });
        }

        session.ToolCatalogKey = key;

        if (session.TurnsSeen < turns.Count)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = Transcript(turns) });
        }

        foreach (var attachment in request.Attachments ?? [])
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = PromptLibrary.AttachmentText(attachment) });
            if (attachment is { Kind: AttachmentKind.Image, Image: { } image })
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = attachment.MediaType ?? ImageMediaType.Of(image), ["data"] = Convert.ToBase64String(image) },
                });
                session.ImagesSeen++;
            }
        }

        if (request.Screenshot is { } shot)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = shot.MediaType, ["data"] = Convert.ToBase64String(shot.Image) },
            });
            session.ImagesSeen++;
        }

        content.Add(new JsonObject { ["type"] = "text", ["text"] = userText });
        return content;
    }

    /// <summary>The live process holds this conversation.</summary>
    private static bool Holds(ClaudeCliSession? session, Conversation conversation) =>
        session is { IsAlive: true } && session.ConversationId == conversation.Id;

    /// <summary>A live process that has not taken a conversation yet.</summary>
    private static bool IsFresh(ClaudeCliSession? session) => session is { IsAlive: true, ConversationId: null };

    /// <summary>
    /// The process carries more than the conversation now needs: Clicky trimmed its own history, or
    /// the process's history is heavy to upload. The next new question moves to a fresh one.
    /// </summary>
    private static bool Worn(ClaudeCliSession session, Conversation conversation) =>
        session.Generation != conversation.Generation || session.ImagesSeen >= MaxSessionImages || session.ContextTokens >= MaxSessionTokens;

    /// <summary>Claude Code's --effort and effortLevel names.</summary>
    internal static string EffortLevel(ClaudeEffort effort) => effort switch
    {
        ClaudeEffort.Low => "low",
        ClaudeEffort.Medium => "medium",
        ClaudeEffort.High => "high",
        ClaudeEffort.XHigh => "xhigh",
        _ => "max",
    };

    private static string LastLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;

    private async Task<ClaudeCliSession> SessionFor(Conversation conversation, bool newQuestion, CancellationToken cancellationToken)
    {
        ClaudeCliSession session;
        ClaudeCliSession? retired = null;
        lock (_gate)
        {
            if (Holds(_session, conversation))
            {
                if (newQuestion && Worn(_session!, conversation) && _spare is { IsReady: true })
                {
                    _logger.LogInformation(
                        "Moving the conversation to a fresh Claude Code ({Images} images, {Tokens} tokens of context, generation {Seen}/{Now})",
                        _session!.ImagesSeen,
                        _session.ContextTokens,
                        _session.Generation,
                        conversation.Generation);
                    retired = _session;
                    _session = TakeSpare();
                }
            }
            else if (!IsFresh(_session))
            {
                var next = _spare is { IsAlive: true } ? TakeSpare() : StartSession(_settings.Current.Claude.SubscriptionFastModel);
                retired = _session;
                _session = next;
            }

            session = _session!;
            if (session.ConversationId is null)
            {
                session.Bind(conversation);
            }
        }

        retired?.Dispose();
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

    /// <summary>Puts the process on the router's model, effort and thinking; each is a control message, sent only on a change.</summary>
    private async Task AlignAsync(ClaudeCliSession session, string model, ClaudeEffort effort, bool thinking, CancellationToken cancellationToken)
    {
        if (!string.Equals(session.Model, model, StringComparison.OrdinalIgnoreCase))
        {
            await session.ControlAsync(new JsonObject { ["subtype"] = "set_model", ["model"] = model }, ControlTimeout, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Claude Code switched from {From} to {To}", session.Model, model);
            session.Model = model;
        }

        if (session.Effort != effort)
        {
            await session.ControlAsync(
                new JsonObject { ["subtype"] = "apply_flag_settings", ["settings"] = new JsonObject { ["effortLevel"] = EffortLevel(effort) } },
                ControlTimeout,
                cancellationToken).ConfigureAwait(false);
            session.Effort = effort;
        }

        if (session.Thinking != thinking)
        {
            await SetThinkingAsync(session, thinking, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>0 turns thinking off; null goes back to Claude Code's default (adaptive).</summary>
    private static async Task SetThinkingAsync(ClaudeCliSession session, bool thinking, CancellationToken cancellationToken)
    {
        await session.ControlAsync(
            new JsonObject { ["subtype"] = "set_max_thinking_tokens", ["max_thinking_tokens"] = thinking ? null : 0 },
            ControlTimeout,
            cancellationToken).ConfigureAwait(false);
        session.Thinking = thinking;
    }

    private bool WantsWarm()
    {
        var claude = _settings.Current.Claude;
        return claude.Backend == ClaudeBackendKind.Subscription && claude.KeepWarm;
    }

    /// <summary>Starts a spare unless a live one exists. Under _gate.</summary>
    private void EnsureSpare()
    {
        if (_spare is { IsAlive: true })
        {
            return;
        }

        _spare?.Dispose();
        _spare = null;
        var spare = StartSession(_settings.Current.Claude.SubscriptionFastModel);
        spare.Ready!.ContinueWith(
            t => _logger.LogWarning(t.Exception?.GetBaseException(), "The spare Claude Code failed to start: {Errors}", spare.ErrorTail),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
        _spare = spare;
    }

    private void StartSpareSafely()
    {
        try
        {
            lock (_gate)
            {
                EnsureSpare();
            }
        }
        catch (Exception ex) when (ex is ClaudeBackendException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not start a spare Claude Code");
        }
    }

    /// <summary>Under _gate.</summary>
    private ClaudeCliSession TakeSpare()
    {
        var spare = _spare!;
        _spare = null;
        return spare;
    }

    private ClaudeCliSession StartSession(string model)
    {
        var executable = ClaudeCliLocator.Find(_settings.Current.Claude.CliPath)
            ?? throw new ClaudeBackendException(ClaudeFailure.NotConfigured, NotInstalledMessage());
        var promptFile = WriteSystemPrompt();
        var effort = _settings.Current.Claude.FastEffort;
        var stopwatch = Stopwatch.StartNew();
        IClaudeCliProcess process;
        try
        {
            process = _start(new ClaudeCliStartInfo(executable, Arguments(model, effort, promptFile), _directory, s_removeEnvironment));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new ClaudeBackendException(ClaudeFailure.NotConfigured, $"Could not start Claude Code ({executable}): {ex.Message}", ex);
        }

        var session = new ClaudeCliSession(process, model, effort);
        session.Ready = InitializeAsync(session, stopwatch);
        _logger.LogInformation("Started Claude Code with {Model}", model);
        return session;
    }

    private async Task InitializeAsync(ClaudeCliSession session, Stopwatch stopwatch)
    {
        await session.ControlAsync(new JsonObject { ["subtype"] = "initialize" }, StallTimeout, CancellationToken.None).ConfigureAwait(false);

        // Most questions take the fast route, so set it up while nobody is waiting.
        if (!_settings.Current.Claude.ThinkingFor(ModelTier.Fast))
        {
            await SetThinkingAsync(session, thinking: false, CancellationToken.None).ConfigureAwait(false);
        }

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

            if (ReferenceEquals(_spare, session))
            {
                _spare = null;
            }
        }

        session.Dispose();
    }

    private void CloseIfIdle()
    {
        // Not while an answer (or an agent's long run of steps) is using the process.
        if (!_turn.Wait(0))
        {
            _idle.Change(TimeSpan.FromMinutes(_settings.Current.Claude.IdleResetMinutes), Timeout.InfiniteTimeSpan);
            return;
        }

        ClaudeCliSession? session;
        try
        {
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

            if (WantsWarm())
            {
                // The next question starts a new conversation; have a process ready for it.
                StartSpareSafely();
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
        finally
        {
            try
            {
                _turn.Release();
            }
            catch (ObjectDisposedException)
            {
            }
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
        private TimeSpan? _firstToken;
        private string? _model;
        private string? _syntheticError;

        public JsonElement? Result { get; private set; }

        public ClaudeBackendException? Failure { get; private set; }

        public bool Completed { get; set; }

        public bool HasText => _spoken.Length > 0;

        /// <summary>When the model started answering (thinking included); the gap to the first text is thinking.</summary>
        public TimeSpan? FirstToken => _firstToken;

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
                _ => _actions.Any(a => a.NeedsResult) ? ReplyStop.Actions : ReplyStop.EndTurn,
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
                    _firstToken ??= elapsed;
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
