using System.Text.Json;
using Comments.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Comments.Infrastructure.Caching;

/// <summary>Cache-key grammar from docs/API-v2.md §9.</summary>
public static class CacheKeys
{
    public const string Version = "comments:version";
    public const string StatsTotals = "stats:totals";

    public static string Page(string sortBy, string sortDir, int page, int pageSize, long version)
        => $"comments:page:{sortBy}:{sortDir}:{page}:{pageSize}:v{version}";

    public static string Item(int id) => $"comments:item:{id}";

    public static string Captcha(string captchaId) => $"captcha:{captchaId}";
}

/// <summary>IMemoryCache-backed cache port implementation (default in tests / no-Redis mode).</summary>
public sealed class MemoryCacheService : ICacheService, ICacheTelemetry
{
    private readonly IMemoryCache _cache;
    private long _hits;
    private long _misses;

    public MemoryCacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool IsAvailable => true;

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public double HitRate
    {
        get
        {
            var hits = Hits;
            var total = hits + Misses;
            return total == 0 ? 0d : (double)hits / total;
        }
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out T? value))
        {
            Interlocked.Increment(ref _hits);
            return Task.FromResult(value);
        }

        Interlocked.Increment(ref _misses);
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var options = new MemoryCacheEntryOptions();
        if (ttl is { } duration)
        {
            options.AbsoluteExpirationRelativeToNow = duration;
        }

        _cache.Set(key, value, options);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    public Task<long> IncrementAsync(string key, long value = 1, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var next = _cache.TryGetValue(key, out long current) ? current + value : value;
        var options = new MemoryCacheEntryOptions();
        if (ttl is { } duration)
        {
            options.AbsoluteExpirationRelativeToNow = duration;
        }

        _cache.Set(key, next, options);
        return Task.FromResult(next);
    }
}

/// <summary>Redis-backed cache. Values are stored as JSON strings.</summary>
public sealed class RedisBackedCacheService : ICacheService, ICacheTelemetry
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private long _hits;
    private long _misses;

    public RedisBackedCacheService(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return _redis.IsConnected;
            }
            catch
            {
                return false;
            }
        }
    }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public double HitRate
    {
        get
        {
            var hits = Hits;
            var total = hits + Misses;
            return total == 0 ? 0d : (double)hits / total;
        }
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var raw = await _redis.GetDatabase().StringGetAsync(key);
        if (raw.IsNullOrEmpty)
        {
            Interlocked.Increment(ref _misses);
            return default;
        }

        Interlocked.Increment(ref _hits);
        return JsonSerializer.Deserialize<T>((string)raw!, Json);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var serialized = JsonSerializer.Serialize(value, Json);
        if (ttl is { } duration)
        {
            await _redis.GetDatabase().StringSetAsync(key, serialized, duration);
        }
        else
        {
            await _redis.GetDatabase().StringSetAsync(key, serialized);
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().KeyDeleteAsync(key);

    public Task<long> IncrementAsync(string key, long value = 1, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => _redis.GetDatabase().StringIncrementAsync(key, value);
}

/// <summary>Primary (Redis) cache with an in-memory fallback so the API keeps working when Redis is down
/// (docs/ARCHITECTURE-v2.md §5).</summary>
public sealed class FallbackCacheService : ICacheService, ICacheTelemetry
{
    private readonly ICacheService _primary;
    private readonly MemoryCacheService _fallback;
    private long _hits;
    private long _misses;

    public FallbackCacheService(ICacheService primary, MemoryCacheService fallback)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public bool IsAvailable
    {
        get
        {
            try
            {
                return _primary.IsAvailable;
            }
            catch
            {
                return false;
            }
        }
    }

    public long Hits => Interlocked.Read(ref _hits);

    public long Misses => Interlocked.Read(ref _misses);

    public double HitRate
    {
        get
        {
            var hits = Hits;
            var total = hits + Misses;
            return total == 0 ? 0d : (double)hits / total;
        }
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await _primary.GetAsync<T>(key, cancellationToken);
            if (value is not null)
            {
                Interlocked.Increment(ref _hits);
                return value;
            }
        }
        catch
        {
            // fall through to memory
        }

        Interlocked.Increment(ref _misses);
        return await _fallback.GetAsync<T>(key, cancellationToken);
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await _primary.SetAsync(key, value, ttl, cancellationToken);
        }
        catch
        {
            // fall through to memory
        }

        await _fallback.SetAsync(key, value, ttl, cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _primary.RemoveAsync(key, cancellationToken);
        }
        catch
        {
            // fall through to memory
        }

        await _fallback.RemoveAsync(key, cancellationToken);
    }

    public async Task<long> IncrementAsync(string key, long value = 1, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        long result;
        try
        {
            result = await _primary.IncrementAsync(key, value, ttl, cancellationToken);
        }
        catch
        {
            result = await _fallback.IncrementAsync(key, value, ttl, cancellationToken);
        }

        await _fallback.SetAsync(key, result, ttl, cancellationToken);
        return result;
    }
}
