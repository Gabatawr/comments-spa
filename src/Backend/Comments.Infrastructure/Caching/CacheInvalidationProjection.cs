using Comments.Application.Abstractions.Caching;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Comments.Infrastructure.Caching;

/// <summary>
/// Cache side effect of <c>CommentCreated</c> (docs/API-v2.md §9), bound to the
/// <see cref="EventQueues.Cache"/> work queue so the invalidation runs <b>once per cluster</b>
/// rather than once per replica.
///
/// The create pipeline already invalidates synchronously for the instance that served the POST;
/// this projection is the other half — it is what makes the other replicas drop their cached pages,
/// and it keeps working for writes that never went through the HTTP pipeline at all.
/// </summary>
public sealed class CacheInvalidationProjection : IHostedService
{
    private readonly IWorkEventConsumer _workConsumer;
    private readonly ICacheService _cache;
    private readonly ILogger<CacheInvalidationProjection> _logger;
    private IDisposable? _subscription;

    public CacheInvalidationProjection(
        IWorkEventConsumer workConsumer,
        ICacheService cache,
        ILogger<CacheInvalidationProjection> logger)
    {
        _workConsumer = workConsumer;
        _cache = cache;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _workConsumer.Subscribe(EventQueues.Cache, HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.IsCommentCreated() || envelope.Payload.AsComment() is null)
        {
            return;
        }

        var failure = await CommentCacheInvalidation.AfterWriteAsync(_cache, cancellationToken);
        if (failure is not null)
        {
            _logger.LogDebug(failure, "Cross-instance cache invalidation failed for {EventId}", envelope.EventId);
        }
    }
}
