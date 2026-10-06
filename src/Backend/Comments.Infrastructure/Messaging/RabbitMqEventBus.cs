using System.Collections;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Comments.Application.Abstractions.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Comments.Infrastructure.Messaging;

/// <summary>
/// RabbitMQ integration-event adapter (docs/API-v2.md §7).
///
/// Topology: durable topic exchange <c>comments.events</c>, durable dead-letter exchange
/// <c>comments.dlx</c>, shared durable work-queues <c>comments.events.search</c>/<c>.cache</c>
/// (with DLQ + retry TTL), and a <b>per-instance</b> exclusive auto-delete queue
/// <c>comments.events.ws.&lt;instanceId&gt;</c> for WebSocket fan-out.
///
/// <see cref="IEventConsumer"/> subscribers get the per-instance fan-out; <see cref="IWorkEventConsumer"/>
/// subscribers get the shared work-queues, so indexing/cache invalidation run once per cluster.
/// Publishing is persistent JSON with publisher confirms (messageId = eventId).
/// </summary>
public sealed class RabbitMqEventBus : BackgroundService, IEventPublisher, IEventConsumer, IWorkEventConsumer
{
    /// <summary>Shared work queues, one per side effect — see <see cref="EventQueues"/>.</summary>
    private static readonly IReadOnlyList<string> WorkQueues = EventQueues.All;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnection _connection;
    private readonly RabbitMqOptions _options;
    private readonly ILogger<RabbitMqEventBus> _logger;
    private readonly string _wsQueue = $"comments.events.ws.{Guid.NewGuid():N}";

    private readonly ConcurrentDictionary<Guid, Func<DomainEventEnvelope, CancellationToken, Task>> _broadcastSubscribers = new();
    /// <summary>
    /// Work subscribers, grouped by queue. Each work queue gets its own copy of an event, so a
    /// handler must only see deliveries from the queue it subscribed to.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Func<DomainEventEnvelope, CancellationToken, Task>>> _workSubscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _seen = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IChannel? _publishChannel;
    private int _pending;
    private int _processed;

