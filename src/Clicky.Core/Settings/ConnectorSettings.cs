using System.Text.Json.Serialization;
using Clicky.Core.Connectors;

namespace Clicky.Core.Settings;

/// <summary>
/// settings.connectors: MCP servers Clicky connects to. Only non-secret configuration lives here;
/// API keys, secret environment values and OAuth tokens are in Credential Manager (see
/// <see cref="ConnectorSecrets"/>).
/// </summary>
/// <remarks>Positional so missing properties read as these defaults (see <see cref="CaptureSettings"/>).</remarks>
public sealed record ConnectorSettings(IReadOnlyList<ConnectorConfig>? Servers = null)
{
    public IReadOnlyList<ConnectorConfig> Servers { get; init; } = Servers ?? [];

    /// <summary>Drops broken entries, repairs ids and makes them unique, so ids can key secrets and tool names.</summary>
    public ConnectorSettings Normalize()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var servers = new List<ConnectorConfig>();
        foreach (var server in Servers ?? [])
        {
            if (server is null)
            {
                continue;
            }

            var normalized = server.Normalize();
            var id = ConnectorNames.Unique(normalized.Id, used);
            used.Add(id);
            servers.Add(normalized with { Id = id });
        }

        return this with { Servers = servers };
    }

    public ConnectorConfig? Find(string connectorId) => Servers.FirstOrDefault(s => s.Id == connectorId);

    // Records compare collections by reference; settings change detection needs content equality.
    public bool Equals(ConnectorSettings? other) =>
        other is not null && Structural.ListEquals(Servers, other.Servers);

    public override int GetHashCode() => Servers?.Count ?? 0;
}

public enum ConnectorTransport
{
    /// <summary>A local program Clicky starts and talks to over stdin/stdout.</summary>
    Stdio,

    /// <summary>A remote server over streamable HTTP (falls back to the older HTTP+SSE transport).</summary>
    Http,
}

public enum ConnectorAuth
{
    /// <summary>No sign-in, or credentials passed as secret headers/environment.</summary>
    None,

    /// <summary>MCP OAuth 2.1: browser sign-in per account, tokens in Credential Manager.</summary>
    OAuth,
}

