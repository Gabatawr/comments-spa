namespace Comments.Application.Abstractions.Caching;

/// <summary>
/// The cache-key grammar (docs/API-v2.md §9). It sits next to the port rather than inside one
/// adapter, because the adapter side (invalidation) and the web layer (page cache) must agree on
/// the exact strings — a second copy is only a silent way for them to drift apart.
/// </summary>
public static class CacheKeys
{
    /// <summary>
    /// Page-cache generation. Every write bumps it, so stale page keys stop being read instead of
    /// being deleted one by one — a single key does the work of all of them (docs/API-v2.md §9).
    /// </summary>
    public const string Version = "comments:version";

    /// <summary>
    /// Aggregate counters. Read both by <c>GET /api/stats</c> and by list pagination, so the
    /// <c>COUNT(*)</c> behind them runs once per write epoch instead of once per request.
    /// </summary>
    public const string Totals = "stats:totals";

    public static string Page(string sortBy, string sortDir, int page, int pageSize, long version)
        => $"comments:page:{sortBy}:{sortDir}:{page}:{pageSize}:v{version}";

    public static string Captcha(string captchaId) => $"captcha:{captchaId}";

    // There is deliberately no per-comment key. A by-id read returns the whole reply subtree, so a
    // reply anywhere in that subtree — not just a direct child — would invalidate the ancestor's
    // entry, and keeping it correct would mean walking the parent chain on every write. The page
    // generation above is the answer to exactly that problem; a per-item cache would reintroduce it.
}
