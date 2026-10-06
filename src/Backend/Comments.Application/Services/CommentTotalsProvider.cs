using Comments.Application.Abstractions.Caching;
using Comments.Application.Abstractions.Persistence;

namespace Comments.Application.Services;

/// <summary>
/// Shared, cached access to <see cref="CommentTotals"/> (docs/API-v2.md §3.5, §9). Both the list
/// endpoint (page count) and <c>GET /api/stats</c> need it, and on a large table the counts behind
/// it are the most expensive read in the system: recomputing the root count on every page-cache
/// miss is what made <c>browse</c> the slowest scenario in perf/report.md §5.1.
///
/// Freshness comes from invalidation, not from the TTL. The key is dropped by
/// <see cref="CommentCacheInvalidation"/> on every write; the TTL is only a safety net for writers
/// that bypass the pipeline (a bulk <c>COPY</c> seed, a manual SQL fix-up), where nothing would
/// otherwise ever expire.
/// </summary>
public sealed class CommentTotalsProvider
{
    /// <summary>Safety net only — normal freshness comes from explicit invalidation on write.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly ICommentRepository _repository;
    private readonly ICacheService _cache;

    public CommentTotalsProvider(ICommentRepository repository, ICacheService cache)
    {
        _repository = repository;
        _cache = cache;
    }

    public async Task<CommentTotals> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var cached = await _cache.GetAsync<CommentTotals>(CacheKeys.Totals, cancellationToken);
            if (cached is not null)
            {
                return cached;
            }
        }
        catch
        {
            // A cache outage degrades to the database, never to an error.
        }

        var totals = await _repository.GetTotalsAsync(cancellationToken);

        try
        {
            await _cache.SetAsync(CacheKeys.Totals, totals, Ttl, cancellationToken);
        }
        catch
        {
            // Same on the write side: caching is best effort.
        }

        return totals;
    }
}
