using System.Text.Json.Nodes;
using StackExchange.Redis;
using Xunit;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// Real-adapter verification against the compose data services (docs/ARCHITECTURE-v2.md §7,
/// Category=IntegrationBroker). Run with:
/// <c>COMMENTS_TEST_EXTERNAL_SERVICES=1 COMMENTS_TEST_REDIS=localhost:56379
///  COMMENTS_TEST_ELASTIC=http://localhost:59200
///  COMMENTS_TEST_RABBITMQ=amqp://comments:comments@localhost:5672/ dotnet test --filter Category=IntegrationBroker</c>
/// Without the opt-in env var these tests are inert (the default suite must not need Redis/ES/Rabbit).
/// </summary>
[Trait("Category", "IntegrationBroker")]
public class ExternalServicesTests : IntegrationTestBase
{
    [Fact]
    public async Task Captcha_is_stored_in_redis_as_plain_string_with_ttl()
    {
        if (!TestAppFactory.UseExternalServices)
        {
            return;
        }

        var response = await Client.GetAsync("/api/captcha");
        Assert.True(response.IsSuccessStatusCode);
        var node = await ReadJsonAsync(response);
        var captchaId = node["captchaId"]!.GetValue<string>();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(TestAppFactory.RedisConnectionString);
        var db = redis.GetDatabase();
        var key = $"captcha:{captchaId}";

        var value = await db.StringGetAsync(key);
        Assert.False(value.IsNullOrEmpty, $"Redis key {key} was not set");
        Assert.Equal(6, ((string)value!).Length);

        var ttl = await db.KeyTimeToLiveAsync(key);
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(300));

        // One-time consume via the API (atomic GETDEL on the Redis store).
        var answer = (string)value!;
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "RedisCap1", email: "rediscap@example.com",
            captchaId: captchaId, captchaAnswer: "WRONG1"));
        Assert.NotNull(errors["captcha"]);
        Assert.False(await db.KeyExistsAsync(key), "captcha key must be consumed even on a wrong answer");
    }

    [Fact]
    public async Task Comment_created_is_indexed_and_searchable_in_elasticsearch()
    {
        if (!TestAppFactory.UseExternalServices)
        {
            return;
        }

        var token = "esprobe" + Guid.NewGuid().ToString("N")[..10];
        var comment = await CreateCommentAsync("EsProbe01", email: "esprobe@example.com", text: $"Hello {token} world");
        var commentId = comment["id"]!.GetValue<int>();

        var found = await PollAsync(
            async () =>
            {
                var response = await Client.GetAsync($"/api/search?q={token}");
                if (!response.IsSuccessStatusCode)
                {
                    return 0;
                }

                var page = await ReadJsonAsync(response);
                return page["items"]!.AsArray()
                    .Count(i => i!["comment"]!["id"]!.GetValue<int>() == commentId);
            },
            count => count >= 1,
            TimeSpan.FromSeconds(20));

        Assert.True(found >= 1, $"comment #{commentId} was not indexed/searchable within 20s");
    }

    [Fact]
    public async Task Broker_health_is_ok_when_rabbit_is_configured()
    {
        if (!TestAppFactory.UseExternalServices || TestAppFactory.RabbitConnectionString is null)
        {
            return;
        }

        var health = await ReadJsonAsync(await Client.GetAsync("/api/health"));
        Assert.Equal("ok", health["broker"]!.GetValue<string>());
        Assert.Equal("ok", health["redis"]!.GetValue<string>());
        Assert.Equal("ok", health["search"]!.GetValue<string>());
    }
}
