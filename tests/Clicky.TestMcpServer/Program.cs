using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Tools: echo (read-only), add, env (reads an environment variable), crash (exits with code 3),
// grow (adds a tool and announces tools/list_changed).
var tools = new McpServerPrimitiveCollection<McpServerTool>();
tools.Add(McpServerTool.Create(
    [Description("Repeats the text back.")] (string text) => text,
    new McpServerToolCreateOptions { Name = "echo", ReadOnly = true }));
tools.Add(McpServerTool.Create(
    [Description("Adds two numbers.")] (int a, int b) => (a + b).ToString(System.Globalization.CultureInfo.InvariantCulture),
    new McpServerToolCreateOptions { Name = "add", ReadOnly = true }));
tools.Add(McpServerTool.Create(
    [Description("Returns the value of an environment variable.")] (string name) => Environment.GetEnvironmentVariable(name) ?? "(unset)",
    new McpServerToolCreateOptions { Name = "env", ReadOnly = true }));
tools.Add(McpServerTool.Create(
    [Description("Exits the server process with code 3.")] () =>
    {
        Console.Error.WriteLine("crash requested");
        Environment.Exit(3);
        return string.Empty;
    },
    new McpServerToolCreateOptions { Name = "crash", Destructive = true }));
tools.Add(McpServerTool.Create(
    [Description("Adds a tool named 'extra'.")] async (McpServer server, CancellationToken cancellationToken) =>
    {
        if (!tools.Any(t => t.ProtocolTool.Name == "extra"))
        {
            tools.Add(McpServerTool.Create(() => "extra", new McpServerToolCreateOptions { Name = "extra", ReadOnly = true }));
        }

        await server.SendNotificationAsync(NotificationMethods.ToolListChangedNotification, cancellationToken);
        return "added";
    },
    new McpServerToolCreateOptions { Name = "grow" }));

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "clicky-test", Version = "1.0.0" },
    Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } },
    ToolCollection = tools,
};

await using var server = McpServer.Create(new StdioServerTransport(options, NullLoggerFactory.Instance), options, NullLoggerFactory.Instance);
await server.RunAsync();
