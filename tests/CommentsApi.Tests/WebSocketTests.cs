using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using CommentsApi.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// WS /ws contract (docs/API.md 2.9). These run against the in-memory TestServer client with a
/// generous timeout. If the backend implementation proves flaky the test is marked skipped and
/// the reason is documented in docs/qa/report.md.
/// </summary>
public class WebSocketTests : IntegrationTestBase
{
    private static async Task<JsonNode> ReceiveJsonAsync(WebSocket socket, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WebSocket was closed by the server before a message arrived.");
            }

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray()))
               ?? throw new InvalidOperationException("Empty WebSocket message.");
    }

    private async Task<WebSocket> ConnectAsync(CancellationToken token)
    {
        var client = Factory.Server.CreateWebSocketClient();
        return await client.ConnectAsync(new Uri(Factory.Server.BaseAddress, "ws"), token);
    }

    [Fact]
    public async Task Hello_then_comment_created_broadcast()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await ConnectAsync(cts.Token);

        var hello = await ReceiveJsonAsync(socket, cts.Token);
        Assert.Equal("hello", hello["type"]!.GetValue<string>());
        Assert.True(hello["clients"]!.GetValue<int>() >= 1);
        Assert.Equal("connected", hello["message"]?.GetValue<string>());

        await CreateCommentAsync("WsUser01", email: "ws1@example.com", text: "<i>ws</i>");

        JsonNode? created = null;
        for (var i = 0; i < 5 && created is null; i++)
        {
            var message = await ReceiveJsonAsync(socket, cts.Token);
            if (message["type"]?.GetValue<string>() == "comment.created")
            {
                created = message;
            }
        }

        Assert.NotNull(created);
        Assert.Equal("WsUser01", created!["comment"]!["userName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ping_gets_pong_and_invalid_json_does_not_close_the_socket()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await ConnectAsync(cts.Token);

        var hello = await ReceiveJsonAsync(socket, cts.Token);
        Assert.Equal("hello", hello["type"]!.GetValue<string>());

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("this is not json"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cts.Token);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cts.Token);

        JsonNode? pong = null;
        for (var i = 0; i < 5 && pong is null; i++)
        {
            var message = await ReceiveJsonAsync(socket, cts.Token);
            if (message["type"]?.GetValue<string>() == "pong")
            {
                pong = message;
            }
        }

        Assert.NotNull(pong);
        Assert.Equal(WebSocketState.Open, socket.State);
    }
}
