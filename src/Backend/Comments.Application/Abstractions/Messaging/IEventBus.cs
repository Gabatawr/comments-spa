using Comments.Application.Dtos;

namespace Comments.Application.Abstractions.Messaging;

/// <summary>Raised after a comment has been persisted (in-memory bus event, v1-compatible).</summary>
public sealed record CommentCreatedEvent(CommentDto Comment);

/// <summary>Payload of the <c>CommentCreated</c> integration event (docs/API-v2.md §7.3).</summary>
public sealed record CommentCreatedPayload(CommentDto Comment);

/// <summary>Payload of the <c>CommentIndexed</c> integration event (docs/API-v2.md §7.3).</summary>
public sealed record CommentIndexedPayload(int CommentId, DateTime IndexedAt);

/// <summary>Payload of the <c>CommentSearchIndexFailed</c> integration event (docs/API-v2.md §7.3).</summary>
public sealed record CommentSearchIndexFailedPayload(int CommentId, string Error);

/// <summary>
/// In-process event bus. Deliberately an interface so a broker (RabbitMQ/Kafka) can replace
/// the in-memory implementation without touching publishers/subscribers (Middle path).
/// </summary>
public interface IEventBus
{
    void Publish<TEvent>(TEvent @event)
        where TEvent : notnull;

    IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : notnull;
}

/// <summary>
/// Integration-event envelope (docs/API-v2.md §7.2). <see cref="Payload"/> is serialised as JSON;
/// the concrete shape depends on <see cref="EventType"/>.
/// </summary>
public sealed record DomainEventEnvelope
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>e.g. <c>CommentCreated</c>, <c>CommentIndexed</c>, <c>CommentSearchIndexFailed</c>.</summary>
    public string EventType { get; init; } = string.Empty;

    public int Version { get; init; } = 1;

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;

    public string Producer { get; init; } = "comments-api";

    /// <summary>Anonymous payload object; <c>{ comment: CommentDto }</c> for CommentCreated.</summary>
    public object? Payload { get; init; }
}

/// <summary>
/// Publishes integration events with publisher confirms (docs/API-v2.md §7.1). RabbitMQ
/// implementation plus an in-memory fallback when the broker is unavailable.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken = default);

    bool IsAvailable { get; }
}

/// <summary>
/// Background consumer of <c>comments.events.*</c>. Subscribers receive the decoded envelope;
/// <see cref="Pending"/>/<see cref="Processed"/> feed <c>GET /api/health</c> (docs/API-v2.md §2.1).
/// In broker mode this is the <b>per-instance</b> fan-out channel (e.g. the WebSocket broadcast
/// queue, docs/API-v2.md §7.1), so every replica sees every event.
/// </summary>
public interface IEventConsumer
{
    IDisposable Subscribe(Func<DomainEventEnvelope, CancellationToken, Task> handler);

    bool IsAvailable { get; }

    int Pending { get; }

    int Processed { get; }
}

/// <summary>
/// Work-queue variant of <see cref="IEventConsumer"/> (docs/API-v2.md §7.1): messages are shared
/// between replicas, so a subscriber runs <b>once per cluster</b>. Used by idempotent projections
/// such as Elasticsearch indexing and cache invalidation.
/// </summary>
public interface IWorkEventConsumer
{
    IDisposable Subscribe(Func<DomainEventEnvelope, CancellationToken, Task> handler);
}
