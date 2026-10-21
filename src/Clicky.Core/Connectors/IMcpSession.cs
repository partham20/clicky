using System.Text.Json;
using Clicky.Core.Settings;

namespace Clicky.Core.Connectors;

/// <summary>Opens connections to MCP servers. The real one wraps the official C# MCP SDK; tests fake it.</summary>
public interface IMcpSessionFactory
{
    /// <summary>
    /// Starts the server (stdio) or opens the session (HTTP) and completes the MCP handshake.
    /// </summary>
    /// <param name="signIn">
    /// How to handle an OAuth server that needs the user: null means fail with
    /// <see cref="SignInRequiredException"/> (background connects never open a browser).
    /// </param>
    /// <exception cref="SignInRequiredException">The server needs a browser sign-in and <paramref name="signIn"/> is null.</exception>
    /// <exception cref="MissingSecretsException">The account has no value yet for a secret the config names.</exception>
    Task<IMcpSession> ConnectAsync(ConnectorConfig config, ConnectorAccount account, IOAuthSignIn? signIn, CancellationToken cancellationToken);
}

/// <summary>One live connection to one MCP server for one account.</summary>
public interface IMcpSession : IAsyncDisposable
{
    /// <summary>Server name and version from the handshake, e.g. "linear 1.2.0".</summary>
    string ServerDescription { get; }

    /// <summary>The server's usage instructions, if it sent any.</summary>
    string? Instructions { get; }

    /// <summary>Completes (never faults) when the session ends: the process exited, the network dropped, or it was disposed.</summary>
    Task<SessionEnd> Completion { get; }

    /// <summary>Raised when the server announces its tool list changed (notifications/tools/list_changed).</summary>
    event EventHandler? ToolsChanged;

    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken);

    /// <summary>Calls a tool. Protocol and transport failures throw; the server's own tool errors come back with IsError.</summary>
    Task<ConnectorToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken);
}

/// <summary>Why a session ended; <see cref="Reason"/> is null for a clean close.</summary>
public sealed record SessionEnd(string? Reason, bool NeedsSignIn = false);

/// <summary>A tool as the server describes it.</summary>
public sealed record McpToolInfo(
    string Name,
    string? Title,
    string? Description,
    JsonElement InputSchema,
    bool? ReadOnlyHint,
    bool? DestructiveHint,
    bool? OpenWorldHint);

/// <summary>Runs an interactive OAuth authorization: show the user the page, wait for the redirect.</summary>
public interface IOAuthSignIn
{
    /// <summary>The loopback redirect URI to register; fixed for the lifetime of one sign-in.</summary>
    Uri RedirectUri { get; }

    /// <summary>Opens <paramref name="authorizationUri"/> in the browser and waits for the redirect to <see cref="RedirectUri"/>.</summary>
    Task<OAuthRedirect> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken);
}

/// <summary>Query parameters of the authorization redirect.</summary>
public sealed record OAuthRedirect(string? Code, string? State, string? Issuer, string? Error, string? ErrorDescription);

public sealed class SignInRequiredException : Exception
{
    public SignInRequiredException()
        : base("The server needs you to sign in.")
    {
    }

    public SignInRequiredException(string message)
        : base(message)
    {
    }

    public SignInRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MissingSecretsException : Exception
{
    public MissingSecretsException()
        : this([])
    {
    }

    public MissingSecretsException(IReadOnlyList<string> names)
        : base($"Missing {string.Join(", ", names)}. Add it in Settings › Connectors.") => Names = names;

    public MissingSecretsException(string message)
        : base(message) => Names = [];

    public MissingSecretsException(string message, Exception innerException)
        : base(message, innerException) => Names = [];

    public IReadOnlyList<string> Names { get; }
}
