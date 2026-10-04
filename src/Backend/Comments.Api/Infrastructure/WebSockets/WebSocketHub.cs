using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Comments.Application.Abstractions.Messaging;
using Comments.Application.Dtos;

namespace Comments.Api.Infrastructure.WebSockets;

/// <summary>
/// WebSocket hub at <c>/ws</c> (docs/API-v2.md §2.9).
///
/// Tracks connected clients, answers <c>ping</c> with <c>pong</c>, ignores malformed JSON and
/// broadcasts <c>comment.created</c> messages. Broadcasts are fed by the <see cref="IEventConsumer"/>
/// port — i.e. by the RabbitMQ consumer in broker mode — so every API instance relays the comments
/// created by the others, not just its own. When no consumer is registered (pure in-process /
/// inmemory mode without a broker adapter) it falls back to the local <see cref="IEventBus"/>.
/// </summary>
public sealed class WebSocketHub : IDisposable
{
    private const string CommentCreatedEventType = "CommentCreated";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();

    /// <summary>Last broadcast timestamp per comment id, used to collapse duplicate deliveries.</summary>
    private readonly ConcurrentDictionary<int, long> _recentlyBroadcast = new();

    private readonly ILogger<WebSocketHub> _logger;
    private readonly IDisposable? _consumerSubscription;
    private readonly IDisposable? _busSubscription;

    public WebSocketHub(IServiceProvider services, ILogger<WebSocketHub> logger)
    {
        _logger = logger;

        // Prefer the broker-driven consumer (multi-instance fan-out). Fall back to the in-process
        // bus only when no consumer adapter is registered (unit-test / memory-only runs).
        IEventConsumer? consumer = null;
        try
        {
            consumer = services.GetService(typeof(IEventConsumer)) as IEventConsumer;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IEventConsumer is not resolvable from the root provider; falling back to IEventBus");
        }

        if (consumer is not null)
        {
            _consumerSubscription = consumer.Subscribe(OnEnvelopeAsync);
            _logger.LogInformation("WebSocketHub subscribed to the message-broker consumer");
        }
        else
        {
            var bus = services.GetService(typeof(IEventBus)) as IEventBus;
            _busSubscription = bus?.Subscribe<CommentCreatedEvent>(OnCommentCreatedEvent);
            _logger.LogInformation("WebSocketHub subscribed to the in-process event bus (no broker consumer registered)");
        }
    }

    public int ClientCount => _sockets.Count;

    // ------------------------------------------------------------- subscriptions

    private Task OnEnvelopeAsync(DomainEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!string.Equals(envelope.EventType, CommentCreatedEventType, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        var comment = ExtractComment(envelope.Payload);
        if (comment is null)
        {
            _logger.LogWarning("Received a {EventType} envelope without a readable comment payload", envelope.EventType);
            return Task.CompletedTask;
        }

        BroadcastCommentCreated(comment, envelope.EventId);
        return Task.CompletedTask;
    }

    private void OnCommentCreatedEvent(CommentCreatedEvent @event)
        => BroadcastCommentCreated(@event.Comment, eventId: null);

    private void BroadcastCommentCreated(CommentDto comment, string? eventId)
    {
        if (IsDuplicate(comment.Id))
        {
            return;
        }

        // Fire-and-forget: the publisher (broker consumer) must not wait for slow sockets.
        _ = BroadcastAsync(new { type = "comment.created", eventId, comment });
    }

    private bool IsDuplicate(int commentId)
    {
        var now = Environment.TickCount64;
        if (_recentlyBroadcast.TryGetValue(commentId, out var last) && now - last < 10_000)
        {
            return true;
        }

        _recentlyBroadcast[commentId] = now;

        // Opportunistic pruning so the dictionary cannot grow unbounded on a busy instance.
        if (_recentlyBroadcast.Count > 10_000)
        {
            foreach (var (id, timestamp) in _recentlyBroadcast)
            {
                if (now - timestamp >= 10_000)
                {
                    _recentlyBroadcast.TryRemove(id, out _);
                }
            }
        }

        return false;
    }

    private static CommentDto? ExtractComment(object? payload)
    {
        switch (payload)
        {
            case null:
                return null;
            case CommentCreatedEvent created:
                return created.Comment;
            case CommentDto dto:
                return dto;
        }

        try
        {
            JsonElement element = payload is JsonElement jsonElement
                ? jsonElement
                : JsonSerializer.SerializeToElement(payload, JsonOptions);

            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("comment", out var inner)
                && inner.ValueKind == JsonValueKind.Object)
            {
                element = inner;
            }

            return element.Deserialize<CommentDto>(JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- connection

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket connection expected.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid();
        _sockets[clientId] = socket;
        _logger.LogInformation("WebSocket client {ClientId} connected ({Clients} total)", clientId, _sockets.Count);

        try
        {
            await SendAsync(socket, new { type = "hello", message = "connected", clients = _sockets.Count });

            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var received = await ReceiveFullMessageAsync(socket, buffer, context.RequestAborted);
                if (received is null)
                {
                    break;
                }

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (received.MessageType != WebSocketMessageType.Text)
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(received.Text!);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("type", out var type)
                        && type.ValueKind == JsonValueKind.String
                        && string.Equals(type.GetString(), "ping", StringComparison.OrdinalIgnoreCase))
                    {
                        await SendAsync(socket, new { type = "pong" });
                    }
                }
                catch (JsonException)
                {
                    // Invalid JSON is ignored on purpose; the connection stays open.
                    _logger.LogDebug("Ignoring malformed WebSocket message from {ClientId}", clientId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected.
        }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "WebSocket client {ClientId} error", clientId);
        }
        finally
        {
            _sockets.TryRemove(clientId, out _);
            _logger.LogInformation("WebSocket client {ClientId} disconnected ({Clients} left)", clientId, _sockets.Count);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // Already gone.
                }
            }
        }
    }

    public async Task BroadcastAsync(object payload)
    {
        if (_sockets.IsEmpty)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        foreach (var (clientId, socket) in _sockets)
        {
            if (socket.State != WebSocketState.Open)
            {
                _sockets.TryRemove(clientId, out _);
                continue;
            }

            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to broadcast to WebSocket client {ClientId}", clientId);
                _sockets.TryRemove(clientId, out _);
            }
        }
    }

    public void Dispose()
    {
        _consumerSubscription?.Dispose();
        _busSubscription?.Dispose();
    }

    private static Task SendAsync(WebSocket socket, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        return socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static async Task<ReceivedMessage?> ReceiveFullMessageAsync(
        WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new ReceivedMessage(WebSocketMessageType.Close, null);
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return new ReceivedMessage(result.MessageType, Encoding.UTF8.GetString(stream.ToArray()));
    }

    private sealed record ReceivedMessage(WebSocketMessageType MessageType, string? Text);
}
