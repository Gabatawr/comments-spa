using CommentsApi.Infrastructure.Events;

namespace CommentsApi.Infrastructure.Queue;

/// <summary>
/// Drains <see cref="CommentCreatedQueue"/> and publishes <see cref="CommentCreatedEvent"/>.
/// Runs detached from the HTTP request, so /api/comments returns 201 before cache invalidation,
/// logging and WebSocket broadcast happen.
/// </summary>
public sealed class CommentCreatedConsumer : BackgroundService
{
    private readonly CommentCreatedQueue _queue;
    private readonly IEventBus _eventBus;
    private readonly ILogger<CommentCreatedConsumer> _logger;

    public CommentCreatedConsumer(
        CommentCreatedQueue queue,
        IEventBus eventBus,
        ILogger<CommentCreatedConsumer> logger)
    {
        _queue = queue;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("CommentCreatedConsumer started");

        await foreach (var notification in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                _logger.LogInformation("Processing comment #{CommentId} from queue", notification.Comment.Id);
                _eventBus.Publish(new CommentCreatedEvent(notification.Comment));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process comment #{CommentId}", notification.Comment.Id);
            }
            finally
            {
                _queue.MarkProcessed();
            }
        }
    }
}
