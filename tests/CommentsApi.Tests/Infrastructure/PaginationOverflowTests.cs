using System.Net;
using Xunit;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// Regression for the int32 offset overflow: an out-of-range <c>page</c> must be an empty 200,
/// never a 500 (docs/API-v2.md §2.3, §4.7).
/// </summary>
public class PaginationOverflowTests : IntegrationTestBase
{
    [Theory]
    [InlineData(100000000, 25)]
    [InlineData(100000000, 100)]
    [InlineData(2147483647, 25)]
    [InlineData(2147483647, 100)]
    [InlineData(2147483646, 25)]
    public async Task Huge_page_returns_200_with_empty_items(int page, int pageSize)
    {
        await CreateCommentAsync("Overflow1", email: "overflow@example.com");

        var response = await Client.GetAsync($"/api/comments?page={page}&pageSize={pageSize}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = await ReadJsonAsync(response);
        Assert.Empty(node["items"]!.AsArray());
        Assert.Equal(1, node["totalItems"]!.GetValue<int>());
        Assert.True(node["nextCursor"] is null);
    }

    [Fact]
    public async Task Page_one_still_returns_items_after_the_overflow_guard()
    {
        var comment = await CreateCommentAsync("Overflow2", email: "overflow2@example.com");

        var response = await Client.GetAsync("/api/comments?page=1&pageSize=25");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = await ReadJsonAsync(response);
        Assert.Single(node["items"]!.AsArray());
        Assert.Equal(comment["id"]!.GetValue<int>(), node["items"]![0]!["id"]!.GetValue<int>());
    }
}
