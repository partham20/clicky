using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Authentication;

namespace Clicky.Core.Connectors;

/// <summary>
/// One account's OAuth state in Credential Manager: tokens, the dynamically registered client
/// they belong to, and the loopback port that client was registered with. Lets a refresh token
/// survive restarts without signing in again.
/// </summary>
public sealed class OAuthTokenStore(ConnectorSecrets secrets, string connectorId, string accountId)
{
    private readonly string _name = ConnectorSecrets.OAuthName(connectorId, accountId);
    private readonly Lock _gate = new();
    private StoredOAuth? _cached;
    private bool _loaded;

    public bool HasTokens => Load()?.AccessToken is { Length: > 0 } || Load()?.RefreshToken is { Length: > 0 };

    /// <summary>The port the stored client registration's redirect URI uses; 0 if none.</summary>
    public int RedirectPort => Load()?.RedirectPort ?? 0;

    /// <summary>
    /// Starts a fresh sign-in on <paramref name="port"/>: drops old tokens and the old client
    /// registration (bound to its own redirect URI), so the browser always asks who to sign in as.
    /// </summary>
    public void BeginSignIn(int port)
    {
        lock (_gate)
        {
            Save(new StoredOAuth { RedirectPort = port });
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            secrets.Write(_name, null);
            _cached = null;
            _loaded = true;
        }
    }

    /// <summary>The SDK's view: called on every request, so reads are served from memory after the first.</summary>
    public ITokenCache AsTokenCache() => new Cache(this);

    private StoredOAuth? Load()
    {
        lock (_gate)
        {
            if (!_loaded)
            {
                _cached = Parse(secrets.Read(_name));
                _loaded = true;
            }

            return _cached;
        }
    }

    private void Save(StoredOAuth value)
    {
        secrets.Write(_name, JsonSerializer.Serialize(value, ConnectorJsonContext.Default.StoredOAuth));
        _cached = value;
        _loaded = true;
    }

    private static StoredOAuth? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, ConnectorJsonContext.Default.StoredOAuth);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Cache(OAuthTokenStore owner) : ITokenCache
    {
        public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
        {
            var stored = owner.Load();
            return ValueTask.FromResult(
                stored is null || (string.IsNullOrEmpty(stored.AccessToken) && string.IsNullOrEmpty(stored.RefreshToken))
                    ? null
                    : stored.ToContainer());
        }

        public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(tokens);
            lock (owner._gate)
            {
                var port = owner.Load()?.RedirectPort ?? 0;
                owner.Save(StoredOAuth.From(tokens, port));
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Our own copy of <see cref="TokenContainer"/>, so the stored format does not move with the SDK.</summary>
internal sealed record StoredOAuth
{
    public string? TokenType { get; init; }

    public string? AccessToken { get; init; }

    public string? RefreshToken { get; init; }

    public int? ExpiresIn { get; init; }

    public string? Scope { get; init; }

    public DateTimeOffset ObtainedAt { get; init; }

    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? TokenEndpointAuthMethod { get; init; }

    public string? AuthorizationServer { get; init; }

    public int RedirectPort { get; init; }

    public static StoredOAuth From(TokenContainer tokens, int redirectPort) => new()
    {
        TokenType = tokens.TokenType,
        AccessToken = tokens.AccessToken,
        RefreshToken = tokens.RefreshToken,
        ExpiresIn = tokens.ExpiresIn,
        Scope = tokens.Scope,
        ObtainedAt = tokens.ObtainedAt,
        ClientId = tokens.ClientId,
        ClientSecret = tokens.ClientSecret,
        TokenEndpointAuthMethod = tokens.TokenEndpointAuthMethod,
        AuthorizationServer = tokens.AuthorizationServer,
        RedirectPort = redirectPort,
    };

    public TokenContainer ToContainer() => new()
    {
        TokenType = TokenType ?? "Bearer",
        AccessToken = AccessToken ?? string.Empty,
        RefreshToken = RefreshToken,
        ExpiresIn = ExpiresIn,
        Scope = Scope,
        ObtainedAt = ObtainedAt,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        TokenEndpointAuthMethod = TokenEndpointAuthMethod,
        AuthorizationServer = AuthorizationServer,
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StoredOAuth))]
internal sealed partial class ConnectorJsonContext : JsonSerializerContext;
