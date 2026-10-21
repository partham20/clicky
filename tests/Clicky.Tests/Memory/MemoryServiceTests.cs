using Clicky.Core.Memory;
using Clicky.Core.Settings;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Memory;

public sealed class MemoryServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clicky-tests", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(Start);
    private readonly FakeMemoryModel _model = new();
    private readonly FakeSettingsStore _settings = new(ClickySettings.Default);
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var d in _owned)
        {
            d.Dispose();
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // The M11 acceptance check, minus the real model: a stated preference is remembered and reaches the
    // prompt of a fresh session (new store and service over the same folder).
    [Fact]
    public async Task Stated_preference_reaches_the_prompt_of_a_fresh_session()
    {
        _model.Reply = """{"edits":[{"op":"add","file":"profile","section":"How to answer","text":"Keep spoken answers to one or two sentences unless asked for more."}]}""";
        var first = CreateService();
        var updated = new TaskCompletionSource<MemoryUpdatedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        first.Updated += (_, e) => updated.TrySetResult(e);

        first.RecordTurn(new MemoryTurn("You talk too much.", "Sorry, I'll keep it short.", Start));
        var e = await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(e.FromFeedback);
        Assert.Contains("You talk too much.", _model.LastUser, StringComparison.Ordinal);
        Assert.Contains("You talk too much", MemoryUpdaterPrompt.System, StringComparison.OrdinalIgnoreCase);

        var fresh = CreateService();
        var prompt = await fresh.GetPromptAsync();
        Assert.Contains("- Keep spoken answers to one or two sentences unless asked for more.", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ordinary_turns_wait_for_the_conversation_to_go_quiet()
    {
        _model.Reply = """{"edits":[{"op":"add","file":"volatile","section":"Current project","text":"Building the memory module."}]}""";
        var service = CreateService();

        service.RecordTurn(new MemoryTurn("What's on my screen?", "Visual Studio with MemoryStore.cs.", Start));
        _time.Advance(TimeSpan.FromMinutes(2));
        service.RecordTurn(new MemoryTurn("Where's the test explorer?", "Top menu, Test.", Start.AddMinutes(2)));
        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(0, _model.Calls);

        _time.Advance(TimeSpan.FromMinutes(1.5));
        await WaitFor(() => _model.Calls == 1 && service.QueuedTurns == 0);

        Assert.Contains("Where's the test explorer?", _model.LastUser, StringComparison.Ordinal);
        Assert.Contains("What's on my screen?", _model.LastUser, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_model_call_keeps_the_turns_for_the_next_pass()
    {
        var service = CreateService();
        _model.Fail = new HttpRequestException("offline");
        service.RecordTurn(new MemoryTurn("Hello", "Hi.", Start));

        await service.EndConversationAsync();
        Assert.Equal(1, service.QueuedTurns);

        _model.Fail = null;
        _model.Reply = "{\"edits\":[]}";
        await service.EndConversationAsync();
        Assert.Equal(0, service.QueuedTurns);
        Assert.Equal(2, _model.Calls);
    }

    [Fact]
    public async Task Garbage_reply_is_dropped_not_retried()
    {
        var service = CreateService();
        _model.Reply = "I don't think anything needs remembering.";
        service.RecordTurn(new MemoryTurn("Hello", "Hi.", Start));

        await service.EndConversationAsync();

        Assert.Equal(0, service.QueuedTurns);
    }

    [Fact]
    public async Task Disabled_memory_is_neither_read_nor_updated()
    {
        var service = CreateService();
        await service.Store.SaveTextAsync(MemoryFile.Profile, "## About me\n- Firmware engineer.\n");
        Assert.NotEmpty(await service.GetPromptAsync());

        await _settings.UpdateAsync(s => s with { Memory = s.Memory with { Enabled = false } });
        service.RecordTurn(new MemoryTurn("Remember that I use Rider.", null, Start));

        Assert.Empty(await service.GetPromptAsync());
        Assert.Equal(0, service.QueuedTurns);
        Assert.Equal(0, _model.Calls);
    }

    [Fact]
    public async Task Without_a_model_memory_is_read_only()
    {
        var store = Own(new MemoryStore(Path.Combine(_dir, "memory"), NullLogger<MemoryStore>.Instance));
        var service = Own(new MemoryService(store, _settings, NullLogger<MemoryService>.Instance, model: null, time: _time));

        service.RecordTurn(new MemoryTurn("Remember that I use Rider.", null, Start));
        await service.EndConversationAsync();

        Assert.Equal(0, service.QueuedTurns);
        Assert.Empty(await service.GetPromptAsync());
    }

    [Theory]
    [InlineData("You talk too much")]
    [InlineData("that was way too long")]
    [InlineData("be more concise please")]
    [InlineData("stop saying great question")]
    [InlineData("don't read the whole thing out")]
    [InlineData("From now on answer in Tamil")]
    [InlineData("remember that my build box is the Dell")]
    [InlineData("forget about the Jira thing")]
    [InlineData("I prefer metric units")]
    [InlineData("call me Partha")]
    public void Feedback_and_preferences_are_spotted(string text) => Assert.True(FeedbackCue.IsFeedback(text));

    [Theory]
    [InlineData("where's the save button?")]
    [InlineData("what does this error mean")]
    [InlineData("open the long file")]
    [InlineData("")]
    public void Ordinary_questions_are_not_feedback(string text) => Assert.False(FeedbackCue.IsFeedback(text));

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the memory pass.");
            await Task.Delay(10);
        }
    }

    private MemoryService CreateService()
    {
        var store = Own(new MemoryStore(Path.Combine(_dir, "memory"), NullLogger<MemoryStore>.Instance));
        return Own(new MemoryService(store, _settings, NullLogger<MemoryService>.Instance, _model, _time));
    }

    private T Own<T>(T disposable)
        where T : IDisposable
    {
        _owned.Add(disposable);
        return disposable;
    }

    private sealed class FakeMemoryModel : IMemoryModel
    {
        private int _calls;

        public string Reply { get; set; } = "{\"edits\":[]}";

        public Exception? Fail { get; set; }

        public string LastUser { get; private set; } = string.Empty;

        public int Calls => Volatile.Read(ref _calls);

        public Task<string> CompleteAsync(string system, string user, CancellationToken cancellationToken)
        {
            LastUser = user;
            Interlocked.Increment(ref _calls);
            return Fail is { } ex ? Task.FromException<string>(ex) : Task.FromResult(Reply);
        }
    }
}
