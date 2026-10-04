using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Comments.Application.Abstractions.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Comments.Infrastructure.Messaging;

/// <summary>
/// Thread-safe in-memory event bus. Handler exceptions are isolated and logged so one bad
/// subscriber can never break the publisher (the HTTP request).
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();
    private readonly ILogger<InMemoryEventBus> _logger;

    public InMemoryEventBus(ILogger<InMemoryEventBus> logger)
    {
        _logger = logger;
    }

    public void Publish<TEvent>(TEvent @event)
        where TEvent : notnull
    {
        if (!_handlers.TryGetValue(typeof(TEvent), out var handlers))
        {
            return;
        }

        object[] snapshot;
        lock (handlers)
        {
            snapshot = handlers.ToArray();
        }

        foreach (var handler in snapshot)
        {
            try
            {
                ((Action<TEvent>)handler)(@event);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler for {EventType} failed", typeof(TEvent).Name);
            }
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : notnull
    {
        var handlers = _handlers.GetOrAdd(typeof(TEvent), _ => new List<object>());
        lock (handlers)
        {
            handlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (handlers)
            {
                handlers.Remove(handler);
            }
        });
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>
/// In-memory fallback for the broker (docs/ARCHITECTURE-v2.md §5): <see cref="IEventPublisher"/>
/// enqueues, a background pump dispatches to <see cref="IEventConsumer"/> subscribers and keeps
/// the health counters real (pending = queued, processed = dispatched).
/// </summary>
public sealed class InMemoryEventConsumer : BackgroundService, IEventConsumer, IEventPublisher, IWorkEventConsumer
{
    private readonly Channel<DomainEventEnvelope> _channel =
        Channel.CreateUnbounded<DomainEventEnvelope>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ConcurrentDictionary<Guid, Func<DomainEventEnvelope, CancellationToken, Task>> _subscribers = new();
    private readonly ILogger<InMemoryEventConsumer> _logger;
    private int _pending;
    private int _processed;

    public InMemoryEventConsumer(ILogger<InMemoryEventConsumer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The in-memory consumer is a degradation fallback, not a broker: report unavailable so
    /// <c>health.broker</c> turns to "error" when Messaging:Provider=rabbitmq but the broker
    /// could not be reached (docs/ARCHITECTURE-v2.md §5).
    /// </summary>
    public bool IsAvailable => false;

    public int Pending => Math.Max(0, Volatile.Read(ref _pending));

    public int Processed => Volatile.Read(ref _processed);

    public IDisposable Subscribe(Func<DomainEventEnvelope, CancellationToken, Task> handler)
    {
        var key = Guid.NewGuid();
        _subscribers[key] = handler;
        return new Subscription(() => _subscribers.TryRemove(key, out _));
    }

    public Task PublishAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _pending);
        if (_channel.Writer.TryWrite(envelope))
        {
            return Task.CompletedTask;
        }

        Interlocked.Decrement(ref _pending);
        throw new InvalidOperationException("The in-memory event channel rejected the message.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InMemoryEventConsumer started");
        await foreach (var envelope in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            foreach (var subscriber in _subscribers.Values)
            {
                try
                {
                    await subscriber(envelope, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "In-memory subscriber failed for {EventType}", envelope.EventType);
                }
            }

            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _processed);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Action? _dispose;

        public Subscription(Action dispose) => _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
