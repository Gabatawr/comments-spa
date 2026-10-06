using System.Text.Json;
using Comments.Application.Dtos;

namespace Comments.Application.Abstractions.Messaging;

/// <summary>
/// Reads a <see cref="CommentDto"/> out of an event payload.
///
/// <see cref="DomainEventEnvelope.Payload"/> is deliberately untyped: the same envelope is handed
/// around as a live object in-process and as a <c>JsonElement</c> after a broker round-trip. Every
/// consumer (WebSocket fan-out, search indexing, cache invalidation) needs the same tolerant
/// unwrapping, and three private copies of it would only be three chances to get it subtly wrong.
/// </summary>
public static class DomainEventPayload
{
    public const string CommentCreated = "CommentCreated";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static bool IsCommentCreated(this DomainEventEnvelope envelope)
        => string.Equals(envelope.EventType, CommentCreated, StringComparison.OrdinalIgnoreCase);

    /// <summary>The comment carried by a <c>CommentCreated</c> payload, or null when unreadable.</summary>
    public static CommentDto? AsComment(this object? payload)
    {
        switch (payload)
        {
            case null:
                return null;
            case CommentCreatedEvent @event:
                return @event.Comment;
            case CommentCreatedPayload created:
                return created.Comment;
            case CommentDto dto:
                return dto;
        }

        try
        {
            var element = payload is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(payload, Json);

            // CommentCreated carries { comment: {...} } (docs/API-v2.md §7.3).
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty("comment", out var inner)
                && inner.ValueKind == JsonValueKind.Object)
            {
                element = inner;
            }

            // An object that merely happens to fit the DTO shape (every field is optional and unknown
            // properties are ignored) is not a comment. Without an id there is nothing to broadcast or
            // index, and acting on id 0 would write a junk document — so an empty DTO is treated as an
            // unreadable payload rather than as a comment.
            var comment = element.Deserialize<CommentDto>(Json);
            return comment is { Id: > 0 } ? comment : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
