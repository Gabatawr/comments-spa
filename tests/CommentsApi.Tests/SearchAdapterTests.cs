using System.Net;
using System.Text;
using System.Text.Json;
using Comments.Application.Abstractions.Search;
using Comments.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// Wire-level tests for the Elasticsearch adapter (docs/API-v2.md §8.3). It speaks raw REST over
/// <see cref="HttpClient"/> precisely so there is no client library to keep in step with the cluster,
/// which also means a stub handler is all it takes to pin the request it really sends — the part a
/// real cluster could not check for us.
/// </summary>
public class SearchAdapterTests
{
    [Fact]
    public async Task Search_asks_the_cluster_for_exact_totals()
    {
        // Without track_total_hits, ES 7.x/8.x reports hits.total.value as a lower bound capped at
        // 10 000, so totalItems/totalPages would silently stop counting past ten thousand documents.
        var (index, handler) = Build(Respond(@"{""hits"":{""total"":{""value"":5,""relation"":""eq""},""hits"":[]}}"));

        await index.SearchAsync(new SearchQuery("ель", 1, 25, "relevance", "desc"));

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.True(body.RootElement.GetProperty("track_total_hits").GetBoolean());
        Assert.Equal(0, body.RootElement.GetProperty("from").GetInt32());
        Assert.Equal(25, body.RootElement.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task Search_reads_the_object_form_of_total()
    {
        var (index, _) = Build(Respond(@"{""took"":7,""hits"":{""total"":{""value"":12345,""relation"":""eq""},""hits"":[]}}"));

        var result = await index.SearchAsync(new SearchQuery("ель", 2, 25, "relevance", "desc"));

        Assert.Equal(12345, result.TotalItems);
        Assert.Equal(494, result.TotalPages);
        Assert.Equal(7, result.Took);
        Assert.Equal(2, result.Page);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Search_parses_a_hit_with_score_and_highlight()
    {
        var (index, _) = Build(Respond(
            """
            {"hits":{"total":{"value":1,"relation":"eq"},"hits":[
              {"_score":2.5,"_source":{"id":42,"parentId":null,"userName":"Alisa","email":"a@example.com",
               "textPlain":"стенд","createdAt":"2026-10-04T12:00:00Z","clientIp":"127.0.0.1"},
               "highlight":{"textPlain":["<em>стенд</em>"]}}]}}
            """));

        var result = await index.SearchAsync(new SearchQuery("стенд", 1, 25, "relevance", "desc"));

        var hit = Assert.Single(result.Items);
        Assert.Equal(42, hit.Comment.Id);
        Assert.Equal("Alisa", hit.Comment.UserName);
        Assert.Equal(2.5, hit.Score);
        Assert.Equal("<em>стенд</em>", hit.Highlight);
    }

    [Fact]
    public async Task Search_sends_a_sort_clause_only_when_asked_to_sort_by_date()
    {
        var (index, handler) = Build(Respond(@"{""hits"":{""total"":{""value"":0,""relation"":""eq""},""hits"":[]}}"));

        await index.SearchAsync(new SearchQuery("a", 1, 25, "createdAt", "asc"));

        using var body = JsonDocument.Parse(handler.LastBody!);
        var sort = body.RootElement.GetProperty("sort")[0].GetProperty("createdAt").GetProperty("order").GetString();
        Assert.Equal("asc", sort);
    }

    [Fact]
    public async Task Search_surfaces_a_cluster_error_instead_of_returning_an_empty_page()
    {
        var (index, _) = Build(new StubHandler(HttpStatusCode.InternalServerError, @"{""error"":""boom""}"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => index.SearchAsync(new SearchQuery("a", 1, 25, "relevance", "desc")));

        Assert.Contains("500", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Indexing_writes_the_document_under_the_comment_id()
    {
        var (index, handler) = Build(new StubHandler(HttpStatusCode.Created));

        await index.IndexAsync(new Comments.Application.Dtos.CommentDto
        {
            Id = 42,
            UserName = "Alisa",
            Email = "a@example.com",
            TextPlain = "plain text only",
        });

        Assert.Equal(HttpMethod.Put, handler.LastRequest!.Method);
        Assert.Equal("/comments/_doc/42", handler.LastRequest.RequestUri!.AbsolutePath);

        using var body = JsonDocument.Parse(handler.LastBody!);
        // The index holds the plain-text projection: search must not match on markup.
        Assert.Equal("plain text only", body.RootElement.GetProperty("textPlain").GetString());
    }

    [Fact]
    public void The_disabled_adapter_reports_itself_and_returns_nothing()
    {
        ICommentSearchIndex index = new NoopSearchIndex();

        Assert.False(index.IsEnabled);
        Assert.False(index.IsAvailable);
    }

    private static HttpResponseMessage Respond(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static (ElasticsearchSearchIndex Index, StubHandler Handler) Build(HttpResponseMessage response)
        => Build(new StubHandler(response));

    private static (ElasticsearchSearchIndex Index, StubHandler Handler) Build(StubHandler handler)
    {
        var options = Options.Create(new ElasticOptions
        {
            Url = "http://elastic.test:9200",
            IndexName = "comments",
        });

        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://elastic.test:9200/"),
        };

        return (new ElasticsearchSearchIndex(http, options, NullLogger<ElasticsearchSearchIndex>.Instance), handler);
    }

    /// <summary>Records the outgoing request and replays a canned response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpStatusCode status, string body = "{}")
            : this(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            })
        {
        }

        public StubHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _response;
        }
    }
}
