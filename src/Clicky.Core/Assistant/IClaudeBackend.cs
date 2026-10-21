namespace Clicky.Core.Assistant;

/// <summary>
/// One way of asking Claude: the Messages API with a key, or Claude Code on the user's
/// subscription. Both speak the shared action schema, so callers can't tell them apart.
/// </summary>
public interface IClaudeBackend
{
    ClaudeBackendKind Kind { get; }

    /// <summary>
    /// A request is coming soon (the user just pressed Talk): do the slow part of getting ready,
    /// such as starting Claude Code. Returns at once; never throws.
    /// </summary>
    void Prepare(Conversation conversation);

    /// <summary>
    /// Answers <paramref name="request"/> in the context of <paramref name="conversation"/> and
    /// appends the turn to it, also when the reply is cancelled part way (barge-in). One reply per
    /// conversation at a time.
    /// </summary>
    /// <param name="model">Model ID or alias, from the router.</param>
    /// <exception cref="ClaudeBackendException">Not set up, or the request failed.</exception>
    IAsyncEnumerable<ReplyEvent> ReplyAsync(Conversation conversation, ClaudeRequest request, string model, ClaudeEffort effort, CancellationToken cancellationToken = default);

    /// <summary>
    /// One plain request, text in and text out: no tools, images or conversation (the memory
    /// updater uses it). Independent of any reply in progress.
    /// </summary>
    /// <exception cref="ClaudeBackendException">Not set up, or the request failed.</exception>
    Task<string> CompleteAsync(string system, string user, string model, CancellationToken cancellationToken = default);

    /// <summary>Whether this backend can answer right now (key stored, CLI installed…), for Settings. Never throws.</summary>
    Task<BackendCheck> CheckAsync(CancellationToken cancellationToken = default);
}

/// <param name="Ready">True if a request should work.</param>
/// <param name="Message">One line for Settings, e.g. "Claude Code 2.1.296 found" or what to fix.</param>
public sealed record BackendCheck(bool Ready, string Message);

public enum ClaudeFailure
{
    /// <summary>Not set up: no key, no Claude Code, not signed in.</summary>
    NotConfigured,

    /// <summary>The key or login was rejected.</summary>
    Unauthorized,

    /// <summary>Rate limited, out of credit or usage limit reached.</summary>
    RateLimited,

    /// <summary>Anthropic is overloaded or had a server error; worth retrying later.</summary>
    Unavailable,

    /// <summary>Could not reach the service or start the process.</summary>
    Network,

    /// <summary>The request itself was wrong (bad model name, too large).</summary>
    BadRequest,

    /// <summary>The reply took too long.</summary>
    Timeout,

    Failed,
}

/// <summary>A reply failed. <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class ClaudeBackendException : Exception
{
    public ClaudeBackendException()
    {
    }

    public ClaudeBackendException(string message)
        : base(message)
    {
    }

    public ClaudeBackendException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ClaudeBackendException(ClaudeFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ClaudeFailure Failure { get; } = ClaudeFailure.Failed;
}