    public RabbitMqEventBus(IConnection connection, IOptions<RabbitMqOptions> options, ILogger<RabbitMqEventBus> logger)
    {
        _connection = connection;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsAvailable => _connection.IsOpen;

    public int Pending => Math.Max(0, Volatile.Read(ref _pending));

    public int Processed => Volatile.Read(ref _processed);

    public IDisposable Subscribe(Func<DomainEventEnvelope, CancellationToken, Task> handler)
    {
        var key = Guid.NewGuid();
        _broadcastSubscribers[key] = handler;
        return new Subscription(() => _broadcastSubscribers.TryRemove(key, out _));
    }

    /// <summary>
    /// Subscribes to one work queue. The queue is part of the subscription: every work queue holds
    /// its own copy of an event, so a handler must only see the queue it asked for.
    /// </summary>
    public IDisposable Subscribe(string queue, Func<DomainEventEnvelope, CancellationToken, Task> handler)
    {
        if (!EventQueues.All.Contains(queue, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(queue),
                queue,
                $"Unknown work queue. Known queues: {string.Join(", ", EventQueues.All)}.");
        }

        var subscribers = _workSubscribers.GetOrAdd(
            queue,
            _ => new ConcurrentDictionary<Guid, Func<DomainEventEnvelope, CancellationToken, Task>>());

        var key = Guid.NewGuid();
        subscribers[key] = handler;
        return new Subscription(() => subscribers.TryRemove(key, out _));
    }

    public async Task PublishAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        // Bounded wait so a broker that is still (re)connecting degrades quickly instead of
        // hanging the HTTP request; the caller already treats a publish failure as non-fatal.
        await _ready.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        var channel = _publishChannel ?? throw new InvalidOperationException("RabbitMQ publish channel is not ready.");

        var body = JsonSerializer.SerializeToUtf8Bytes(envelope, Json);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            MessageId = envelope.EventId,
            DeliveryMode = DeliveryModes.Persistent,
            Type = envelope.EventType,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        Interlocked.Increment(ref _pending);

        // With publisherConfirmationsEnabled && tracking enabled, this completes only after the
        // broker acks (and throws on nack) — that is our publisher confirm (docs/API-v2.md §7.1).
        await channel.BasicPublishAsync(
            exchange: _options.Exchange,
            routingKey: RoutingKey(envelope.EventType),
            mandatory: false,
            basicProperties: properties,
            body: body,
            cancellationToken: cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await StartAsyncCore(stoppingToken);
                _ready.TrySetResult();
                await Task.Delay(Timeout.Infinite, stoppingToken);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                attempt++;
                _logger.LogError(ex, "RabbitMQ event bus start attempt {Attempt} failed; retrying in 5s", attempt);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task StartAsyncCore(CancellationToken stoppingToken)
    {
        var confirmOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        var consumeOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: false,
            publisherConfirmationTrackingEnabled: false,
            consumerDispatchConcurrency: null);

        _publishChannel = await _connection.CreateChannelAsync(confirmOptions, stoppingToken);
        await DeclareTopologyAsync(_publishChannel, stoppingToken);

        // Per-instance fan-out queue (WebSocket broadcast on every replica).
        var wsChannel = await _connection.CreateChannelAsync(consumeOptions, stoppingToken);
        await wsChannel.BasicQosAsync(prefetchSize: 0, prefetchCount: 32, global: false, stoppingToken);
        var wsConsumer = new AsyncEventingBasicConsumer(wsChannel);
        wsConsumer.ReceivedAsync += (_, args) => HandleBroadcastAsync(wsChannel, args, stoppingToken);
        await wsChannel.BasicConsumeAsync(_wsQueue, autoAck: false, consumer: wsConsumer, cancellationToken: stoppingToken);
        _logger.LogInformation("RabbitMQ WS fan-out consumer subscribed to {Queue}", _wsQueue);

        // Shared work-queues (search/cache): one replica handles each message.
        foreach (var queue in WorkQueues)
        {
            var channel = await _connection.CreateChannelAsync(consumeOptions, stoppingToken);
            await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 8, global: false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, args) => HandleWorkAsync(channel, queue, args, stoppingToken);
            await channel.BasicConsumeAsync(queue, autoAck: false, consumer: consumer, cancellationToken: stoppingToken);
            _logger.LogInformation("RabbitMQ work consumer subscribed to {Queue}", queue);
        }
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: _options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchange: _options.DeadLetterExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        // Retry queue: dead-lettered messages wait 5 s, then return to comments.events.
        var retryArgs = new Dictionary<string, object?>
        {
            ["x-message-ttl"] = _options.RetryDelaySeconds * 1000,
            ["x-dead-letter-exchange"] = _options.Exchange,
            ["x-dead-letter-routing-key"] = _options.CreatedRoutingKey,
        };
        await EnsureQueueAsync(channel, _options.RetryQueue, durable: true, arguments: retryArgs, cancellationToken);

        foreach (var queue in WorkQueues)
        {
            var args = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = _options.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = queue + ".retry",
            };

            await EnsureQueueAsync(channel, queue, durable: true, arguments: args, cancellationToken);
            await channel.QueueBindAsync(queue, _options.Exchange, _options.CreatedRoutingKey, arguments: null, cancellationToken: cancellationToken);
            await channel.QueueBindAsync(_options.RetryQueue, _options.DeadLetterExchange, queue + ".retry", arguments: null, cancellationToken: cancellationToken);

            var dlq = queue + ".dlq";
            await EnsureQueueAsync(channel, dlq, durable: true, arguments: null, cancellationToken);
            await channel.QueueBindAsync(dlq, _options.DeadLetterExchange, queue + ".dead", arguments: null, cancellationToken: cancellationToken);
        }

