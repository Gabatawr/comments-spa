using CommentsApi.Dtos;
using CommentsApi.Infrastructure.Events;
using Microsoft.Extensions.Caching.Memory;

namespace CommentsApi.Infrastructure.Cache;

/// <summary>
/// IMemoryCache-backed page cache for <c>GET /api/comments</c>.
/// Invalidation is a lock-free generation bump: new keys carry the new version, stale entries
/// simply expire (30 s sliding window), so no prefix scan is needed.
/// </summary>
public sealed class CommentCache
{
    private static readonly TimeSpan PageTtl = TimeSpan.FromSeconds(30);

    private readonly IMemoryCache _cache;
    private readonly ILogger<CommentCache> _logger;
    private int _version;

    public CommentCache(IMemoryCache cache, IEventBus eventBus, ILogger<CommentCache> logger)
    {
        _cache = cache;
        _logger = logger;
        eventBus.Subscribe<CommentCreatedEvent>(_ => Invalidate());
    }

    public int Version => Volatile.Read(ref _version);

    public string BuildKey(int page, int pageSize, string sortBy, string sortDir) =>
        $"comments:v{Volatile.Read(ref _version)}:{page}:{pageSize}:{sortBy}:{sortDir}";

    public bool TryGet(string key, out CommentPageDto? value) => _cache.TryGetValue(key, out value);

    public void Set(string key, CommentPageDto value) => _cache.Set(key, value, PageTtl);

    public void Invalidate()
    {
        var version = Interlocked.Increment(ref _version);
        _logger.LogInformation("Comment page cache invalidated (version {Version})", version);
    }
}
