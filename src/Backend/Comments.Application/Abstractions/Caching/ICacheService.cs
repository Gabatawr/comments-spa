namespace Comments.Application.Abstractions.Caching;

/// <summary>
/// Key/value cache port (docs/ARCHITECTURE-v2.md §3, docs/API-v2.md §9).
/// Redis and in-memory implementations live in Infrastructure.
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);

    Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Atomic increment, used for the page-cache generation version and stats.</summary>
    Task<long> IncrementAsync(string key, long value = 1, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>True when the backing provider is reachable ("ok"); false -> health "error".</summary>
    bool IsAvailable { get; }
}
