using System.Net;
using System.Text.Json.Nodes;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>GET /api/health contract (docs/API.md 2.8) and Junior+ wiring visibility.</summary>
public class HealthTests : IntegrationTestBase
{
    private async Task<JsonNode> HealthAsync()
    {
        var response = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    [Fact]
    public async Task Health_reports_all_contract_sections()
    {
        var node = await HealthAsync();

        Assert.Equal("ok", node["status"]!.GetValue<string>());
        Assert.Equal("ok", node["database"]!.GetValue<string>());
        Assert.Equal("ok", node["cache"]!.GetValue<string>());
        Assert.NotNull(node["queue"]);
        Assert.NotNull(node["queue"]!["pending"]);
        Assert.NotNull(node["queue"]!["processed"]);
        Assert.NotNull(node["websocket"]);
        Assert.NotNull(node["websocket"]!["clients"]);
        Assert.False(string.IsNullOrWhiteSpace(node["version"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Queue_processed_counter_increases_after_create()
    {
        var before = (await HealthAsync())["queue"]!["processed"]!.GetValue<long>();

        await CreateCommentAsync("QueueUser1", email: "queue1@example.com");

        var after = await PollAsync(
            async () => (await HealthAsync())["queue"]!["processed"]!.GetValue<long>(),
            value => value > before,
            TimeSpan.FromSeconds(10));

        Assert.True(after > before, $"queue.processed did not increase (before={before}, after={after}).");
    }
}
