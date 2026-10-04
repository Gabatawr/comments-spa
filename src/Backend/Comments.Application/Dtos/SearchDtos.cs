namespace Comments.Application.Dtos;

/// <summary>One Elasticsearch hit (docs/API-v2.md §1, §8.3).</summary>
public sealed class SearchHitDto
{
    /// <summary>Comment projection without the reply tree.</summary>
    public CommentDto Comment { get; set; } = new();

    public double Score { get; set; }

    /// <summary>Highlighted <c>textPlain</c> fragment with &lt;em&gt; markers, or null.</summary>
    public string? Highlight { get; set; }
}

/// <summary>Response of <c>GET /api/search</c> and the GraphQL <c>search</c> query (docs/API-v2.md §3.1).</summary>
public sealed class SearchPageDto
{
    public List<SearchHitDto> Items { get; set; } = new();
    public string Query { get; set; } = string.Empty;
    public long Took { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
}
