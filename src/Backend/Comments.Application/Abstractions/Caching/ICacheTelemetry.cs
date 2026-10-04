namespace Comments.Application.Abstractions.Caching;

/// <summary>
/// Optional cache telemetry for <c>GET /api/stats</c> (docs/API-v2.md §3.5).
/// Resolve it optionally; when absent the API reports 0.0.
/// </summary>
public interface ICacheTelemetry
{
    long Hits { get; }

    long Misses { get; }

    double HitRate { get; }
}
