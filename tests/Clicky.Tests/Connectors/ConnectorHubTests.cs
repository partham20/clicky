using System.Text.Json;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Clicky.Tests.Connectors;

public sealed class ConnectorHubTests
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;

    [Fact]
    public async Task Publishes_prefixed_tools_and_routes_calls()
    {
        var (hub, _, factory) = Create(Server("Linear"));
        await using (hub)
        {
            await hub.StartAsync();
            var tools = await hub.GetToolsAsync(TimeSpan.FromSeconds(5));

            var tool = Assert.Single(tools);
            Assert.Equal("linear__echo", tool.Name);
            Assert.Equal("echo", tool.ServerToolName);
            Assert.StartsWith("[Linear] ", tool.Description, StringComparison.Ordinal);
            Assert.True(tool.ReadOnly);
            Assert.False(tool.NeedsConfirmation);
            Assert.True(hub.IsConnectorTool("linear__echo"));
            Assert.False(hub.IsConnectorTool("say"));

            var result = await hub.CallAsync("linear__echo", JsonDocument.Parse("""{"text":"hi"}""").RootElement);
            Assert.False(result.IsError);
            Assert.Equal("echo:text=hi", result.ToText());
            Assert.Equal(1, factory.Attempts);
        }
    }

    [Fact]
    public async Task Several_accounts_get_separate_tool_names_and_labels()
    {
        var config = Server("Gmail") with { Accounts = [new ConnectorAccount("work", "Work"), new ConnectorAccount("home", "Personal")] };
        var (hub, _, factory) = Create(config);
        await using (hub)
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.Tools.Count == 2, "both accounts connect");

            Assert.Equal(["gmail-work__echo", "gmail-home__echo"], hub.Tools.Select(t => t.Name));
            Assert.Equal(["[Gmail · Work] Does echo.", "[Gmail · Personal] Does echo."], hub.Tools.Select(t => t.Description));

            var accounts = new List<string>();
            factory.Sessions.ForEach(s => s.OnCall = (name, _, _) =>
            {
                lock (accounts)
                {
                    accounts.Add(name);
                }

                return Task.FromResult(new ConnectorToolResult(false, [new ConnectorContent.Text(s.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture))]));
            });
            var work = await hub.CallAsync("gmail-work__echo", NoArguments);
            var home = await hub.CallAsync("gmail-home__echo", NoArguments);
            Assert.NotEqual(work.ToText(), home.ToText());
        }
    }

    [Fact]
    public async Task Disabled_tools_connectors_and_accounts_are_left_out_live()
    {
        var settings = new FakeSettingsStore(ClickySettings.Default with
        {
            Connectors = new ConnectorSettings([Server("Files")]),
        });
        var factory = new FakeSessionFactory { Connect = (_, _, _) => new FakeSession([FakeSession.Tool("read"), FakeSession.Tool("write", readOnly: false)]) };
        await using var hub = new ConnectorHub(settings, factory, new ConnectorSecrets(new LimitedSecretStore()), new RecordingBrowser(), NullLoggerFactory.Instance);
        await hub.StartAsync();
        await Eventually.TrueAsync(() => hub.Tools.Count == 2, "connected");

        var write = hub.Tools.Single(t => t.ServerToolName == "write");
        Assert.True(write.NeedsConfirmation);
        Assert.True(write.Destructive);

        var changes = 0;
        hub.ToolsChanged += (_, _) => Interlocked.Increment(ref changes);
        await hub.UpdateConnectorAsync("files", c => c with { DisabledTools = ["write"] });
        await Eventually.TrueAsync(() => hub.Tools.Count == 1, "tool filter applies without reconnecting");
        Assert.Equal(1, factory.Attempts);
        Assert.True(changes >= 1);

        await hub.UpdateConnectorAsync("files", c => c with { Enabled = false });
        await Eventually.TrueAsync(() => hub.Tools.Count == 0, "connector switched off");
        Assert.True(factory.Sessions[0].Disposed);
        Assert.Equal(ConnectionState.Off, hub.GetStatus("files", ConnectorAccount.DefaultId).State);

        var result = await hub.CallAsync("files__read", NoArguments);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Changing_the_command_restarts_the_connection()
    {
        var (hub, settings, factory) = Create(Server("Files"));
        await using (hub)
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.Tools.Count == 1, "connected");

            await settings.UpdateAsync(s => s with { Connectors = new ConnectorSettings([s.Connectors.Servers[0] with { Arguments = ["--root", "C:\\"] }]) });
            await Eventually.TrueAsync(() => factory.Attempts == 2 && hub.Tools.Count == 1, "reconnected with new arguments");
            Assert.True(factory.Sessions[0].Disposed);
        }
    }

    [Fact]
    public async Task Reconnects_after_a_crash_with_backoff()
    {
        var time = new FakeTimeProvider();
        var (hub, _, factory) = Create(Server("Files"), time);
        await using (hub)
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.Tools.Count == 1, "connected");

            factory.Sessions[0].Drop("The server exited with code 3.");
            await Eventually.TrueAsync(() => hub.GetStatus("files", "default").State == ConnectionState.Failed, "shows the failure");
            var status = hub.GetStatus("files", "default");
            Assert.Equal("The server exited with code 3.", status.Message);
            Assert.Equal(time.GetUtcNow() + AccountConnection.Backoff[0], status.RetryAt);
            Assert.Empty(hub.Tools);

            time.Advance(AccountConnection.Backoff[0]);
            await Eventually.TrueAsync(() => hub.Tools.Count == 1 && factory.Attempts == 2, "reconnected after the first backoff");
        }
    }

    [Fact]
    public async Task Gives_up_on_a_server_that_never_starts()
    {
        var time = new FakeTimeProvider();
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([Server("Broken")]) });
        var factory = new FakeSessionFactory { Connect = (_, _, _) => throw new InvalidOperationException("'nopex' is not recognized") };
        await using var hub = new ConnectorHub(settings, factory, new ConnectorSecrets(new LimitedSecretStore()), new RecordingBrowser(), NullLoggerFactory.Instance, time);
        await hub.StartAsync();

        for (var i = 1; i < AccountConnection.MaxAttemptsBeforeFirstConnect; i++)
        {
            await Eventually.TrueAsync(() => factory.Attempts == i && hub.GetStatus("broken", "default").RetryAt is not null, $"attempt {i} waits to retry");
            time.Advance(AccountConnection.Backoff[i - 1]);
        }

        await Eventually.TrueAsync(() => hub.GetStatus("broken", "default") is { State: ConnectionState.Failed, RetryAt: null }, "gave up");
        Assert.Contains("nopex", hub.GetStatus("broken", "default").Message, StringComparison.Ordinal);
        time.Advance(TimeSpan.FromMinutes(10));
        await Task.Delay(100);
        Assert.Equal(AccountConnection.MaxAttemptsBeforeFirstConnect, factory.Attempts);

        // Reconnect (the Retry button) tries again.
        factory.Connect = (_, _, _) => new FakeSession([FakeSession.Tool("echo")]);
        await hub.ReconnectAsync("broken");
        await Eventually.TrueAsync(() => hub.Tools.Count == 1, "manual retry connects");
    }

    [Fact]
    public async Task Waits_for_sign_in_without_opening_a_browser_and_signs_in_on_request()
    {
        var config = Server("Linear") with { Transport = ConnectorTransport.Http, Url = "https://mcp.example.test/mcp", Auth = ConnectorAuth.OAuth };
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config]) });
        var browser = new RecordingBrowser();
        var factory = new FakeSessionFactory
        {
            Connect = (_, _, signIn) => signIn is null ? throw new SignInRequiredException() : new FakeSession([FakeSession.Tool("issues")]),
        };
        await using var hub = new ConnectorHub(settings, factory, new ConnectorSecrets(new LimitedSecretStore()), browser, NullLoggerFactory.Instance);
        await hub.StartAsync();

        var tools = await hub.GetToolsAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(tools);
        Assert.Equal(ConnectionState.SignInRequired, hub.GetStatus("linear", "default").State);
        Assert.Empty(browser.Opened);

        var status = await hub.SignInAsync("linear", "default");
        Assert.Equal(ConnectionState.Connected, (await WaitForStateAsync(hub, "linear", ConnectionState.Connected)).State);
        Assert.Equal("linear__issues", Assert.Single(hub.Tools).Name);
        Assert.NotNull(status);

        // The server revoking the session sends it back to waiting for sign-in.
        factory.Sessions[^1].Drop("401", needsSignIn: true);
        await WaitForStateAsync(hub, "linear", ConnectionState.SignInRequired);
        Assert.Empty(hub.Tools);
    }

    [Fact]
    public async Task Missing_secrets_wait_for_settings_instead_of_retrying()
    {
        var config = Server("GitHub") with { SecretEnvironment = ["GITHUB_TOKEN"] };
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config]) });
        var secrets = new ConnectorSecrets(new LimitedSecretStore());
        var factory = new FakeSessionFactory
        {
            Connect = (c, a, _) => secrets.MissingFor(c, a.Id) is { Count: > 0 } missing ? throw new MissingSecretsException(missing) : new FakeSession([FakeSession.Tool("repos")]),
        };
        await using var hub = new ConnectorHub(settings, factory, secrets, new RecordingBrowser(), NullLoggerFactory.Instance);
        await hub.StartAsync();

        var status = await WaitForStateAsync(hub, "github", ConnectionState.NeedsSecrets);
        Assert.Contains("GITHUB_TOKEN", status.Message, StringComparison.Ordinal);

        secrets.Write(ConnectorSecrets.EnvironmentName("github", "default", "GITHUB_TOKEN"), "ghp_x");
        await hub.ReconnectAsync("github", "default");
        await WaitForStateAsync(hub, "github", ConnectionState.Connected);
    }

    [Fact]
    public async Task Tool_list_changes_are_followed()
    {
        var (hub, _, factory) = Create(Server("Files"));
        await using (hub)
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.Tools.Count == 1, "connected");

            factory.Sessions[0].ChangeTools([FakeSession.Tool("echo"), FakeSession.Tool("extra")]);
            await Eventually.TrueAsync(() => hub.Tools.Count == 2, "picked up tools/list_changed");
        }
    }

    [Fact]
    public async Task Call_failures_come_back_as_error_results()
    {
        var (hub, _, factory) = Create(Server("Files") with { ToolTimeoutSeconds = 5 });
        await using (hub)
        {
            await hub.StartAsync();
            await Eventually.TrueAsync(() => hub.Tools.Count == 1, "connected");

            Assert.True((await hub.CallAsync("nope__echo", NoArguments)).IsError);
            Assert.True((await hub.CallAsync("files__echo", JsonDocument.Parse("[1]").RootElement)).IsError);

            factory.Sessions[0].OnCall = (_, _, _) => throw new InvalidOperationException("disk on fire");
            var failed = await hub.CallAsync("files__echo", NoArguments);
            Assert.True(failed.IsError);
            Assert.Contains("disk on fire", failed.ToText(), StringComparison.Ordinal);

            using var cancel = new CancellationTokenSource();
            factory.Sessions[0].OnCall = async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return ConnectorToolResult.Error("unreachable");
            };
            var call = hub.CallAsync("files__echo", NoArguments, cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        }
    }

    [Fact]
    public void Long_results_are_cut()
    {
        var result = new ConnectorToolResult(false, [new ConnectorContent.Text(new string('a', 80)), new ConnectorContent.Image("AAAA", "image/png"), new ConnectorContent.Text(new string('b', 80))]);

        var cut = ConnectorHub.Truncate(result, 100);

        Assert.Equal(4, cut.Content.Count);
        Assert.Equal(80, ((ConnectorContent.Text)cut.Content[0]).Value.Length);
        Assert.IsType<ConnectorContent.Image>(cut.Content[1]);
        Assert.Equal(20, ((ConnectorContent.Text)cut.Content[2]).Value.Length);
        Assert.Contains("60 of 160", ((ConnectorContent.Text)cut.Content[3]).Value, StringComparison.Ordinal);
        Assert.Same(result, ConnectorHub.Truncate(result, 1000));
    }

    [Fact]
    public async Task Removing_a_connector_deletes_its_secrets()
    {
        var store = new LimitedSecretStore();
        var secrets = new ConnectorSecrets(store);
        var config = Server("GitHub") with { SecretEnvironment = ["GITHUB_TOKEN"], Accounts = [new("work", "Work"), new("home", "Home")] };
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config, Server("Other")]) });
        await using var hub = new ConnectorHub(settings, new FakeSessionFactory(), secrets, new RecordingBrowser(), NullLoggerFactory.Instance);
        secrets.Write(ConnectorSecrets.EnvironmentName("github", "work", "GITHUB_TOKEN"), new string('w', 3000));
        secrets.Write(ConnectorSecrets.EnvironmentName("github", "home", "GITHUB_TOKEN"), "h");
        secrets.Write(ConnectorSecrets.OAuthName("other", "default"), "keep");

        await hub.RemoveAccountAsync("github", "home");
        Assert.Null(secrets.Read(ConnectorSecrets.EnvironmentName("github", "home", "GITHUB_TOKEN")));
        Assert.NotNull(secrets.Read(ConnectorSecrets.EnvironmentName("github", "work", "GITHUB_TOKEN")));

        await hub.RemoveConnectorAsync("github");
        Assert.Null(settings.Current.Connectors.Find("github"));
        Assert.DoesNotContain(store.Entries.Keys, k => k.StartsWith("mcp/github", StringComparison.Ordinal));
        Assert.Equal("keep", secrets.Read(ConnectorSecrets.OAuthName("other", "default")));
    }

    [Fact]
    public async Task Import_writes_secrets_before_the_first_connect()
    {
        var settings = new FakeSettingsStore(ClickySettings.Default);
        var secrets = new ConnectorSecrets(new LimitedSecretStore());
        var seen = new List<string?>();
        var factory = new FakeSessionFactory
        {
            Connect = (c, a, _) =>
            {
                lock (seen)
                {
                    seen.Add(secrets.Read(ConnectorSecrets.EnvironmentName(c.Id, a.Id, "API_KEY")));
                }

                return new FakeSession([FakeSession.Tool("search")]);
            },
        };
        await using var hub = new ConnectorHub(settings, factory, secrets, new RecordingBrowser(), NullLoggerFactory.Instance);
        await hub.StartAsync();

        var imported = McpServersImport.Parse("""{"mcpServers":{"brave":{"command":"npx","args":["-y","@brave/brave-search-mcp-server"],"env":{"API_KEY":"k1"}}}}""", out _);
        var ids = await hub.ImportAsync(imported);

        Assert.Equal(["brave"], ids);
        await Eventually.TrueAsync(() => hub.Tools.Count == 1, "imported server connects");
        Assert.Equal(["k1"], seen);
        Assert.Empty(settings.Current.Connectors.Servers[0].Environment);
    }

    private static ConnectorConfig Server(string name) => new ConnectorConfig(Name: name, Command: "fake").Normalize();

    private static (ConnectorHub Hub, FakeSettingsStore Settings, FakeSessionFactory Factory) Create(ConnectorConfig config, TimeProvider? time = null)
    {
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config]) });
        var factory = new FakeSessionFactory();
        var hub = new ConnectorHub(settings, factory, new ConnectorSecrets(new LimitedSecretStore()), new RecordingBrowser(), NullLoggerFactory.Instance, time);
        return (hub, settings, factory);
    }

    private static async Task<ConnectionStatus> WaitForStateAsync(ConnectorHub hub, string connectorId, ConnectionState state)
    {
        await Eventually.TrueAsync(() => hub.GetStatus(connectorId, ConnectorAccount.DefaultId).State == state, $"{connectorId} reaches {state}");
        return hub.GetStatus(connectorId, ConnectorAccount.DefaultId);
    }
}
