using Comments.Application.Abstractions.Messaging;
using Comments.Application.Abstractions.Search;
using Comments.Application.Dtos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Comments.Infrastructure.Search;

/// <summary>
/// Elasticsearch side effect of <c>CommentCreated</c> (docs/API-v2.md §8.3), bound to the
/// <see cref="EventQueues.Search"/> work queue: indexing runs once per cluster, and its retries are
/// independent of cache invalidation (a search outage must not make every page stale).
///
/// Publishes <c>CommentIndexed</c> / <c>CommentSearchIndexFailed</c> so the indexing outcome is
/// observable in the log/topology instead of being swallowed by a background task.
/// </summary>
public sealed class SearchIndexProjection : IHostedService
{
    private readonly IWorkEventConsumer _workConsumer;
    private readonly ICommentSearchIndex _search;
    private readonly IEventPublisher _publisher;
    private readonly ILogger<SearchIndexProjection> _logger;
    private IDisposable? _subscription;

    public SearchIndexProjection(
        IWorkEventConsumer workConsumer,
        ICommentSearchIndex search,
        IEventPublisher publisher,
        ILogger<SearchIndexProjection> logger)
    {
        _workConsumer = workConsumer;
        _search = search;
        _publisher = publisher;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _workConsumer.Subscribe(EventQueues.Search, HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    private async Task HandleAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!envelope.IsCommentCreated())
        {
            return;
        }

        CommentDto? comment = envelope.Payload.AsComment();
        if (comment is null)
        {
            return;
        }

        if (!_search.IsEnabled)
        {
            return;
        }

        try
        {
            await _search.IndexAsync(comment, cancellationToken);
            await _publisher.PublishAsync(
                new DomainEventEnvelope
                {
                    EventType = "CommentIndexed",
                    Producer = "comments-api",
                    Payload = new CommentIndexedPayload(comment.Id, DateTime.UtcNow),
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Elasticsearch indexing failed for comment #{CommentId}", comment.Id);

            await _publisher.PublishAsync(
                new DomainEventEnvelope
                {
                    EventType = "CommentSearchIndexFailed",
                    Producer = "comments-api",
                    Payload = new CommentSearchIndexFailedPayload(comment.Id, ex.Message),
                },
                CancellationToken.None);
        }
    }
}