/// <summary>One MCP server. A server with several accounts runs one connection per account.</summary>
public sealed record ConnectorConfig(
    string Id = "",
    string Name = "",
    bool Enabled = true,
    ConnectorTransport Transport = ConnectorTransport.Stdio,
    string Command = "",
    IReadOnlyList<string>? Arguments = null,
    string WorkingDirectory = "",
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? SecretEnvironment = null,
    string Url = "",
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<string>? SecretHeaders = null,
    ConnectorAuth Auth = ConnectorAuth.None,
    string OAuthClientId = "",
    IReadOnlyList<string>? OAuthScopes = null,
    int OAuthRedirectPort = 0,
    IReadOnlyList<ConnectorAccount>? Accounts = null,
    IReadOnlyList<string>? DisabledTools = null,
    int ToolTimeoutSeconds = ConnectorConfig.DefaultToolTimeoutSeconds)
{
    public const int DefaultToolTimeoutSeconds = 120;
    public const int MinToolTimeoutSeconds = 5;
    public const int MaxToolTimeoutSeconds = 1800;

    /// <summary>Stable slug ([a-z0-9-]): prefixes tool names and keys this server's secrets. Never shown as a name.</summary>
    public string Id { get; init; } = Id;

    public string Name { get; init; } = Name;

    public bool Enabled { get; init; } = Enabled;

    public ConnectorTransport Transport { get; init; } = Transport;

    /// <summary>Stdio: program to run ("npx", "uvx", a full path). Windows .cmd shims like npx run through cmd.exe.</summary>
    public string Command { get; init; } = Command;

    public IReadOnlyList<string> Arguments { get; init; } = Arguments ?? [];

    /// <summary>Stdio: working directory; empty is the user's profile folder.</summary>
    public string WorkingDirectory { get; init; } = WorkingDirectory;

    /// <summary>Stdio: environment variables that are not secret, on top of Clicky's own environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = Environment ?? new Dictionary<string, string>();

    /// <summary>Stdio: environment variable names whose values are per-account secrets (GITHUB_TOKEN, …).</summary>
    public IReadOnlyList<string> SecretEnvironment { get; init; } = SecretEnvironment ?? [];

    /// <summary>Http: the MCP endpoint, e.g. https://mcp.linear.app/mcp.</summary>
    public string Url { get; init; } = Url;

    /// <summary>Http: headers that are not secret.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = Headers ?? new Dictionary<string, string>();

    /// <summary>Http: header names whose values are per-account secrets ("Authorization" for an API key).</summary>
    public IReadOnlyList<string> SecretHeaders { get; init; } = SecretHeaders ?? [];

    public ConnectorAuth Auth { get; init; } = Auth;

    /// <summary>OAuth: a pre-registered client id, for servers without dynamic client registration. Its secret, if any, is in Credential Manager.</summary>
    [JsonPropertyName("oauthClientId")]
    public string OAuthClientId { get; init; } = OAuthClientId;

    /// <summary>OAuth: scopes to ask for when the server does not advertise any.</summary>
    [JsonPropertyName("oauthScopes")]
    public IReadOnlyList<string> OAuthScopes { get; init; } = OAuthScopes ?? [];

    /// <summary>OAuth: fixed loopback port for the redirect, when a pre-registered client only allows one; 0 picks a free port.</summary>
    [JsonPropertyName("oauthRedirectPort")]
    public int OAuthRedirectPort { get; init; } = OAuthRedirectPort;

    /// <summary>Always at least one. Each enabled account is its own connection with its own credentials.</summary>
    public IReadOnlyList<ConnectorAccount> Accounts { get; init; } = Accounts ?? [];

    /// <summary>Server tool names Claude is not offered.</summary>
    public IReadOnlyList<string> DisabledTools { get; init; } = DisabledTools ?? [];

    /// <summary>How long one tool call may run before it is cancelled.</summary>
    public int ToolTimeoutSeconds { get; init; } = ToolTimeoutSeconds;

    /// <summary>True when accounts carry credentials, so the account list is worth showing.</summary>
    [JsonIgnore]
    public bool HasCredentials => Auth == ConnectorAuth.OAuth || SecretEnvironment.Count > 0 || SecretHeaders.Count > 0;

    [JsonIgnore]
    public IEnumerable<ConnectorAccount> EnabledAccounts => Accounts.Where(a => a.Enabled);

    public ConnectorAccount? FindAccount(string accountId) => Accounts.FirstOrDefault(a => a.Id == accountId);

    public ConnectorConfig Normalize()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? (Transport == ConnectorTransport.Http ? Url : Command)?.Trim() ?? string.Empty : Name.Trim();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var accounts = new List<ConnectorAccount>();
        foreach (var account in Accounts ?? [])
        {
            if (account is null)
            {
                continue;
            }

            var id = ConnectorNames.Unique(ConnectorNames.Slug(string.IsNullOrWhiteSpace(account.Id) ? account.Label : account.Id, ConnectorAccount.DefaultId), used);
            used.Add(id);
            accounts.Add(account with { Id = id, Label = string.IsNullOrWhiteSpace(account.Label) ? id : account.Label.Trim() });
        }

        if (accounts.Count == 0)
        {
            accounts.Add(new ConnectorAccount(ConnectorAccount.DefaultId, "Default"));
        }

        return this with
        {
            Id = ConnectorNames.Slug(string.IsNullOrWhiteSpace(Id) ? name : Id, "server"),
            Name = name,
            Transport = Enum.IsDefined(Transport) ? Transport : ConnectorTransport.Stdio,
            Command = Command?.Trim() ?? string.Empty,
            Arguments = [.. (Arguments ?? []).Where(a => a is not null)],
            WorkingDirectory = WorkingDirectory?.Trim() ?? string.Empty,
            Environment = Structural.CleanMap(Environment),
            SecretEnvironment = Structural.CleanNames(SecretEnvironment),
            Url = Url?.Trim() ?? string.Empty,
            Headers = Structural.CleanMap(Headers),
            SecretHeaders = Structural.CleanNames(SecretHeaders),
            Auth = Enum.IsDefined(Auth) ? Auth : ConnectorAuth.None,
            OAuthClientId = OAuthClientId?.Trim() ?? string.Empty,
            OAuthScopes = Structural.CleanNames(OAuthScopes),
            OAuthRedirectPort = OAuthRedirectPort is > 0 and <= 65535 ? OAuthRedirectPort : 0,
            Accounts = accounts,
            DisabledTools = Structural.CleanNames(DisabledTools),
            ToolTimeoutSeconds = Math.Clamp(ToolTimeoutSeconds <= 0 ? DefaultToolTimeoutSeconds : ToolTimeoutSeconds, MinToolTimeoutSeconds, MaxToolTimeoutSeconds),
        };
    }

    /// <summary>True when a running connection must restart to pick up the change (accounts and tool filters apply live).</summary>
    public bool ConnectionEquals(ConnectorConfig? other) =>
        other is not null
        && Id == other.Id
        && Transport == other.Transport
        && Command == other.Command
        && Structural.ListEquals(Arguments, other.Arguments)
        && WorkingDirectory == other.WorkingDirectory
        && Structural.MapEquals(Environment, other.Environment)
        && Structural.ListEquals(SecretEnvironment, other.SecretEnvironment)
        && Url == other.Url
        && Structural.MapEquals(Headers, other.Headers)
        && Structural.ListEquals(SecretHeaders, other.SecretHeaders)
        && Auth == other.Auth
        && OAuthClientId == other.OAuthClientId
        && Structural.ListEquals(OAuthScopes, other.OAuthScopes)
        && OAuthRedirectPort == other.OAuthRedirectPort;

    public bool Equals(ConnectorConfig? other) =>
        ConnectionEquals(other)
        && Name == other!.Name
        && Enabled == other.Enabled
        && Structural.ListEquals(Accounts, other.Accounts)
        && Structural.ListEquals(DisabledTools, other.DisabledTools)
        && ToolTimeoutSeconds == other.ToolTimeoutSeconds;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Enabled, Transport, Command, Url, Accounts?.Count ?? 0);
}

