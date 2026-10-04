using System.Net;
using System.Text.Json.Nodes;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>List/read/sort/pagination/tree behaviour (docs/API.md 2.2, 2.3, 2.4).</summary>
public class CommentsTests : IntegrationTestBase
{
    // LIFO / ordering assertions need distinguishable CreatedAt values even if the backend
    // truncates to whole seconds.
    private static Task DistinctTimeAsync() => Task.Delay(1100);

    private static List<int> Ids(JsonNode list)
        => list["items"]!.AsArray().Select(i => i!["id"]!.GetValue<int>()).ToList();

    [Fact]
    public async Task Create_returns_201_location_and_client_identity()
    {
        var captcha = await NewCaptchaAsync();
        var response = await PostCommentRawAsync(
            userName: "CreateUser1",
            email: "create1@example.com",
            homePage: "https://example.com",
            text: "Hello <strong>world</strong>",
            captchaId: captcha.Id,
            captchaAnswer: captcha.Code);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var node = await ReadJsonAsync(response);
        var comment = UnwrapComment(node);
        var id = comment["id"]!.GetValue<int>();

        Assert.NotNull(response.Headers.Location);
        Assert.EndsWith($"/api/comments/{id}", response.Headers.Location!.ToString());

        Assert.Equal("CreateUser1", comment["userName"]!.GetValue<string>());
        Assert.Equal("create1@example.com", comment["email"]!.GetValue<string>());
        Assert.Equal("https://example.com", comment["homePage"]!.GetValue<string>());
        Assert.True(comment["parentId"] is null);

        var createdAt = comment["createdAt"]!.GetValue<string>();
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$", createdAt);

        Assert.Contains("CommentsQA", comment["userAgent"]?.GetValue<string>() ?? string.Empty);
        // TestServer may not supply a remote IP; the real IP is verified by the e2e curl run.
        if (comment["clientIp"] is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(comment["clientIp"]!.GetValue<string>()));
        }
    }

    [Fact]
    public async Task Default_list_is_lifo_createdAt_desc()
    {
        var first = await CreateCommentAsync("LifoUser1", email: "lifo1@example.com");
        await DistinctTimeAsync();
        var second = await CreateCommentAsync("LifoUser2", email: "lifo2@example.com");
        await DistinctTimeAsync();
        var third = await CreateCommentAsync("LifoUser3", email: "lifo3@example.com");

        var list = await ListAsync();

        Assert.Equal("createdAt", list["sortBy"]!.GetValue<string>());
        Assert.Equal("desc", list["sortDir"]!.GetValue<string>());
        Assert.Equal(
            new[] { third["id"]!.GetValue<int>(), second["id"]!.GetValue<int>(), first["id"]!.GetValue<int>() },
            Ids(list));
    }

    [Fact]
    public async Task Sort_by_userName_ascending_and_descending()
    {
        var alpha = await CreateCommentAsync("Alpha1", email: "alpha@example.com");
        var mike = await CreateCommentAsync("Mike2", email: "mike@example.com");
        var zed = await CreateCommentAsync("Zed3", email: "zed@example.com");

        var asc = await ListAsync(sortBy: "userName", sortDir: "asc");
        Assert.Equal(
            new[] { alpha["id"]!.GetValue<int>(), mike["id"]!.GetValue<int>(), zed["id"]!.GetValue<int>() },
            Ids(asc));

        var desc = await ListAsync(sortBy: "userName", sortDir: "desc");
        Assert.Equal(
            new[] { zed["id"]!.GetValue<int>(), mike["id"]!.GetValue<int>(), alpha["id"]!.GetValue<int>() },
            Ids(desc));
    }

    [Fact]
    public async Task Sort_by_email_ascending_and_descending()
    {
        var a = await CreateCommentAsync("EmailSort1", email: "aaa@example.com");
        var b = await CreateCommentAsync("EmailSort2", email: "bbb@example.com");
        var c = await CreateCommentAsync("EmailSort3", email: "ccc@example.com");

        var asc = await ListAsync(sortBy: "email", sortDir: "asc");
        Assert.Equal(new[] { a["id"]!.GetValue<int>(), b["id"]!.GetValue<int>(), c["id"]!.GetValue<int>() }, Ids(asc));

        var desc = await ListAsync(sortBy: "email", sortDir: "desc");
        Assert.Equal(new[] { c["id"]!.GetValue<int>(), b["id"]!.GetValue<int>(), a["id"]!.GetValue<int>() }, Ids(desc));
    }

    [Fact]
    public async Task Sort_by_createdAt_ascending_and_descending()
    {
        var first = await CreateCommentAsync("DateSort1", email: "d1@example.com");
        await DistinctTimeAsync();
        var second = await CreateCommentAsync("DateSort2", email: "d2@example.com");
        await DistinctTimeAsync();
        var third = await CreateCommentAsync("DateSort3", email: "d3@example.com");

        var asc = await ListAsync(sortBy: "createdAt", sortDir: "asc");
        Assert.Equal(
            new[] { first["id"]!.GetValue<int>(), second["id"]!.GetValue<int>(), third["id"]!.GetValue<int>() },
            Ids(asc));

        var desc = await ListAsync(sortBy: "createdAt", sortDir: "desc");
        Assert.Equal(
            new[] { third["id"]!.GetValue<int>(), second["id"]!.GetValue<int>(), first["id"]!.GetValue<int>() },
            Ids(desc));
    }

    [Fact]
    public async Task Replies_are_always_ascending_by_createdAt()
    {
        var root = await CreateCommentAsync("ReplyAsc1", email: "ra1@example.com");
        var reply1 = await CreateCommentAsync(
            "ReplyAsc2", email: "ra2@example.com", parentId: root["id"]!.GetValue<int>().ToString());
        await DistinctTimeAsync();
        var reply2 = await CreateCommentAsync(
            "ReplyAsc3", email: "ra3@example.com", parentId: root["id"]!.GetValue<int>().ToString());

        var list = await ListAsync(pageSize: 100);
        var item = list["items"]!.AsArray().Single(i => i!["id"]!.GetValue<int>() == root["id"]!.GetValue<int>());
        var replies = item!["replies"]!.AsArray();

        Assert.Equal(
            new[] { reply1["id"]!.GetValue<int>(), reply2["id"]!.GetValue<int>() },
            replies.Select(r => r!["id"]!.GetValue<int>()).ToArray());
    }

    [Fact]
    public async Task Nested_reply_tree_is_complete_and_root_only()
    {
        var root = await CreateCommentAsync("TreeRoot1", email: "tree@example.com");
        var rootId = root["id"]!.GetValue<int>();
        var r1 = await CreateCommentAsync("TreeReply1", email: "tr1@example.com", parentId: rootId.ToString());
        var r2 = await CreateCommentAsync("TreeReply2", email: "tr2@example.com", parentId: r1["id"]!.GetValue<int>().ToString());
        var r3 = await CreateCommentAsync("TreeReply3", email: "tr3@example.com", parentId: rootId.ToString());

        var list = await ListAsync(pageSize: 100);

        // Only the root appears at top level.
        Assert.Equal(1, list["totalItems"]!.GetValue<int>());
        Assert.Equal(new[] { rootId }, Ids(list));

        var item = list["items"]!.AsArray().Single()!;
        var direct = item["replies"]!.AsArray();
        Assert.Equal(2, direct.Count);
        Assert.Equal(
            new[] { r1["id"]!.GetValue<int>(), r3["id"]!.GetValue<int>() }.OrderBy(x => x),
            direct.Select(x => x!["id"]!.GetValue<int>()).OrderBy(x => x));

        var r1Node = direct.Single(x => x!["id"]!.GetValue<int>() == r1["id"]!.GetValue<int>())!;
        Assert.Single(r1Node["replies"]!.AsArray());
        Assert.Equal(r2["id"]!.GetValue<int>(), r1Node["replies"]![0]!["id"]!.GetValue<int>());

        // replyCount is either direct or total descendants; both are >= direct count.
        Assert.True(item["replyCount"]!.GetValue<int>() >= 2);
    }

    [Fact]
    public async Task Pagination_default_is_25_per_page_with_correct_math()
    {
        for (var i = 0; i < 30; i++)
        {
            await CreateCommentAsync($"PgUser{i:D2}", email: $"pg{i:D2}@example.com");
        }

        var page1 = await ListAsync();
        Assert.Equal(1, page1["page"]!.GetValue<int>());
        Assert.Equal(25, page1["pageSize"]!.GetValue<int>());
        Assert.Equal(30, page1["totalItems"]!.GetValue<int>());
        Assert.Equal(2, page1["totalPages"]!.GetValue<int>());
        Assert.Equal(25, page1["items"]!.AsArray().Count);

        var page2 = await ListAsync(page: 2);
        Assert.Equal(5, page2["items"]!.AsArray().Count);
        Assert.Equal(30, page2["totalItems"]!.GetValue<int>());
    }

    [Fact]
    public async Task Pagination_out_of_range_page_is_empty_not_404()
    {
        await CreateCommentAsync("OorUser01", email: "oor1@example.com");

        var page3 = await Client.GetAsync("/api/comments?page=3");
        Assert.Equal(HttpStatusCode.OK, page3.StatusCode);
        var node = await ReadJsonAsync(page3);
        Assert.Empty(node["items"]!.AsArray());

        var page99 = await Client.GetAsync("/api/comments?page=99");
        Assert.Equal(HttpStatusCode.OK, page99.StatusCode);
        Assert.Empty((await ReadJsonAsync(page99))["items"]!.AsArray());
    }

    [Fact]
    public async Task Pagination_custom_page_size_computes_total_pages()
    {
        for (var i = 0; i < 12; i++)
        {
            await CreateCommentAsync($"PsUser{i:D2}", email: $"ps{i:D2}@example.com");
        }

        var pageSize5 = await ListAsync(pageSize: 5);
        Assert.Equal(5, pageSize5["pageSize"]!.GetValue<int>());
        Assert.Equal(12, pageSize5["totalItems"]!.GetValue<int>());
        Assert.Equal(3, pageSize5["totalPages"]!.GetValue<int>());
        Assert.Equal(5, pageSize5["items"]!.AsArray().Count);

        var all = await ListAsync(pageSize: 100);
        Assert.Equal(12, all["items"]!.AsArray().Count);
        Assert.Equal(1, all["totalPages"]!.GetValue<int>());
    }

    [Fact]
    public async Task Get_by_id_returns_tree_and_unknown_id_is_404()
    {
        var root = await CreateCommentAsync("GetById01", email: "gb1@example.com");
        var reply = await CreateCommentAsync(
            "GetById02", email: "gb2@example.com", parentId: root["id"]!.GetValue<int>().ToString());

        var response = await Client.GetAsync($"/api/comments/{root["id"]!.GetValue<int>()}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var comment = UnwrapComment(await ReadJsonAsync(response));
        Assert.Equal(root["id"]!.GetValue<int>(), comment["id"]!.GetValue<int>());
        Assert.Equal(reply["id"]!.GetValue<int>(), comment["replies"]![0]!["id"]!.GetValue<int>());

        var missing = await Client.GetAsync("/api/comments/999999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task List_cache_is_invalidated_after_create()
    {
        await CreateCommentAsync("CacheUser1", email: "cache1@example.com");
        var before = await ListAsync();
        Assert.Equal(1, before["totalItems"]!.GetValue<int>());

        await CreateCommentAsync("CacheUser2", email: "cache2@example.com");
        var after = await ListAsync();
        Assert.Equal(2, after["totalItems"]!.GetValue<int>());
    }
}
