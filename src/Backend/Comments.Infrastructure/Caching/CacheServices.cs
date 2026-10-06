using System.Text.Json;
using Comments.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Comments.Infrastructure.Caching;

/// <summary>
/// Hit/miss accounting shared by every cache adapter (docs/API-v2.md §3.5). It used to be copied
/// into each of them verbatim, which is exactly how the fallback wrapper ended up counting a
/// fallback hit as a miss — there was no single place where "hit" was defined.
/// </summary>
public abstract class CacheTelemetry : ICacheTelemetry
{
    private long _hits;
    private long _misses;

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

    /// <summary>A value was served from cache — from the primary provider or from a fallback tier.</summary>
    protected void RecordHit() => Interlocked.Increment(ref _hits);

    protected void RecordMiss() => Interlocked.Increment(ref _misses);
}

/// <summary>IMemoryCache-backed cache port implementation (default in tests / no-Redis mode).</summary>
public sealed class MemoryCacheService : CacheTelemetry, ICacheService
{
    private readonly IMemoryCache _cache;

    public MemoryCacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool IsAvailable => true;

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(key, out T? value))
        {
            RecordHit();
            return Task.FromResult(value);
        }

        RecordMiss();
        return Task.FromResult<T?>(default);
    }

    public Task SetAsync<T>(string key, T value, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _cache.Set(key, value, EntryOptions(ttl));
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
        _cache.Set(key, next, EntryOptions(ttl));
        return Task.FromResult(next);
    }

    private static MemoryCacheEntryOptions EntryOptions(TimeSpan? ttl)
    {
        var options = new MemoryCacheEntryOptions();
        if (ttl is { } duration)
        {
            options.AbsoluteExpirationRelativeToNow = duration;
        }

        return options;
    }
}

/// <summary>Redis-backed cache. Values are stored as JSON strings.</summary>
public sealed class RedisBackedCacheService : CacheTelemetry, ICacheService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;

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

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var raw = await _redis.GetDatabase().StringGetAsync(key);
        if (raw.IsNullOrEmpty)
        {
            RecordMiss();
            return default;
        }

        RecordHit();
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

/// <summary>
/// Primary (Redis) cache with an in-memory fallback so the API keeps working when Redis is down
/// (docs/ARCHITECTURE-v2.md §5). This is the one cache implementation where a fallback tier exists,
/// so it is also the one that has to decide how a tier below the primary is accounted for.
/// </summary>
public sealed class FallbackCacheService : CacheTelemetry, ICacheService
{
    private readonly ICacheService _primary;
    private readonly MemoryCacheService _fallback;

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

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        T? value = default;
        try
        {
            value = await _primary.GetAsync<T>(key, cancellationToken);
        }
        catch
        {
            // The primary tier is unavailable; the memory tier below is why this wrapper exists.
        }

        if (value is not null)
        {
            RecordHit();
            return value;
        }

        value = await _fallback.GetAsync<T>(key, cancellationToken);
        if (value is not null)
        {
            // A fallback hit is still a hit. Counting it as a miss understated cacheHitRate exactly
            // when the system was degraded and the number mattered most (docs/ARCHITECTURE-v2.md §5).
            RecordHit();
            return value;
        }

        RecordMiss();
        return default;
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

        // The counters must agree between the tiers: a page key built from the memory tier and one
        // built from Redis would otherwise diverge and serve a page from the wrong generation.
        await _fallback.SetAsync(key, result, ttl, cancellationToken);
        return result;
    }
}
