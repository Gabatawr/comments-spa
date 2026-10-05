using System.Net;
using System.Text.Json.Nodes;
using Comments.Application.Abstractions.Providers;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// <c>GET /api/info</c> plus the provider block of <c>GET /api/health</c>: a deployment must be
/// able to say what it is running on without anyone reading environment variables
/// (docs/ARCHITECTURE-v2.md §3).
///
/// The default test app is wired through the legacy v2.0 keys (cache=memory, messaging=inmemory,
/// search off), so these assertions also pin the alias behaviour end to end.
/// </summary>
public class InfoEndpointTests : IntegrationTestBase
{
    private async Task<JsonNode> InfoAsync()
    {
        var response = await Client.GetAsync("/api/info");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    [Fact]
    public async Task Info_reports_the_active_provider_for_every_port()
    {
        var node = await InfoAsync();
        var providers = node["providers"]!.AsArray().ToDictionary(
            p => p!["port"]!.GetValue<string>(),
            p => p!["value"]!.GetValue<string>());

        Assert.Equal(ProviderCatalog.Ports.Count, providers.Count);
        Assert.Equal("postgres", providers[ProviderCatalog.Database]);
        Assert.Equal("filesystem", providers[ProviderCatalog.Storage]);

        // The test app runs the in-process providers (see TestAppFactory.BuildSettings).
        Assert.Equal("memory", providers[ProviderCatalog.Cache]);
        Assert.Equal("inmemory", providers[ProviderCatalog.Messaging]);
        Assert.Equal("none", providers[ProviderCatalog.Search]);
    }

    [Fact]
    public async Task Info_lists_what_each_port_could_run_on_instead()
    {
        var node = await InfoAsync();

        var storage = node["providers"]!.AsArray()
            .Single(p => p!["port"]!.GetValue<string>() == ProviderCatalog.Storage)!;

        var alternatives = storage["alternatives"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();

        // The active value is not repeated, and object storage is offered as a real option.
        Assert.DoesNotContain("filesystem", alternatives);
        Assert.Contains("s3", alternatives);
        Assert.Contains("azureblob", alternatives);
        Assert.False(storage["selfContained"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Info_marks_in_process_providers_as_self_contained_and_disabled()
    {
        var node = await InfoAsync();

        var cache = node["providers"]!.AsArray()
            .Single(p => p!["port"]!.GetValue<string>() == ProviderCatalog.Cache)!;

        Assert.Equal("memory", cache["value"]!.GetValue<string>());
        Assert.True(cache["selfContained"]!.GetValue<bool>());
        Assert.Equal("disabled", cache["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Info_describes_the_strictness_policy_in_force()
    {
        var node = await InfoAsync();

        Assert.True(node["strict"]!.GetValue<bool>());
        Assert.False(node["failFastOnUnavailable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Health_reports_the_active_provider_set_next_to_the_statuses()
    {
        var response = await Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var node = await ReadJsonAsync(response);
        var providers = node["providers"]!;

        Assert.Equal("postgres", providers[ProviderCatalog.Database]!.GetValue<string>());
        Assert.Equal("filesystem", providers[ProviderCatalog.Storage]!.GetValue<string>());
        Assert.Equal("memory", providers[ProviderCatalog.Cache]!.GetValue<string>());

        // Names say which adapter answers, statuses say whether it is reachable.
        Assert.Equal("ok", node["storage"]!.GetValue<string>());
        Assert.Equal("disabled", node["redis"]!.GetValue<string>());
        Assert.Equal("disabled", node["broker"]!.GetValue<string>());
        Assert.Equal("disabled", node["search"]!.GetValue<string>());
    }
}
