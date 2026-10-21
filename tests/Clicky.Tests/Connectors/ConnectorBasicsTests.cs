using System.Text.Json;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;

namespace Clicky.Tests.Connectors;

public sealed class ConnectorBasicsTests
{
    [Theory]
    [InlineData("Linear", "linear")]
    [InlineData("  GitHub (work)  ", "github-work")]
    [InlineData("Brave Search!", "brave-search")]
    [InlineData("Café/Ünïcode", "cafe-unicode")]
    [InlineData("***", "server")]
    [InlineData("a-very-long-connector-name-that-keeps-going", "a-very-long-connector-na")]
    public void Slugs_are_safe_ids(string text, string expected) => Assert.Equal(expected, ConnectorNames.Slug(text, "server"));

    [Fact]
    public void Unique_appends_a_counter_within_the_length_limit()
    {
        var used = new HashSet<string> { "files", "files-2", new string('x', 24) };

        Assert.Equal("files-3", ConnectorNames.Unique("files", used));
        Assert.Equal("other", ConnectorNames.Unique("other", used));
        Assert.Equal(new string('x', 22) + "-2", ConnectorNames.Unique(new string('x', 24), used));
    }

    [Fact]
    public void Tool_names_are_valid_for_the_messages_api()
    {
        Assert.Equal("linear__list_issues", ConnectorNames.ToolName("linear", "list_issues"));
        Assert.Equal("gmail-work__send_email", ConnectorNames.ToolName(ConnectorNames.Prefix("gmail", "work", true), "send.email"));

        var longA = ConnectorNames.ToolName("a-very-long-connector-na", new string('t', 60) + "a");
        var longB = ConnectorNames.ToolName("a-very-long-connector-na", new string('t', 60) + "b");
        Assert.Equal(64, longA.Length);
        Assert.NotEqual(longA, longB);
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", longA);
    }

    [Fact]
    public void Normalize_repairs_ids_accounts_and_ranges()
    {
        var settings = new ConnectorSettings(
        [
            new ConnectorConfig(Name: "Files", Command: " npx ", Accounts: [new("", "Work"), new("", "Work"), null!], ToolTimeoutSeconds: 99_999),
            null!,
            new ConnectorConfig(Name: "Files", Transport: (ConnectorTransport)42, Arguments: ["a", null!], Environment: new Dictionary<string, string> { [" KEY "] = "v", [""] = "x" }),
            new ConnectorConfig(Url: "https://mcp.linear.app/mcp", Transport: ConnectorTransport.Http, OAuthRedirectPort: 70_000),
        ]).Normalize();

        Assert.Equal(["files", "files-2", "https-mcp-linear-app-mcp"], settings.Servers.Select(s => s.Id));
        var first = settings.Servers[0];
        Assert.Equal("npx", first.Command);
        Assert.Equal(["work", "work-2"], first.Accounts.Select(a => a.Id));
        Assert.Equal(ConnectorConfig.MaxToolTimeoutSeconds, first.ToolTimeoutSeconds);
        var second = settings.Servers[1];
        Assert.Equal(ConnectorTransport.Stdio, second.Transport);
        Assert.Equal(["a"], second.Arguments);
        Assert.Equal(new Dictionary<string, string> { ["KEY"] = "v" }, second.Environment);
        Assert.Equal(ConnectorAccount.DefaultId, Assert.Single(second.Accounts).Id);
        Assert.Equal(0, settings.Servers[2].OAuthRedirectPort);
        Assert.Equal("https://mcp.linear.app/mcp", settings.Servers[2].Name);
    }

    [Fact]
    public void Settings_compare_by_content_and_round_trip_through_json()
    {
        var a = Sample();
        var b = Sample();
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, a with { Connectors = new ConnectorSettings([a.Connectors.Servers[0] with { DisabledTools = ["x"] }]) });
        Assert.True(a.Connectors.Servers[0].ConnectionEquals(a.Connectors.Servers[0] with { Name = "Renamed", DisabledTools = ["x"] }));
        Assert.False(a.Connectors.Servers[0].ConnectionEquals(a.Connectors.Servers[0] with { Arguments = ["other"] }));

