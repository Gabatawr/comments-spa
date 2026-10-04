using System.Text;
using System.Text.Json.Nodes;
using Comments.Application.Abstractions.Persistence;

namespace Comments.Application.Services;

/// <summary>
/// Raised when a <c>cursor</c> query parameter cannot be honoured (docs/API-v2.md §4.5).
/// <c>Comments.Api</c> maps this to HTTP 400 <c>{ "detail": ex.Detail }</c>.
/// </summary>
public sealed class CommentCursorException : Exception
{
    public CommentCursorException(string detail) : base(detail)
    {
        Detail = detail;
    }

    public string Detail { get; }

    public string Parameter => "cursor";
}

/// <summary>
/// Opaque keyset cursor codec: <c>base64url(JSON { "s", "d", "v", "id" })</c> (docs/API-v2.md §4.5).
/// </summary>
public static class CommentCursorCodec
{
    public static string Encode(CommentCursor cursor)
    {
        var json = new JsonObject
        {
            ["s"] = cursor.SortBy,
            ["d"] = cursor.SortDir,
            ["v"] = cursor.Value,
            ["id"] = cursor.Id,
        }.ToJsonString();

        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    public static CommentCursor Decode(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new CommentCursorException("Cursor is invalid.");
        }

        try
        {
            var bytes = Base64UrlDecode(raw.Trim());
            var node = JsonNode.Parse(bytes);
            var sortBy = node?["s"]?.GetValue<string>();
            var sortDir = node?["d"]?.GetValue<string>();
            var value = node?["v"]?.GetValue<string>();
            var id = node?["id"]?.GetValue<int>();

            if (sortBy is null || sortDir is null || value is null || id is null)
            {
                throw new CommentCursorException("Cursor is invalid.");
            }

            return new CommentCursor(sortBy, sortDir, value, id.Value);
        }
        catch (CommentCursorException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new CommentCursorException("Cursor is invalid.");
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new CommentCursorException("Cursor is invalid."),
        };

        return Convert.FromBase64String(padded);
    }
}
