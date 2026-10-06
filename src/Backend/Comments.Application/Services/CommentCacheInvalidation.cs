using Comments.Application.Abstractions.Caching;

namespace Comments.Application.Services;

/// <summary>
/// What a write invalidates (docs/API-v2.md §9), in one place because two callers perform it: the
/// create pipeline does it synchronously, so the instance that served the POST answers fresh
/// straight away, and the broker projection does it again so the other replicas drop their pages
/// too. Both must drop exactly the same keys — otherwise one of the two paths quietly serves stale
/// data and only a load test on a second instance would notice.
/// </summary>
public static class CommentCacheInvalidation
{
    /// <summary>
    /// Drops the page generation and the aggregate counters. Never throws: a cache hiccup must
    /// degrade to a cache miss, not fail a request whose comment is already committed. The first
    /// error is returned so the caller can log it with its own context.
    /// </summary>
    public static async Task<Exception?> AfterWriteAsync(
        ICacheService cache,
        CancellationToken cancellationToken = default)
    {
        Exception? failure = null;

        try
        {
            await cache.IncrementAsync(CacheKeys.Version, 1, ttl: null, cancellationToken);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await cache.RemoveAsync(CacheKeys.Totals, cancellationToken);
        }
        catch (Exception ex)
        {
            failure ??= ex;
        }

        return failure;
    }
}