        var json = JsonSerializer.Serialize(a, SettingsJsonContext.ReadableClickySettings);
        Assert.DoesNotContain("hasCredentials", json, StringComparison.Ordinal);
        Assert.DoesNotContain("enabledAccounts", json, StringComparison.Ordinal);
        Assert.Contains("\"secretEnvironment\": [", json, StringComparison.Ordinal);
        var back = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.ClickySettings)!.Normalize();
        Assert.Equal(a, back);

        // A file from before M16 has no "connectors" section.
        var old = JsonSerializer.Deserialize("""{"schemaVersion":1}""", SettingsJsonContext.Default.ClickySettings)!.Normalize();
        Assert.Empty(old.Connectors.Servers);

        // A hand-written minimal server gets every default.
        var minimal = JsonSerializer.Deserialize("""{"connectors":{"servers":[{"name":"Files","command":"npx"}]}}""", SettingsJsonContext.Default.ClickySettings)!.Normalize();
        var server = Assert.Single(minimal.Connectors.Servers);
        Assert.True(server.Enabled);
        Assert.Equal(ConnectorConfig.DefaultToolTimeoutSeconds, server.ToolTimeoutSeconds);
        Assert.Equal(ConnectorAccount.DefaultId, Assert.Single(server.Accounts).Id);
        Assert.True(server.Accounts[0].Enabled);
    }

    [Fact]
    public void Long_secrets_are_split_across_credential_entries()
    {
        var store = new LimitedSecretStore();
        var secrets = new ConnectorSecrets(store);
        var token = string.Concat(Enumerable.Range(0, 5000).Select(i => (char)('a' + (i % 26))));

        secrets.Write("mcp/x/default/oauth", token);
        Assert.Equal(token, secrets.Read("mcp/x/default/oauth"));
        Assert.Equal(5, store.Entries.Count);

        secrets.Write("mcp/x/default/oauth", "short");
        Assert.Equal("short", secrets.Read("mcp/x/default/oauth"));
        Assert.Single(store.Entries);

        secrets.Write("mcp/x/default/oauth", null);
        Assert.Empty(store.Entries);
        Assert.Null(secrets.Read("mcp/x/default/oauth"));

        // A value typed into Credential Manager by hand is read as-is.
        store.Write("mcp/x/default/env/TOKEN", "ghp_plain|value");
        Assert.Equal("ghp_plain|value", secrets.Read("mcp/x/default/env/TOKEN"));

        // A missing tail reads as no secret rather than a truncated one.
        secrets.Write("mcp/y", token);
        store.Delete("mcp/y#3");
        Assert.Null(secrets.Read("mcp/y"));
    }

    [Fact]
    public void Missing_secrets_are_reported_per_account()
    {
        var secrets = new ConnectorSecrets(new LimitedSecretStore());
        var config = new ConnectorConfig(Id: "gh", SecretEnvironment: ["GITHUB_TOKEN"], SecretHeaders: ["Authorization"]).Normalize();

        Assert.Equal(["GITHUB_TOKEN", "Authorization"], secrets.MissingFor(config, "work"));
        secrets.Write(ConnectorSecrets.EnvironmentName("gh", "work", "GITHUB_TOKEN"), "t");
        secrets.Write(ConnectorSecrets.HeaderName("gh", "work", "Authorization"), "Bearer t");
        Assert.Empty(secrets.MissingFor(config, "work"));
        Assert.Equal(2, secrets.MissingFor(config, "home").Count);
    }

    [Fact]
    public void Imports_claude_desktop_and_mcp_json_servers()
    {
        const string Json = """
            {
              // comments are fine
              "mcpServers": {
                "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\Desktop"] },
                "github": { "command": "docker", "args": ["run", "-i", "--rm", "ghcr.io/github/github-mcp-server"], "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_123" } },
                "linear": { "type": "http", "url": "https://mcp.linear.app/mcp" },
                "internal": { "url": "https://mcp.example.com/mcp", "headers": { "X-API-Key": "k", "X-Team": "blue" } },
                "broken": { "args": [] },
              }
            }
            """;

        var servers = McpServersImport.Parse(Json, out var problems);

        Assert.Equal(["filesystem", "github", "linear", "internal"], servers.Select(s => s.Config.Name));
        Assert.Equal("broken: no \"command\" or \"url\", skipped.", Assert.Single(problems));

        var files = servers[0].Config;
        Assert.Equal(ConnectorTransport.Stdio, files.Transport);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\Desktop"], files.Arguments);

        Assert.Equal(["GITHUB_PERSONAL_ACCESS_TOKEN"], servers[1].Config.SecretEnvironment);
        Assert.Equal("ghp_123", servers[1].SecretEnvironment["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Empty(servers[1].Config.Environment);

        Assert.Equal(ConnectorTransport.Http, servers[2].Config.Transport);
        Assert.Equal(ConnectorAuth.OAuth, servers[2].Config.Auth);

        var internalServer = servers[3];
        Assert.Equal(ConnectorAuth.None, internalServer.Config.Auth);
        Assert.Equal(["X-API-Key"], internalServer.Config.SecretHeaders);
        Assert.Equal("blue", internalServer.Config.Headers["X-Team"]);
        Assert.Equal("k", internalServer.SecretHeaders["X-API-Key"]);

        var single = McpServersImport.Parse("""{"command":"uvx","args":["mcp-server-time"]}""", out _);
        Assert.Equal("uvx", Assert.Single(single).Config.Command);

        Assert.Empty(McpServersImport.Parse("not json", out problems));
        Assert.StartsWith("That is not valid JSON", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-y @scope/pkg", new[] { "-y", "@scope/pkg" })]
    [InlineData("--root \"C:\\Program Files\\x\"", new[] { "--root", "C:\\Program Files\\x" })]
    [InlineData("a\\\\b \"say \\\"hi\\\"\" \"\"", new[] { "a\\\\b", "say \"hi\"", "" })]
    [InlineData("  ", new string[0])]
    public void Command_lines_split_like_windows(string line, string[] expected)
    {
        var parts = CommandLine.Split(line);
        Assert.Equal(expected, parts);
        Assert.Equal(expected, CommandLine.Split(CommandLine.Join(parts)));
    }

    [Fact]
    public void Redirect_query_is_parsed_and_only_web_urls_open()
    {
        var redirect = LoopbackOAuthSignIn.ParseQuery("?code=abc%2B1&state=xyz&iss=https%3A%2F%2Fauth.example.com");
        Assert.Equal(new OAuthRedirect("abc+1", "xyz", "https://auth.example.com", null, null), redirect);
        Assert.Equal("access_denied", LoopbackOAuthSignIn.ParseQuery("error=access_denied&error_description=No+thanks").Error);

        Assert.True(LoopbackOAuthSignIn.IsWebUrl(new Uri("https://auth.example.com/authorize?x=1")));
        Assert.True(LoopbackOAuthSignIn.IsWebUrl(new Uri("http://127.0.0.1:9000/authorize")));
        Assert.False(LoopbackOAuthSignIn.IsWebUrl(new Uri("http://auth.example.com/authorize")));
        Assert.False(LoopbackOAuthSignIn.IsWebUrl(new Uri("file:///C:/Windows/System32/calc.exe")));
        Assert.False(LoopbackOAuthSignIn.IsWebUrl(new Uri("ms-settings:privacy")));
    }

    private static ClickySettings Sample() => (ClickySettings.Default with
    {
        Connectors = new ConnectorSettings(
        [
            new ConnectorConfig(
                Name: "GitHub",
                Command: "docker",
                Arguments: ["run", "-i"],
                Environment: new Dictionary<string, string> { ["LOG"] = "1" },
                SecretEnvironment: ["GITHUB_TOKEN"],
                Accounts: [new("work", "Work"), new("home", "Home", Enabled: false)]),
            new ConnectorConfig(Name: "Linear", Transport: ConnectorTransport.Http, Url: "https://mcp.linear.app/mcp", Auth: ConnectorAuth.OAuth, OAuthScopes: ["read"]),
        ]),
    }).Normalize();
}
