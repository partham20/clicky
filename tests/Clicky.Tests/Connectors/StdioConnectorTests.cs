using System.Net.Http;
using System.Text.Json;
using Clicky.Core.Connectors;
using Clicky.Core.Settings;
using Clicky.Tests.Voice;
using Microsoft.Extensions.Logging.Abstractions;

namespace Clicky.Tests.Connectors;

/// <summary>End to end through the real SDK against Clicky.TestMcpServer as a child process.</summary>
public sealed class StdioConnectorTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Lists_and_calls_tools_passes_secret_environment_and_survives_a_crash()
    {
        var store = new LimitedSecretStore();
        var secrets = new ConnectorSecrets(store);
        var config = TestServer() with { SecretEnvironment = ["CLICKY_TEST_TOKEN"], Environment = new Dictionary<string, string> { ["CLICKY_TEST_PLAIN"] = "plain" } };
        secrets.Write(ConnectorSecrets.EnvironmentName("test", "default", "CLICKY_TEST_TOKEN"), "s3cret");
        var settings = new FakeSettingsStore(ClickySettings.Default with { Connectors = new ConnectorSettings([config]) });
        using var http = new HttpClient();
        await using var hub = new ConnectorHub(settings, new SdkMcpSessionFactory(secrets, http, NullLoggerFactory.Instance), secrets, new RecordingBrowser(), NullLoggerFactory.Instance);

        await hub.StartAsync();
        var tools = await hub.GetToolsAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(["test__echo", "test__add", "test__env", "test__crash", "test__grow"], tools.Select(t => t.Name));
        Assert.True(tools[0].ReadOnly);
        Assert.Equal("[Test server] Repeats the text back.", tools[0].Description);
        Assert.True(tools[3].Destructive);
        Assert.Equal(JsonValueKind.Object, tools[1].InputSchema.ValueKind);
        Assert.Equal("clicky-test 1.0.0", hub.GetStatus("test", "default").Server);

        Assert.Equal("hello", (await hub.CallAsync("test__echo", Args("""{"text":"hello"}"""))).ToText());
        Assert.Equal("5", (await hub.CallAsync("test__add", Args("""{"a":2,"b":3}"""))).ToText());
        Assert.Equal("s3cret", (await hub.CallAsync("test__env", Args("""{"name":"CLICKY_TEST_TOKEN"}"""))).ToText());
        Assert.Equal("plain", (await hub.CallAsync("test__env", Args("""{"name":"CLICKY_TEST_PLAIN"}"""))).ToText());

        var bad = await hub.CallAsync("test__add", Args("""{"a":"two"}"""));
        Assert.True(bad.IsError);

        // tools/list_changed
        await hub.CallAsync("test__grow", Args("{}"));
        await Eventually.TrueAsync(() => hub.Tools.Any(t => t.Name == "test__extra"), "picks up the added tool");

        // The process exits; the connection comes back on its own after the first backoff.
        var crash = await hub.CallAsync("test__crash", Args("{}"));
        Assert.True(crash.IsError);
        await Eventually.TrueAsync(() => hub.GetStatus("test", "default").State != ConnectionState.Connected, "notices the exit");
        await Eventually.TrueAsync(() => hub.GetStatus("test", "default").State == ConnectionState.Connected, "reconnects", timeoutMs: 60_000);
        Assert.Equal("again", (await hub.CallAsync("test__echo", Args("""{"text":"again"}"""))).ToText());
    }

    [Fact]
    public async Task A_command_that_does_not_exist_fails_with_a_reason()
    {
        var secrets = new ConnectorSecrets(new LimitedSecretStore());
        using var http = new HttpClient();
        var factory = new SdkMcpSessionFactory(secrets, http, NullLoggerFactory.Instance) { InitializationTimeout = TimeSpan.FromSeconds(20) };
        var config = new ConnectorConfig(Name: "Nope", Command: "clicky-no-such-command-x").Normalize();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => factory.ConnectAsync(config, config.Accounts[0], null, CancellationToken.None));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Missing_secret_environment_is_reported_before_starting_anything()
    {
        var secrets = new ConnectorSecrets(new LimitedSecretStore());
        using var http = new HttpClient();
        var factory = new SdkMcpSessionFactory(secrets, http, NullLoggerFactory.Instance);
        var config = TestServer() with { SecretEnvironment = ["NEEDED"] };

        var error = await Assert.ThrowsAsync<MissingSecretsException>(() => factory.ConnectAsync(config, config.Accounts[0], null, CancellationToken.None));
        Assert.Equal(["NEEDED"], error.Names);
    }

    internal static ConnectorConfig TestServer()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Clicky.TestMcpServer.dll");
        Assert.True(File.Exists(dll), "The test server is built next to the tests.");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        return new ConnectorConfig(Id: "test", Name: "Test server", Command: dotnet, Arguments: [dll]).Normalize();
    }
}