        // Per-instance WebSocket queue: exclusive + auto-delete, bound to comment.created.
        await channel.QueueDeclareAsync(
            _wsQueue,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(_wsQueue, _options.Exchange, _options.CreatedRoutingKey, arguments: null, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Declares a queue only when it does not already exist. Passive existence probing avoids the
    /// AMQP 406 <c>PRECONDITION_FAILED</c> storm (and a closed channel) during rolling upgrades when
    /// an older instance already declared the queue with different arguments.
    /// </summary>
    private async Task EnsureQueueAsync(
        IChannel channel,
        string queue,
        bool durable,
        IDictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        if (await QueueExistsAsync(queue, cancellationToken))
        {
            _logger.LogDebug("Queue {Queue} already exists; keeping its existing arguments", queue);
            return;
        }

        await channel.QueueDeclareAsync(
            queue,
            durable: durable,
            exclusive: false,
            autoDelete: false,
            arguments: arguments,
            cancellationToken: cancellationToken);
    }

    private async Task<bool> QueueExistsAsync(string queue, CancellationToken cancellationToken)
    {
        var probe = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);
        try
        {
            await probe.QueueDeclareAsync(
                queue,
                durable: false,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                passive: true,
                cancellationToken: cancellationToken);
            return true;
        }
        catch (OperationInterruptedException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Passive queue probe for {Queue} failed; declaring it", queue);
            return false;
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }

    private async Task HandleBroadcastAsync(IChannel channel, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = Deserialize(args);
            if (envelope is null)
            {
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                return;
            }

            // The work-queues also feed this instance; dispatch to broadcast subscribers at most once.
            if (_seen.TryAdd(envelope.EventId, DateTime.UtcNow))
            {
                foreach (var subscriber in _broadcastSubscribers.Values)
                {
                    await subscriber(envelope, cancellationToken);
                }

                Interlocked.Decrement(ref _pending);
                Interlocked.Increment(ref _processed);
                PruneSeen();
            }

            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket broadcast handler failed for a live message; dropping");
            await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken);
        }
    }

    private async Task HandleWorkAsync(IChannel channel, string queue, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = Deserialize(args);
            if (envelope is null)
            {
                await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken);
                return;
            }

            // Only this queue's subscribers: the other work queues receive their own copy of the
            // same event and are served by their own channels (docs/API-v2.md §7.1).
            if (_workSubscribers.TryGetValue(queue, out var subscribers))
            {
                foreach (var subscriber in subscribers.Values)
                {
                    await subscriber(envelope, cancellationToken);
                }
            }

            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _processed);
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Work handler for {Queue} failed; retry/DLQ", queue);
            await DeadLetterOrRetryAsync(channel, queue, args, cancellationToken);
        }
    }

    /// <summary>After the 3rd delivery a failing work message goes to its DLQ; before that it is retried via TTL.</summary>
    private async Task DeadLetterOrRetryAsync(IChannel channel, string queue, BasicDeliverEventArgs args, CancellationToken cancellationToken)
    {
        var deaths = DeathCount(args, queue);
        if (deaths >= 2 && _publishChannel is not null)
        {
            await _publishChannel.BasicPublishAsync(
                exchange: _options.DeadLetterExchange,
                routingKey: queue + ".dead",
                mandatory: false,
                basicProperties: new BasicProperties { DeliveryMode = DeliveryModes.Persistent },
                body: args.Body,
                cancellationToken: cancellationToken);
            await channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken);
            return;
        }

        // requeue:false -> queue's DLX -> comments.events.retry (TTL 5 s) -> comments.events.
        await channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken);
    }

    private static DomainEventEnvelope? Deserialize(BasicDeliverEventArgs args)
    {
        var json = Encoding.UTF8.GetString(args.Body.Span);
        return JsonSerializer.Deserialize<DomainEventEnvelope>(json, Json);
    }

    private static int DeathCount(BasicDeliverEventArgs args, string queue)
    {
        if (args.BasicProperties.Headers is null
            || !args.BasicProperties.Headers.TryGetValue("x-death", out var raw)
            || raw is not IEnumerable entries)
        {
            return 0;
        }

        var count = 0;
        foreach (var entry in entries)
        {
            if (entry is IDictionary dictionary
                && dictionary["queue"]?.ToString() == queue
                && dictionary["count"] is { } value)
            {
                count += Convert.ToInt32(value);
            }
        }

        return count;
    }

    private void PruneSeen()
    {
        if (_seen.Count < 10000)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        foreach (var pair in _seen)
        {
            if (pair.Value < cutoff)
            {
                _seen.TryRemove(pair.Key, out _);
            }
        }
    }

    private string RoutingKey(string eventType) => eventType switch
    {
        "CommentCreated" => _options.CreatedRoutingKey,
        "CommentIndexed" => "comment.indexed",
        "CommentSearchIndexFailed" => "comment.search-index-failed",
        _ => eventType,
    };

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose) => _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
