using Clicky.Core.Assistant;
using Clicky.Core.Assistant.ClaudeCode;
using Clicky.Core.Settings;
using Clicky.Tests.Memory;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Assistant;

public sealed class ClaudeAssistantTests : IDisposable
{
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly AnthropicApiBackend _api;
    private readonly ClaudeCodeBackend _subscription;
    private readonly ClaudeAssistant _assistant;

    public ClaudeAssistantTests()
    {
        var settings = new FakeSettingsStore(ClickySettings.Default);
        _api = new AnthropicApiBackend(new FakeSecrets(), settings, NullLogger<AnthropicApiBackend>.Instance);
        _subscription = new ClaudeCodeBackend(settings, Path.GetTempPath(), NullLogger<ClaudeCodeBackend>.Instance, _ => throw new InvalidOperationException("not started in these tests"));
        _assistant = new ClaudeAssistant(settings, _api, _subscription, NullLogger<ClaudeAssistant>.Instance, _time);
    }

    public void Dispose()
    {
        _api.Dispose();
        _subscription.Dispose();
    }

    [Fact]
    public void Dropping_a_conversation_says_so_once()
    {
        var ended = new List<Conversation>();
        _assistant.ConversationEnded += (_, conversation) => ended.Add(conversation);

        var first = _assistant.CurrentConversation("memory");
        Assert.Same(first, _assistant.CurrentConversation("other memory"));
        Assert.Equal("memory", first.Memory);
        Assert.Empty(ended);

        // Idle past settings.claude.idleResetMinutes (10): a new one starts and the old one has ended.
        _time.Advance(TimeSpan.FromMinutes(11));
        var second = _assistant.CurrentConversation();
        Assert.NotSame(first, second);
        Assert.Equal([first], ended);

        _assistant.NewConversation();
        _assistant.NewConversation();
        Assert.Equal([first, second], ended);
    }
}
