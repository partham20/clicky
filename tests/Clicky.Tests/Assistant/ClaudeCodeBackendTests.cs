using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Clicky.Core.Actions;
using Clicky.Core.Assistant;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Geometry;
using Clicky.Core.Settings;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Assistant;

public sealed class ClaudeCodeBackendTests : IDisposable
{
    private static readonly RequestScreenshot s_shot = new([1, 2, 3], new ScreenshotGeometry(new PixelRect(0, 0, 1000, 500), 1000, 500));

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));
    private readonly List<FakeCliProcess> _processes = [];
    private readonly List<ClaudeCliStartInfo> _starts = [];
    private readonly Queue<string[]> _replies = new();
    private readonly ClaudeCodeBackend _backend;

    public ClaudeCodeBackendTests()
    {
        // Any existing file stands in for claude.exe; the fake never runs it.
        var settings = new FakeSettingsStore(ClickySettings.Default with
        {
            Claude = new ClaudeSettings(Backend: ClaudeBackendKind.Subscription, CliPath: typeof(ClaudeCodeBackendTests).Assembly.Location),
        });
        _backend = new ClaudeCodeBackend(settings, _directory, NullLogger<ClaudeCodeBackend>.Instance, info =>
        {
            _starts.Add(info);
            var process = new FakeCliProcess(_replies);
            _processes.Add(process);
            return process;
        });
    }

    public void Dispose()
    {
        _backend.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Streams_speech_and_tagged_actions_and_records_the_turn()
    {
        var conversation = new Conversation();
        Script(TextReply("msg_1", "That's Share. <act", "ion>{\"type\":\"point\",\"x\":990,\"y\":20}</action> Click it."));

        var events = await Collect(conversation, new ClaudeRequest("where is share", RequestSource.Voice, s_shot), "haiku");

        Assert.Equal(["That's Share. ", " Click it."], events.OfType<ReplyText>().Select(e => e.Text));
        Assert.Equal(new PointAction(990, 20), Assert.Single(events.OfType<ReplyAction>()).Action);
        var completed = Assert.IsType<ReplyCompleted>(events[^1]);
        Assert.Equal([new SayAction("That's Share. "), new PointAction(990, 20), new SayAction(" Click it.")], completed.Actions);
        Assert.Equal(ReplyStop.EndTurn, completed.Stop);
        Assert.Equal(1234, completed.Usage.InputTokens);
        Assert.Equal(0.0012m, completed.Usage.CostUsd);

        var turn = Assert.Single(conversation.Turns);
        Assert.Equal("That's Share.  Click it.", turn.AssistantText);
        Assert.Equal(s_shot.Png, turn.Screenshot);

        // Isolated, subscription-billed, nothing saved; screenshot inline over stdin.
        var start = Assert.Single(_starts);
        Assert.Contains("--safe-mode", start.Arguments);
        Assert.Contains("--no-session-persistence", start.Arguments);
        Assert.Equal(string.Empty, start.Arguments[start.Arguments.ToList().IndexOf("--tools") + 1]);
        Assert.Contains("ANTHROPIC_API_KEY", start.RemoveEnvironment);
        Assert.Equal(_directory, start.WorkingDirectory);
        Assert.True(File.Exists(Path.Combine(_directory, "system-prompt.md")));
        var user = _processes[0].Written.Single(m => m["type"]!.GetValue<string>() == "user");
        var content = user["message"]!["content"]!.AsArray();
        Assert.Equal(["image", "text"], content.Select(c => c!["type"]!.GetValue<string>()));
        Assert.Equal(Convert.ToBase64String(s_shot.Png), content[0]!["source"]!["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Later_turns_reuse_the_process_and_switch_models_with_a_control_message()
    {
        var conversation = new Conversation();
        Script(TextReply("msg_1", "One."));
        await Collect(conversation, new ClaudeRequest("hi", RequestSource.Voice), "haiku");
        Script(TextReply("msg_2", "Two."));

        var events = await Collect(conversation, new ClaudeRequest("explain", RequestSource.Voice), "opus");

        Assert.Single(_processes);
        Assert.Contains(_processes[0].Written, m => m["type"]!.GetValue<string>() == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "set_model" && m["request"]!["model"]!.GetValue<string>() == "opus");
        Assert.Equal("Two.", Assert.IsType<ReplyCompleted>(events[^1]).SpokenText);

        // The process already has the first turn, so no transcript is resent.
        var lastUser = _processes[0].Written.Last(m => m["type"]!.GetValue<string>() == "user");
        Assert.Single(lastUser["message"]!["content"]!.AsArray());
    }

    [Fact]
    public async Task A_new_process_joining_a_conversation_gets_the_earlier_turns_as_text()
    {
        var conversation = new Conversation();
        conversation.Append(new ConversationTurn("[No screenshot]\nThe user typed: hi", null, "Hello!", [], DateTimeOffset.UnixEpoch), new HistoryLimits());
        Script(TextReply("msg_1", "Sure."));

        await Collect(conversation, new ClaudeRequest("and now?", RequestSource.Typed), "haiku");

        var content = _processes[0].Written.Single(m => m["type"]!.GetValue<string>() == "user")["message"]!["content"]!.AsArray();
        var transcript = content[0]!["text"]!.GetValue<string>();
        Assert.Contains("User: [No screenshot]\nThe user typed: hi\nClicky: Hello!", transcript, StringComparison.Ordinal);
        Assert.Equal(2, conversation.Turns.Count);
    }

    [Fact]
    public async Task Signed_out_claude_code_becomes_a_clear_error_and_the_process_is_dropped()
    {
        var conversation = new Conversation();
        Script(
        [
            """{"type":"assistant","message":{"id":"x","model":"<synthetic>","role":"assistant","content":[{"type":"text","text":"Failed to authenticate: OAuth session expired and could not be refreshed"}]}}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired","usage":{}}""",
        ]);

        var error = await Assert.ThrowsAsync<ClaudeBackendException>(() => Collect(conversation, new ClaudeRequest("hi", RequestSource.Voice), "haiku"));

        Assert.Equal(ClaudeFailure.Unauthorized, error.Failure);
        Assert.Contains("/login", error.Message, StringComparison.Ordinal);
        Assert.Empty(conversation.Turns);
        Assert.True(_processes[0].HasExited);

        Script(TextReply("msg_1", "Back."));
        await Collect(conversation, new ClaudeRequest("hi", RequestSource.Voice), "haiku");
        Assert.Equal(2, _processes.Count);
    }

    [Fact]
    public async Task Barge_in_interrupts_claude_code_and_keeps_what_was_said()
    {
        var conversation = new Conversation();
        Script(["""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"First sentence. "}}}"""]);
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in _backend.ReplyAsync(conversation, new ClaudeRequest("tell me a story", RequestSource.Voice), "haiku", ClaudeEffort.Low, cancel.Token))
            {
                if (e is ReplyText)
                {
                    cancel.Cancel();
                }
            }
        });

        var process = _processes[0];
        Assert.Contains(process.Written, m => m["type"]!.GetValue<string>() == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "interrupt");
        var turn = Assert.Single(conversation.Turns);
        Assert.True(turn.Interrupted);
        Assert.Equal("First sentence. ", turn.AssistantText);
        Assert.False(process.HasExited); // It wound down cleanly, so it stays warm.
    }

    [Fact]
    public async Task Missing_claude_code_is_reported_as_not_configured()
    {
        using var backend = new ClaudeCodeBackend(
            new FakeSettingsStore(ClickySettings.Default with { Claude = new ClaudeSettings(CliPath: Path.Combine(_directory, "nope.exe")) }),
            _directory,
            NullLogger<ClaudeCodeBackend>.Instance,
            _ => throw new InvalidOperationException("must not start"));

        var error = await Assert.ThrowsAsync<ClaudeBackendException>(async () =>
        {
            await foreach (var _ in backend.ReplyAsync(new Conversation(), new ClaudeRequest("hi", RequestSource.Voice), "haiku", ClaudeEffort.Low))
            {
            }
        });

        Assert.Equal(ClaudeFailure.NotConfigured, error.Failure);
        Assert.False((await backend.CheckAsync()).Ready);
    }

    private static string[] TextReply(string id, params string[] chunks) =>
    [
        """{"type":"system","subtype":"init","model":"claude-haiku-5-5"}""",
        """{"type":"stream_event","event":{"type":"message_start","message":{"id":"@ID","model":"claude-haiku-5-5"}}}""".Replace("@ID", id, StringComparison.Ordinal),
        .. chunks.Select(c => """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":@TEXT}}}""".Replace("@TEXT", JsonSerializer.Serialize(c), StringComparison.Ordinal)),
        """{"type":"stream_event","event":{"type":"content_block_stop","index":0}}""",
        """{"type":"assistant","message":{"id":"@ID","model":"claude-haiku-5-5","role":"assistant","content":[{"type":"text","text":"(already streamed)"}]}}""".Replace("@ID", id, StringComparison.Ordinal),
        """{"type":"result","subtype":"success","is_error":false,"result":"done","usage":{"input_tokens":1234,"output_tokens":20,"cache_read_input_tokens":1000},"total_cost_usd":0.0012}""",
    ];

    /// <summary>What Claude Code answers to the next user message, whichever process gets it.</summary>
    private void Script(string[] lines) => _replies.Enqueue(lines);

    private async Task<List<ReplyEvent>> Collect(Conversation conversation, ClaudeRequest request, string model)
    {
        var events = new List<ReplyEvent>();
        await foreach (var e in _backend.ReplyAsync(conversation, request, model, ClaudeEffort.Low))
        {
            events.Add(e);
        }

        return events;
    }

    /// <summary>Answers control requests like Claude Code and replays a script for each user message.</summary>
    private sealed class FakeCliProcess(Queue<string[]> replies) : IClaudeCliProcess
    {
        private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();

        public List<JsonNode> Written { get; } = [];

        public bool HasExited { get; private set; }

        public int? ExitCode => HasExited ? 0 : null;

        public string ErrorTail => string.Empty;

        public Task WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            var message = JsonNode.Parse(line)!;
            lock (Written)
            {
                Written.Add(message);
            }

            switch (message["type"]!.GetValue<string>())
            {
                case "control_request":
                    var id = message["request_id"]!.GetValue<string>();
                    _stdout.Writer.TryWrite("""{"type":"control_response","response":{"subtype":"success","request_id":"@ID","response":{}}}""".Replace("@ID", id, StringComparison.Ordinal));
                    if (message["request"]!["subtype"]!.GetValue<string>() == "interrupt")
                    {
                        _stdout.Writer.TryWrite("""{"type":"result","subtype":"error_during_execution","is_error":false,"usage":{}}""");
                    }

                    break;
                case "user":
                    var script = replies.Count > 0 ? replies.Dequeue() : [];
                    foreach (var reply in script)
                    {
                        _stdout.Writer.TryWrite(reply);
                    }

                    break;
            }

            return Task.CompletedTask;
        }

        public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await _stdout.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public void Kill()
        {
            HasExited = true;
            _stdout.Writer.TryComplete();
        }

        public void Dispose() => Kill();
    }
}
