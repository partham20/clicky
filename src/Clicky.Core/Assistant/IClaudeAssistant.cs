namespace Clicky.Core.Assistant;

/// <summary>What the Talk loop and the agent loop ask Claude through; <see cref="ClaudeAssistant"/> in the app.</summary>
public interface IClaudeAssistant
{
    /// <summary>Routes the request, then streams the active backend's reply; the first event is <see cref="ReplyStarted"/>.</summary>
    /// <exception cref="ClaudeBackendException">Not set up, or the request failed.</exception>
    IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, CancellationToken cancellationToken = default);
}
