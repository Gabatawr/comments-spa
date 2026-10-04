using Comments.Application.Abstractions.Caching;
using Microsoft.Extensions.Caching.Memory;
using StackExchange.Redis;

namespace Comments.Infrastructure.Captcha;

/// <summary>In-memory CAPTCHA store (default / fallback), one-time consume semantics.</summary>
public sealed class MemoryCaptchaStore : ICaptchaStore
{
    private readonly IMemoryCache _cache;

    public MemoryCaptchaStore(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool IsAvailable => true;

    internal static string Key(string captchaId) => $"captcha:{captchaId}";

    public void Set(string captchaId, string code, TimeSpan ttl)
        => _cache.Set(Key(captchaId), code, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl });

    public string? Get(string captchaId)
        => _cache.TryGetValue(Key(captchaId), out string? code) ? code : null;

    public string? Consume(string captchaId)
    {
        if (_cache.TryGetValue(Key(captchaId), out string? code))
        {
            _cache.Remove(Key(captchaId));
            return code;
        }

        return null;
    }
}

/// <summary>Redis-backed CAPTCHA store. Uses GETDEL for atomic one-time consumption.</summary>
public sealed class RedisBackedCaptchaStore : ICaptchaStore
{
    private readonly IConnectionMultiplexer _redis;

    public RedisBackedCaptchaStore(IConnectionMultiplexer redis)
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

    public void Set(string captchaId, string code, TimeSpan ttl)
        => _redis.GetDatabase().StringSet(MemoryCaptchaStore.Key(captchaId), code, ttl);

    public string? Get(string captchaId)
        => _redis.GetDatabase().StringGet(MemoryCaptchaStore.Key(captchaId));

    public string? Consume(string captchaId)
        => _redis.GetDatabase().StringGetDelete(MemoryCaptchaStore.Key(captchaId));
}

/// <summary>
/// Primary store with a memory fallback: any provider failure degrades instead of throwing
/// (docs/ARCHITECTURE-v2.md §5).
/// </summary>
public sealed class FallbackCaptchaStore : ICaptchaStore
{
    private readonly ICaptchaStore _primary;
    private readonly ICaptchaStore _fallback;

    public FallbackCaptchaStore(ICaptchaStore primary, ICaptchaStore fallback)
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

    public void Set(string captchaId, string code, TimeSpan ttl)
    {
        try
        {
            _primary.Set(captchaId, code, ttl);
        }
        catch
        {
            _fallback.Set(captchaId, code, ttl);
        }
    }

    public string? Get(string captchaId)
    {
        try
        {
            return _primary.Get(captchaId) ?? _fallback.Get(captchaId);
        }
        catch
        {
            return _fallback.Get(captchaId);
        }
    }

    public string? Consume(string captchaId)
    {
        try
        {
            var value = _primary.Consume(captchaId);
            if (value is not null)
            {
                return value;
            }
        }
        catch
        {
            // fall through to the memory copy
        }

        return _fallback.Consume(captchaId);
    }
}
