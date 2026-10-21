using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Clicky.Core.Settings;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Clicky.Core.Connectors;

/// <summary>
/// <see cref="IMcpSessionFactory"/> over the official C# MCP SDK: stdio servers as child
/// processes, remote servers over streamable HTTP (with SSE fallback) and MCP OAuth.
/// </summary>
public sealed class SdkMcpSessionFactory(ConnectorSecrets secrets, HttpClient http, ILoggerFactory loggerFactory) : IMcpSessionFactory
{
    private const int StderrTailLines = 20;

    private static readonly string ClientVersion =
        typeof(SdkMcpSessionFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private readonly ILogger _logger = loggerFactory.CreateLogger<SdkMcpSessionFactory>();

    /// <summary>How long the handshake may take. npx/uvx can download a package on first run.</summary>
    public TimeSpan InitializationTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public async Task<IMcpSession> ConnectAsync(ConnectorConfig config, ConnectorAccount account, IOAuthSignIn? signIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(account);

        var missing = secrets.MissingFor(config, account.Id);
        if (missing.Count > 0)
        {
            throw new MissingSecretsException(missing);
        }

        var auth = new AuthBridge(signIn);
        var stderr = new StderrTail(StderrTailLines);
        var session = new SdkMcpSession(auth, stderr, _logger, $"{config.Id}/{account.Id}");
        IClientTransport transport = config.Transport == ConnectorTransport.Http
            ? CreateHttpTransport(config, account, auth)
            : CreateStdioTransport(config, account, stderr);

        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "clicky", Title = "Clicky", Version = ClientVersion },
            InitializationTimeout = InitializationTimeout,
            Handlers = new McpClientHandlers
            {
                NotificationHandlers =
                [
                    new(NotificationMethods.ToolListChangedNotification, (_, _) =>
                    {
                        session.RaiseToolsChanged();
                        return ValueTask.CompletedTask;
                    }),
                ],
            },
        };

        try
        {
            var client = await McpClient.CreateAsync(transport, options, loggerFactory, cancellationToken).ConfigureAwait(false);
            session.Attach(client);
            return session;
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && (auth.SignInRequested || (signIn is null && config.Auth == ConnectorAuth.OAuth && IsUnauthorized(ex))))
        {
            throw new SignInRequiredException($"{config.Name} needs you to sign in.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && stderr.Text is { Length: > 0 } tail)
        {
            // The handshake failing usually means the process died; its stderr says why.
            throw new McpException($"{ex.Message}\n{tail}", ex);
        }
    }

    private static bool IsUnauthorized(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden })
            {
                return true;
            }