/// <summary>One set of credentials for a connector ("Work", "Personal").</summary>
public sealed record ConnectorAccount(string Id = ConnectorAccount.DefaultId, string Label = "", bool Enabled = true)
{
    public const string DefaultId = "default";

    /// <summary>Slug ([a-z0-9-]); keys this account's secrets and, with several accounts, its tool names.</summary>
    public string Id { get; init; } = Id;

    public string Label { get; init; } = Label;

    public bool Enabled { get; init; } = Enabled;
}

/// <summary>Content equality for the collections inside settings records.</summary>
internal static class Structural
{
    public static bool ListEquals<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
        ReferenceEquals(a, b) || (a is not null && b is not null && a.SequenceEqual(b));

    public static bool MapEquals(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        foreach (var (key, value) in a)
        {
            if (!b.TryGetValue(key, out var other) || other != value)
            {
                return false;
            }
        }

        return true;
    }

    public static IReadOnlyList<string> CleanNames(IReadOnlyList<string>? names) =>
        [.. (names ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.Ordinal)];

    public static IReadOnlyDictionary<string, string> CleanMap(IReadOnlyDictionary<string, string>? map)
    {
        var clean = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                clean[key.Trim()] = value ?? string.Empty;
            }
        }

        return new Dictionary<string, string>(clean, StringComparer.Ordinal);
    }
}
