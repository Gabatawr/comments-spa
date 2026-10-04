using CommentsApi.Dtos;

namespace CommentsApi.Infrastructure.Events;

/// <summary>Raised after a comment has been persisted.</summary>
public sealed record CommentCreatedEvent(CommentDto Comment);

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
