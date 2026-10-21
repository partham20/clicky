namespace Clicky.Core.Connectors;

public enum ConnectionState
{
    /// <summary>The connector or this account is switched off.</summary>
    Off,

    /// <summary>Starting the server or opening the session (also while a browser sign-in is open).</summary>
    Connecting,

    Connected,

    /// <summary>OAuth: nobody has signed in yet, or the refresh token stopped working. Waits for Sign in.</summary>
    SignInRequired,

    /// <summary>An API key or secret environment value has not been entered yet.</summary>
    NeedsSecrets,

    /// <summary>Could not connect or the connection dropped; <see cref="ConnectionStatus.RetryAt"/> says when it tries again, null if it gave up.</summary>
    Failed,
}

/// <summary>What Settings shows for one account of one connector.</summary>
public sealed record ConnectionStatus(
    string ConnectorId,
    string AccountId,
    ConnectionState State,
    string? Message,
    string? Server,
    IReadOnlyList<McpToolInfo> Tools,
    DateTimeOffset? RetryAt)
{
    public static ConnectionStatus Off(string connectorId, string accountId) => new(connectorId, accountId, ConnectionState.Off, null, null, [], null);

    /// <summary>Still settling: tools may appear shortly.</summary>
    public bool IsPending => State == ConnectionState.Connecting;
}
