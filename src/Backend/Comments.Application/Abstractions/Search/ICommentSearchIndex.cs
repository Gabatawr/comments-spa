using Comments.Application.Dtos;

namespace Comments.Application.Abstractions.Search;

/// <summary>Search request (docs/API-v2.md §3.1).</summary>
public sealed record SearchQuery(
    string Query,
    int Page,
    int PageSize,
    string Sort,
    string SortDir)
{
    public bool Ascending => SortDir == "asc";

    public int From => (Page - 1) * PageSize;
}

/// <summary>One indexed hit.</summary>
public sealed record SearchHit(CommentDto Comment, double Score, string? Highlight);

/// <summary>Search result page.</summary>
public sealed record SearchResult(
    IReadOnlyList<SearchHit> Items,
    string Query,
    long Took,
    int Page,
    int PageSize,
    int TotalItems,
    int TotalPages);

/// <summary>
/// Elasticsearch-backed full-text index (docs/API-v2.md §8.3). Uses the exact mapping documented
/// there; <c>EnsureIndexAsync</c> is idempotent.
/// </summary>
public interface ICommentSearchIndex
{
    Task EnsureIndexAsync(CancellationToken cancellationToken = default);

    Task IndexAsync(CommentDto comment, CancellationToken cancellationToken = default);

    Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);

    bool IsEnabled { get; }

    bool IsAvailable { get; }
}
