using System.Text.Json.Nodes;
using Xunit;

namespace CommentsApi.Tests.Infrastructure;

/// <summary>
/// Middle+ keyset pagination (docs/API-v2.md §4.5). Exercises the real REST endpoint end-to-end:
/// cursor round-trip, duplicate-free deep pages with equal sort values (id tie-break) and the
/// mismatch 400. Offset paging is already covered by CommentsTests.
/// </summary>
public class KeysetPaginationTests : IntegrationTestBase
{
    private async Task<JsonNode> ListWithCursorAsync(string? cursor, int pageSize, string sortBy, string sortDir)
    {
        var url = $"/api/comments?pageSize={pageSize}&sortBy={sortBy}&sortDir={sortDir}";
        if (cursor is not null)
        {
            url += $"&cursor={Uri.EscapeDataString(cursor)}";
        }

        var response = await Client.GetAsync(url);
        Assert.True(
            response.IsSuccessStatusCode,
            $"GET {url} -> HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await ReadJsonAsync(response);
    }

    private static List<int> Ids(JsonNode page)
        => page["items"]!.AsArray().Select(i => i!["id"]!.GetValue<int>()).ToList();

    [Fact]
    public async Task Cursor_by_userName_covers_every_root_once_including_equal_values()
    {
        // Two roots share a userName so the id ASC tie-breaker is exercised.
        await CreateCommentAsync("DupUser1", email: "dup1a@example.com");
        await CreateCommentAsync("DupUser1", email: "dup1b@example.com");
        await CreateCommentAsync("KsUserA1", email: "ksa@example.com");
        await CreateCommentAsync("KsUserB1", email: "ksb@example.com");
        await CreateCommentAsync("KsUserC1", email: "ksc@example.com");

        var full = await ListAsync(pageSize: 100, sortBy: "userName", sortDir: "asc");
        var expected = Ids(full);
        Assert.Equal(5, expected.Count);

        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;

        while (pages++ < 10)
        {
            var page = await ListWithCursorAsync(cursor, pageSize: 2, sortBy: "userName", sortDir: "asc");
            seen.AddRange(Ids(page));
            cursor = page["nextCursor"]?.GetValue<string>();
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Cursor_by_createdAt_descending_covers_every_root_once()
    {
        for (var i = 0; i < 6; i++)
        {
            await CreateCommentAsync($"KsDate{i:D2}", email: $"ksdate{i:D2}@example.com");
        }

        var full = await ListAsync(pageSize: 100, sortBy: "createdAt", sortDir: "desc");
        var expected = Ids(full);
        Assert.Equal(6, expected.Count);

        var seen = new List<int>();
        string? cursor = null;
        var pages = 0;

        while (pages++ < 10)
        {
            var page = await ListWithCursorAsync(cursor, pageSize: 2, sortBy: "createdAt", sortDir: "desc");
            seen.AddRange(Ids(page));
            cursor = page["nextCursor"]?.GetValue<string>();
            if (cursor is null)
            {
                break;
            }
        }

        Assert.Equal(expected, seen);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task Cursor_does_not_match_sort_returns_400_detail()
    {
        await CreateCommentAsync("KsMismatch1", email: "ksm1@example.com");
        await CreateCommentAsync("KsMismatch2", email: "ksm2@example.com");

        var first = await ListWithCursorAsync(cursor: null, pageSize: 1, sortBy: "userName", sortDir: "asc");
        var cursor = first["nextCursor"]!.GetValue<string>();

        var response = await Client.GetAsync($"/api/comments?sortBy=email&sortDir=asc&cursor={Uri.EscapeDataString(cursor)}");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        var body = await ReadJsonAsync(response);
        Assert.Equal("Cursor does not match sort.", body["detail"]?.GetValue<string>());
    }

    [Fact]
    public async Task Last_page_has_no_next_cursor()
    {
        await CreateCommentAsync("KsLast001", email: "kslast@example.com");

        var page = await ListWithCursorAsync(cursor: null, pageSize: 25, sortBy: "createdAt", sortDir: "desc");
        Assert.True(page["nextCursor"] is null, "single page must not emit nextCursor");
    }
}
