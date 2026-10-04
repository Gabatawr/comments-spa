using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CommentsApi.Infrastructure.Events;

namespace CommentsApi.Infrastructure.WebSockets;

/// <summary>
/// Minimal WebSocket hub at <c>/ws</c>. Tracks connected clients, answers ping with pong,
/// ignores malformed JSON and broadcasts <c>comment.created</c> messages on the event bus.
/// </summary>
public sealed class WebSocketHub
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly ILogger<WebSocketHub> _logger;

    public WebSocketHub(IEventBus eventBus, ILogger<WebSocketHub> logger)
    {
        _logger = logger;
        eventBus.Subscribe<CommentCreatedEvent>(OnCommentCreated);
    }

    public int ClientCount => _sockets.Count;

    private void OnCommentCreated(CommentCreatedEvent @event)
    {
        _ = BroadcastAsync(new { type = "comment.created", comment = @event.Comment });
    }

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
