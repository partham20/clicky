using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;
using Clicky.Tests.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;

// The fake server builds small JSON documents per request; caching their arrays is not worth it.
#pragma warning disable CA1861

namespace Clicky.Tests.Connectors;

/// <summary>
/// End to end through the real SDK over streamable HTTP, against an in-process MCP server that is
/// protected by a minimal OAuth 2.1 authorization server (metadata discovery, dynamic client
/// registration, PKCE, refresh tokens).
/// </summary>
public sealed class HttpConnectorTests
{
    [Fact]
    public async Task Oauth_sign_in_through_the_browser_then_restarts_and_refreshes_without_it()
    {
        await using var server = await FakeOAuthMcpServer.StartAsync();
        var store = new LimitedSecretStore();
        var secrets = new ConnectorSecrets(store);
        var config = new ConnectorConfig(Id: "remote", Name: "Remote", Transport: ConnectorTransport.Http, Url: server.McpUrl, Auth: ConnectorAuth.OAuth).Normalize();
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config]) });
        using var http = new HttpClient();
        using var browserHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        // The "browser": follows the authorization URL (the fake server approves at once) to the loopback redirect.
        var browser = new RecordingBrowser(async uri =>
        {
            using var approve = await browserHttp.GetAsync(uri);
            Assert.Equal(HttpStatusCode.Redirect, approve.StatusCode);
            using var landing = await browserHttp.GetAsync(approve.Headers.Location);
            Assert.Contains("Signed in", await landing.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        });

        await using (var hub = new ConnectorHub(settings, new SdkMcpSessionFactory(secrets, http, NullLoggerFactory.Instance), secrets, browser, NullLoggerFactory.Instance))
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.GetStatus("remote", "default").State == ConnectionState.SignInRequired, "waits for sign-in", 30_000, () => hub.GetStatus("remote", "default"));
            Assert.Empty(browser.Opened);

            await hub.SignInAsync("remote", "default");
            await Eventually.TrueAsync(() => hub.GetStatus("remote", "default").State == ConnectionState.Connected, "connected after sign-in", 30_000);
            var opened = Assert.Single(browser.Opened);
            Assert.StartsWith(server.BaseUrl + "/authorize?", opened.AbsoluteUri, StringComparison.Ordinal);

            Assert.Equal("remote__whoami", Assert.Single(hub.Tools).Name);
            Assert.Equal("user-1", (await hub.CallAsync("remote__whoami", JsonDocument.Parse("{}").RootElement)).ToText());
            Assert.Equal(1, server.Registrations);
        }

        // The access token is longer than one credential entry holds, so it was split.
        Assert.Contains(store.Entries.Keys, k => k == ConnectorSecrets.OAuthName("remote", "default") + "#2");
        Assert.Contains(FakeOAuthMcpServer.AccessTokenPrefix, secrets.Read(ConnectorSecrets.OAuthName("remote", "default")), StringComparison.Ordinal);

        // "Restart": a new hub with the same Credential Manager connects silently, and once the
        // server expires the access token the SDK refreshes it with the stored refresh token.
        server.ExpireAccessTokens();
        await using (var hub = new ConnectorHub(settings, new SdkMcpSessionFactory(secrets, http, NullLoggerFactory.Instance), secrets, browser, NullLoggerFactory.Instance))
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.GetStatus("remote", "default").State == ConnectionState.Connected, "reconnects with stored tokens", 30_000);
            Assert.Equal("user-1", (await hub.CallAsync("remote__whoami", JsonDocument.Parse("{}").RootElement)).ToText());
            Assert.Single(browser.Opened);
            Assert.True(server.Refreshes >= 1);
            Assert.Equal(1, server.Registrations);

            // Sign out forgets the tokens and goes back to waiting for a sign-in.
            await hub.SignOutAsync("remote", "default");
            await Eventually.TrueAsync(() => hub.GetStatus("remote", "default").State == ConnectionState.SignInRequired, "signed out", 30_000);
            Assert.Null(secrets.Read(ConnectorSecrets.OAuthName("remote", "default")));
        }
    }

    [Fact]
    public async Task Api_key_headers_come_from_secrets_per_account()
    {
        await using var server = await FakeOAuthMcpServer.StartAsync();
        var secrets = new ConnectorSecrets(new LimitedSecretStore());
        var config = new ConnectorConfig(
            Id: "remote",
            Name: "Remote",
            Transport: ConnectorTransport.Http,
            Url: server.McpUrl,
            SecretHeaders: ["Authorization"],
            Accounts: [new("a", "A"), new("b", "B")]).Normalize();
        secrets.Write(ConnectorSecrets.HeaderName("remote", "a", "Authorization"), "Bearer " + FakeOAuthMcpServer.StaticKey("alice"));
        secrets.Write(ConnectorSecrets.HeaderName("remote", "b", "Authorization"), "Bearer " + FakeOAuthMcpServer.StaticKey("bob"));
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config]) });
        using var http = new HttpClient();
        await using var hub = new ConnectorHub(settings, new SdkMcpSessionFactory(secrets, http, NullLoggerFactory.Instance), secrets, new RecordingBrowser(), NullLoggerFactory.Instance);

        await hub.StartAsync();
        var tools = await hub.GetToolsAsync(TimeSpan.FromSeconds(30));
        Assert.True(tools.Count == 2, hub.GetStatus("remote", "a").ToString());

        Assert.Equal(["remote-a__whoami", "remote-b__whoami"], tools.Select(t => t.Name));
        Assert.Equal("alice", (await hub.CallAsync("remote-a__whoami", default)).ToText());
        Assert.Equal("bob", (await hub.CallAsync("remote-b__whoami", default)).ToText());
    }

    /// <summary>An MCP endpoint at /mcp plus just enough of an OAuth 2.1 authorization server to protect it.</summary>
    private sealed class FakeOAuthMcpServer : IAsyncDisposable
    {
        public const string AccessTokenPrefix = "at-";

        private readonly WebApplication _app;
        private readonly ConcurrentDictionary<string, (string Challenge, string User)> _codes = new();
        private readonly ConcurrentDictionary<string, string> _accessTokens = new();
        private readonly ConcurrentDictionary<string, string> _refreshTokens = new();
        private int _registrations;
        private int _refreshes;

        private FakeOAuthMcpServer(WebApplication app) => _app = app;

        public string BaseUrl { get; private set; } = string.Empty;

        public string McpUrl => BaseUrl + "/mcp";

        public int Registrations => Volatile.Read(ref _registrations);

        public int Refreshes => Volatile.Read(ref _refreshes);

        public static string StaticKey(string user) => "key-" + user;

        public static async Task<FakeOAuthMcpServer> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services
                .AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools([McpServerTool.Create((McpServer mcp) => (string)mcp.Services!.GetRequiredService<IHttpContextAccessor>().HttpContext!.Items["user"]!, new McpServerToolCreateOptions { Name = "whoami", ReadOnly = true })]);
            builder.Services.AddHttpContextAccessor();
            var app = builder.Build();
            var server = new FakeOAuthMcpServer(app);
            server.Map(app);
            await app.StartAsync();
            server.BaseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
            return server;
        }

        public void ExpireAccessTokens() => _accessTokens.Clear();

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static string NewToken(string prefix) => prefix + Base64Url(RandomNumberGenerator.GetBytes(16));

        private void Map(WebApplication app)
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.Ordinal))
                {
                    var header = context.Request.Headers.Authorization.ToString();
                    var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : string.Empty;
                    string? user = token.StartsWith("key-", StringComparison.Ordinal) ? token[4..] : _accessTokens.GetValueOrDefault(token);
                    if (user is null)
                    {
                        context.Response.StatusCode = 401;
                        context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{BaseUrl}/.well-known/oauth-protected-resource\"";
                        return;
                    }

                    context.Items["user"] = user;
                }

                await next(context);
            });

            app.MapMcp("/mcp");

            app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new Dictionary<string, object>
            {
                ["resource"] = McpUrl,
                ["authorization_servers"] = new[] { BaseUrl },
                ["scopes_supported"] = new[] { "read" },
            }));
            app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Redirect("/.well-known/oauth-protected-resource"));

            IResult Metadata() => Results.Json(new Dictionary<string, object>
            {
                ["issuer"] = BaseUrl,
                ["authorization_endpoint"] = BaseUrl + "/authorize",
                ["token_endpoint"] = BaseUrl + "/token",
                ["registration_endpoint"] = BaseUrl + "/register",
                ["response_types_supported"] = new[] { "code" },
                ["grant_types_supported"] = new[] { "authorization_code", "refresh_token" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
                ["token_endpoint_auth_methods_supported"] = new[] { "none" },
                ["scopes_supported"] = new[] { "read", "offline_access" },
            });
            app.MapGet("/.well-known/oauth-authorization-server", Metadata);
            app.MapGet("/.well-known/openid-configuration", Metadata);

            app.MapPost("/register", async (HttpRequest request) =>
            {
                Interlocked.Increment(ref _registrations);
                using var body = await JsonDocument.ParseAsync(request.Body);
                return Results.Json(
                    new Dictionary<string, object>
                    {
                        ["client_id"] = "client-" + Registrations,
                        ["redirect_uris"] = body.RootElement.GetProperty("redirect_uris").Clone(),
                        ["token_endpoint_auth_method"] = "none",
                        ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                        ["response_types"] = new[] { "code" },
                    },
                    statusCode: 201);
            });

            // Approves immediately, as if the user clicked Allow.
            app.MapGet("/authorize", (HttpRequest request) =>
            {
                var query = request.Query;
                if (query["code_challenge_method"] != "S256" || string.IsNullOrEmpty(query["code_challenge"]))
                {
                    return Results.BadRequest("PKCE required");
                }

                var code = NewToken("code-");
                _codes[code] = (query["code_challenge"]!, "user-1");
                return Results.Redirect($"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(query["state"]!)}");
            });

            app.MapPost("/token", async (HttpRequest request) =>
            {
                var form = await request.ReadFormAsync();
                string user;
                if (form["grant_type"] == "authorization_code")
                {
                    if (!_codes.TryRemove(form["code"]!, out var issued))
                    {
                        return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                    }

                    var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!)));
                    if (challenge != issued.Challenge)
                    {
                        return Results.Json(new { error = "invalid_grant", error_description = "PKCE mismatch" }, statusCode: 400);
                    }

                    user = issued.User;
                }
                else if (form["grant_type"] == "refresh_token" && _refreshTokens.TryRemove(form["refresh_token"]!, out var refreshUser))
                {
                    Interlocked.Increment(ref _refreshes);
                    user = refreshUser;
                }
                else
                {
                    return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
                }

                // Longer than one Credential Manager entry, like real JWTs often are.
                var access = AccessTokenPrefix + new string('x', 1500) + NewToken(string.Empty);
                var refresh = NewToken("rt-");
                _accessTokens[access] = user;
                _refreshTokens[refresh] = user;
                return Results.Json(new Dictionary<string, object>
                {
                    ["access_token"] = access,
                    ["token_type"] = "Bearer",
                    ["expires_in"] = 3600,
                    ["refresh_token"] = refresh,
                    ["scope"] = "read",
                });
            });
        }
    }
}