            if (current is ClientTransportClosedException { Details: HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Clicky's PATH plus anything added to the registry PATH since it started: Clicky starts at
    /// sign-in and keeps the environment it got, so Node or Python installed afterwards would
    /// otherwise be invisible to npx/uvx until the next sign-in.
    /// </summary>
    internal static string? FreshPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = current.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var known = entries.Select(e => e.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            var stored = Environment.GetEnvironmentVariable("Path", target);
            foreach (var entry in Environment.ExpandEnvironmentVariables(stored ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (known.Add(entry.TrimEnd('\\')))
                {
                    entries.Add(entry);
                }
            }
        }

        return string.Join(';', entries);
    }

    private StdioClientTransport CreateStdioTransport(ConnectorConfig config, ConnectorAccount account, StderrTail stderr)
    {
        if (string.IsNullOrWhiteSpace(config.Command))
        {
            throw new InvalidOperationException($"{config.Name} has no command to run.");
        }

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (FreshPath() is { } path && !config.Environment.Keys.Any(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)))
        {
            environment["PATH"] = path;
        }

        foreach (var (key, value) in config.Environment)
        {
            environment[key] = value;
        }

        foreach (var variable in config.SecretEnvironment)
        {
            environment[variable] = secrets.Read(ConnectorSecrets.EnvironmentName(config.Id, account.Id, variable));
        }

        var name = $"{config.Id}/{account.Id}";
        return new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = name,
                Command = config.Command,
                Arguments = [.. config.Arguments],
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : Environment.ExpandEnvironmentVariables(config.WorkingDirectory),
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(3),
                StandardErrorLines = line =>
                {
                    stderr.Add(line);
                    _logger.LogDebug("[{Server}] {Line}", name, line);
                },
            },
            loggerFactory);
    }

    private HttpClientTransport CreateHttpTransport(ConnectorConfig config, ConnectorAccount account, AuthBridge auth)
    {
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var endpoint) || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{config.Name} has no valid http(s) address.");
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback && config.Auth == ConnectorAuth.OAuth)
        {
            // Tokens would cross the network in clear text.
            throw new InvalidOperationException($"{config.Name} uses OAuth over plain http; use an https address.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in config.Headers)
        {
            headers[key] = value;
        }

        foreach (var header in config.SecretHeaders)
        {
            headers[header] = secrets.Read(ConnectorSecrets.HeaderName(config.Id, account.Id, header)) ?? string.Empty;
        }

        ClientOAuthOptions? oauth = null;
        var tokens = new OAuthTokenStore(secrets, config.Id, account.Id);

        // Without tokens a background connect goes in bare: a server that needs sign-in answers
        // 401 and we stop there. With OAuth options the SDK would register a new client with the
        // authorization server on every attempt before it ever asks for the browser.
        if (config.Auth == ConnectorAuth.OAuth && (auth.SignIn is not null || tokens.HasTokens))
        {
            var port = auth.SignIn?.RedirectUri.Port ?? (tokens.RedirectPort > 0 ? tokens.RedirectPort : 1);
            oauth = new ClientOAuthOptions
            {
                RedirectUri = auth.SignIn?.RedirectUri ?? new Uri($"http://127.0.0.1:{port}{LoopbackOAuthSignIn.CallbackPath}"),
                ClientId = string.IsNullOrEmpty(config.OAuthClientId) ? null : config.OAuthClientId,
                ClientSecret = string.IsNullOrEmpty(config.OAuthClientId) ? null : secrets.Read(ConnectorSecrets.ClientSecretName(config.Id)),
                Scopes = config.OAuthScopes.Count > 0 ? config.OAuthScopes : null,
                AuthorizationCallbackHandler = auth.AuthorizeAsync,
                TokenCache = tokens.AsTokenCache(),
                DynamicClientRegistration = new DynamicClientRegistrationOptions { ClientName = "Clicky for Windows" },
            };
        }

        return new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = $"{config.Id}/{account.Id}",
                Endpoint = endpoint,
                TransportMode = HttpTransportMode.AutoDetect,
                AdditionalHeaders = headers,
                OAuth = oauth,
                ConnectionTimeout = TimeSpan.FromSeconds(30),
            },
            http,
            loggerFactory,
            ownsHttpClient: false);
    }

    internal static ConnectorContent Map(ContentBlock block) => block switch
    {
        TextContentBlock text => new ConnectorContent.Text(text.Text),
        ImageContentBlock image => new ConnectorContent.Image(Convert.ToBase64String(image.DecodedData.Span), image.MimeType),
        EmbeddedResourceBlock { Resource: TextResourceContents text } => new ConnectorContent.Resource(text.Uri, text.MimeType, text.Text),
        EmbeddedResourceBlock { Resource: { } resource } => new ConnectorContent.Resource(resource.Uri, resource.MimeType, null),
        ResourceLinkBlock link => new ConnectorContent.Text($"Resource: {link.Name} <{link.Uri}>{(string.IsNullOrEmpty(link.Description) ? string.Empty : " " + link.Description)}"),
        AudioContentBlock audio => new ConnectorContent.Text($"[audio {audio.MimeType} not shown]"),
        _ => new ConnectorContent.Text($"[{block.Type} content not shown]"),
    };

    /// <summary>Turns the SDK's OAuth callback into a browser sign-in, or a "needs sign-in" flag in the background.</summary>
    private sealed class AuthBridge(IOAuthSignIn? signIn)
    {
        private volatile bool _requested;

        public IOAuthSignIn? SignIn { get; } = signIn;

        public bool SignInRequested => _requested;

        public async Task<AuthorizationResult?> AuthorizeAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
        {
            if (SignIn is null)
            {
                _requested = true;
                throw new SignInRequiredException();
            }

            var redirect = await SignIn.AuthorizeAsync(context.AuthorizationUri, cancellationToken).ConfigureAwait(false);
            if (redirect.Error is not null || string.IsNullOrEmpty(redirect.Code))
            {
                throw new InvalidOperationException(redirect.Error is null
                    ? "The sign-in page did not send an authorization code."
                    : $"Sign-in was refused ({redirect.Error}{(redirect.ErrorDescription is null ? string.Empty : ": " + redirect.ErrorDescription)}).");
            }

            return new AuthorizationResult { Code = redirect.Code, State = redirect.State, Iss = redirect.Issuer };
        }
    }

    private sealed class StderrTail(int capacity)
    {
        private readonly Queue<string> _lines = new();
        private readonly Lock _gate = new();

        public string Text
        {
            get
            {
                lock (_gate)
                {
                    return string.Join('\n', _lines);
                }
            }
        }

        public void Add(string line)
        {
            lock (_gate)
            {
                _lines.Enqueue(line.Length > 500 ? line[..500] : line);
                while (_lines.Count > capacity)
                {
                    _lines.Dequeue();
                }
            }
        }
    }

    private sealed class SdkMcpSession(AuthBridge auth, StderrTail stderr, ILogger logger, string name) : IMcpSession
    {
        private McpClient? _client;
        private Task<SessionEnd>? _completion;

        public event EventHandler? ToolsChanged;

        public string ServerDescription => _client?.ServerInfo is { } info ? $"{info.Name} {info.Version}".Trim() : name;

        public string? Instructions => _client?.ServerInstructions;

        public Task<SessionEnd> Completion => _completion ?? Task.FromResult(new SessionEnd("Not connected."));

        private McpClient Client => _client ?? throw new InvalidOperationException("Not connected.");

        public void Attach(McpClient client)
        {
            _client = client;
            _completion = WatchAsync(client);
        }

        public void RaiseToolsChanged()
        {
            try
            {
                ToolsChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "[{Server}] Tools-changed handler threw", name);
            }
        }

        public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var tools = await Client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return [.. tools.Select(t => t.ProtocolTool).Select(t => new McpToolInfo(
                    t.Name,
                    t.Title ?? t.Annotations?.Title,
                    t.Description,
                    t.InputSchema,
                    t.Annotations?.ReadOnlyHint,
                    t.Annotations?.DestructiveHint,
                    t.Annotations?.OpenWorldHint))];
            }
            catch (Exception ex) when (auth.SignInRequested && ex is not OperationCanceledException)
            {
                throw new SignInRequiredException("The server needs you to sign in again.", ex);
            }
        }

        public async Task<ConnectorToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
        {
            CallToolResult result;
            try
            {
                result = await Client.CallToolAsync(
                    new CallToolRequestParams { Name = toolName, Arguments = new Dictionary<string, JsonElement>(arguments) },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (auth.SignInRequested && ex is not OperationCanceledException)
            {
                throw new SignInRequiredException("The server needs you to sign in again.", ex);
            }

            return new ConnectorToolResult(
                result.IsError == true,
                [.. (result.Content ?? []).Select(Map)],
                result.StructuredContent);
        }

        public async ValueTask DisposeAsync()
        {
            if (_client is { } client)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<SessionEnd> WatchAsync(McpClient client)
        {
            var details = await client.Completion.ConfigureAwait(false);
            if (auth.SignInRequested)
            {
                return new SessionEnd("The server needs you to sign in again.", NeedsSignIn: true);
            }

            var reason = details switch
            {
                StdioClientCompletionDetails { ExitCode: { } code } stdio =>
                    $"The server exited with code {code}." + Tail(stdio.StandardErrorTail),
                HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.Unauthorized } =>
                    "The server rejected the credentials.",
                HttpClientCompletionDetails { HttpStatusCode: { } status } =>
                    $"The server answered HTTP {(int)status}.",
                _ => details.Exception?.Message,
            };

            return new SessionEnd(reason, NeedsSignIn: details is HttpClientCompletionDetails { HttpStatusCode: HttpStatusCode.Unauthorized });
        }

        private string Tail(IReadOnlyList<string>? lines)
        {
            var text = lines is { Count: > 0 } ? string.Join('\n', lines.TakeLast(StderrTailLines)) : stderr.Text;
            return text.Length == 0 ? string.Empty : "\n" + text;
        }
    }
}
