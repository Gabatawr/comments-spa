using Comments.Application.Abstractions.Caching;

namespace Comments.Infrastructure.Caching;

/// <summary>
/// Placeholder primary used when <c>Providers:Cache=redis</c> but the server cannot be reached:
/// every operation fails so <see cref="FallbackCacheService"/> degrades to memory while
/// <see cref="IsAvailable"/> stays false, letting health report <c>cache=error</c>.
/// </summary>
public sealed class UnavailableCacheService : ICacheService
{
    public bool IsAvailable => false;

    private static InvalidOperationException Unavailable() => new("The configured cache provider is unavailable.");

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => throw Unavailable();

    public Task<long> IncrementAsync(string key, long value = 1, TimeSpan? ttl = null, CancellationToken cancellationToken = default) => throw Unavailable();
}
