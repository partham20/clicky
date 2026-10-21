using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Clicky.Core.Agent.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Clicky.Tests.Agent;

public sealed class CdpConnectionTests
{
    [Fact]
    public async Task Commands_get_their_own_answers_and_events_are_raised()
    {
        await using var browser = await FakeBrowser.StartAsync();
        await using var connection = await CdpConnection.ConnectAsync(browser.Endpoint);
        var events = new List<(string Method, string? Session)>();
        connection.EventReceived += (method, _, session) =>
        {
            lock (events)
            {
                events.Add((method, session));
            }
        };

        var first = connection.SendAsync("Echo.slow", new JsonObject { ["value"] = 1 });
        var second = connection.SendAsync("Echo.fast", new JsonObject { ["value"] = 2 }, sessionId: "S1");

        Assert.Equal(2, (await second).GetProperty("value").GetInt32());
        Assert.Equal(1, (await first).GetProperty("value").GetInt32());
        Assert.True(connection.IsOpen);
        Assert.Contains(("Page.loadEventFired", "S1"), events);
    }

    [Fact]
    public async Task Browser_errors_and_silence_become_cdp_exceptions()
    {
        await using var browser = await FakeBrowser.StartAsync();
        await using var connection = await CdpConnection.ConnectAsync(browser.Endpoint);

        var error = await Assert.ThrowsAsync<CdpException>(() => connection.SendAsync("Target.nope"));
        Assert.Contains("'Target.nope' wasn't found", error.Message, StringComparison.Ordinal);

        var silent = await Assert.ThrowsAsync<CdpException>(() => connection.SendAsync("Never.answer", timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Contains("did not answer Never.answer", silent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_browser_fails_pending_and_new_commands()
    {
        await using var browser = await FakeBrowser.StartAsync();
        await using var connection = await CdpConnection.ConnectAsync(browser.Endpoint);

        await Assert.ThrowsAsync<CdpException>(() => connection.SendAsync("Browser.close"));
        await Task.Delay(100);

        Assert.False(connection.IsOpen);
        await Assert.ThrowsAsync<CdpException>(() => connection.SendAsync("Echo.fast"));
    }

    /// <summary>A WebSocket endpoint that answers like Chrome's DevTools: echoes, errors, events and closing.</summary>
    private sealed class FakeBrowser : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeBrowser(WebApplication app) => _app = app;

        public Uri Endpoint { get; private set; } = null!;

        public static async Task<FakeBrowser> StartAsync()
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            app.UseWebSockets();
            app.Map("/devtools/browser/test", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                await ServeAsync(socket);
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new FakeBrowser(app) { Endpoint = new Uri(address.Replace("http://", "ws://", StringComparison.Ordinal).TrimEnd('/') + "/devtools/browser/test") };
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private static async Task ServeAsync(WebSocket socket)
        {
            var buffer = new byte[64 * 1024];
            var held = new List<JsonNode>();
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                var message = JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, received.Count))!;
                var id = message["id"]!.GetValue<int>();
                switch (message["method"]!.GetValue<string>())
                {
                    case "Echo.slow":
                        held.Add(new JsonObject { ["id"] = id, ["result"] = message["params"]!.DeepClone() });
                        break;
                    case "Echo.fast":
                        await SendAsync(socket, new JsonObject { ["method"] = "Page.loadEventFired", ["params"] = new JsonObject(), ["sessionId"] = message["sessionId"]?.DeepClone() });
                        await SendAsync(socket, new JsonObject { ["id"] = id, ["result"] = message["params"]!.DeepClone(), ["sessionId"] = message["sessionId"]?.DeepClone() });
                        foreach (var answer in held)
                        {
                            await SendAsync(socket, answer);
                        }

                        held.Clear();
                        break;
                    case "Never.answer":
                        break;
                    case "Browser.close":
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        return;
                    default:
                        await SendAsync(socket, new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"'{message["method"]}' wasn't found" } });
                        break;
                }
            }
        }

        private static Task SendAsync(WebSocket socket, JsonNode message) =>
            socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }
}
