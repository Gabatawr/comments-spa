using System.Text.Json;
using Comments.Application.Abstractions.Caching;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Abstractions.Search;
using Comments.Application.Dtos;
using Comments.Infrastructure.Caching;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Comments.Infrastructure.Search;

/// <summary>
/// Owns the adapter-side effects of <c>CommentCreated</c>: bumps the cross-instance cache
/// generation (docs/API-v2.md §9), indexes the comment into Elasticsearch (§8.3) and publishes
/// <c>CommentIndexed</c> / <c>CommentSearchIndexFailed</c> (§7.3). Subscribes to the shared
/// <see cref="IEventConsumer"/>, so it works for both the broker and the in-memory fallback.
/// </summary>
public sealed class CommentCreatedProjection : IHostedService
{
    private readonly IWorkEventConsumer _workConsumer;
    private readonly ICommentSearchIndex _search;
    private readonly ICacheService _cache;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<CommentCreatedProjection> _logger;
    private IDisposable? _subscription;

    public CommentCreatedProjection(
        IWorkEventConsumer workConsumer,
        ICommentSearchIndex search,
        ICacheService cache,
        IEventPublisher publisher,
        ILogger<CommentCreatedProjection> logger)
    {
        _workConsumer = workConsumer;
        _search = search;
        _cache = cache;
        _publisher = publisher;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _workConsumer.Subscribe(HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!string.Equals(envelope.EventType, "CommentCreated", StringComparison.Ordinal))
        {
            return;
        }

        var comment = ExtractComment(envelope.Payload);
        if (comment is null)
        {
            return;
        }

        // Generation bump keeps multi-instance page caches coherent (docs/API-v2.md §9).
        try
        {
            await _cache.IncrementAsync(CacheKeys.Version, 1, ttl: null, cancellationToken);
            await _cache.RemoveAsync(CacheKeys.Item(comment.Id), cancellationToken);
            await _cache.RemoveAsync(CacheKeys.StatsTotals, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cache invalidation failed for comment #{CommentId}", comment.Id);
        }

        if (!_search.IsEnabled)
        {
            return;
        }

        try
        {
            await _search.IndexAsync(comment, cancellationToken);
            await _publisher.PublishAsync(new DomainEventEnvelope
            {
                EventType = "CommentIndexed",
                Producer = "comments-api",
                Payload = new CommentIndexedPayload(comment.Id, DateTime.UtcNow),
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elasticsearch indexing failed for comment #{CommentId}", comment.Id);
            await _publisher.PublishAsync(new DomainEventEnvelope
            {
                EventType = "CommentSearchIndexFailed",
                Producer = "comments-api",
                Payload = new CommentSearchIndexFailedPayload(comment.Id, ex.Message),
            }, CancellationToken.None);
        }
    }

    private static CommentDto? ExtractComment(object? payload)
    {
        switch (payload)
        {
            case null:
                return null;
            case CommentCreatedPayload created:
                return created.Comment;
            case CommentDto dto:
                return dto;
            case JsonElement element:
                if (element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("comment", out var commentElement))
                {
                    return commentElement.Deserialize<CommentDto>(JsonOptions);
                }

                return element.Deserialize<CommentDto>(JsonOptions);
            default:
                return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
