namespace Comments.Infrastructure.Messaging;

/// <summary>RabbitMQ topology settings (docs/API-v2.md §7.1, §10).</summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string ConnectionString { get; set; } = "amqp://guest:guest@rabbitmq:5672/";

    public string Exchange { get; set; } = "comments.events";

    public string DeadLetterExchange { get; set; } = "comments.dlx";

    public string RetryQueue { get; set; } = "comments.events.retry";

    /// <summary>Routing key for the CommentCreated event.</summary>
    public string CreatedRoutingKey { get; set; } = "comment.created";

    public int RetryDelaySeconds { get; set; } = 5;
}
