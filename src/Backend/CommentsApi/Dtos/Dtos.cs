using System.Text.Json.Serialization;

namespace CommentsApi.Dtos;

/// <summary>Attachment projection for <see cref="CommentDto"/> (docs/API.md §1).</summary>
public sealed class AttachmentDto
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;

    /// <summary>"image" | "text".</summary>
    public string Kind { get; set; } = string.Empty;

    public long SizeBytes { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }

    public string Url { get; set; } = string.Empty;

    /// <summary>Null for text attachments.</summary>
    public string? ThumbUrl { get; set; }
}

/// <summary>Comment projection for <see cref="CommentDto"/> (docs/API.md §1).</summary>
public sealed class CommentDto
{
    public int Id { get; set; }
    public int? ParentId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? HomePage { get; set; }

    /// <summary>Already sanitised HTML.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Text without tags.</summary>
    public string TextPlain { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public AttachmentDto? Attachment { get; set; }
    public int ReplyCount { get; set; }
    public List<CommentDto> Replies { get; set; } = new();
}

/// <summary>Paged root-comment response (docs/API.md §2.2).</summary>
public sealed class CommentPageDto
{
    public List<CommentDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
    public string SortBy { get; set; } = "createdAt";
    public string SortDir { get; set; } = "desc";
}

/// <summary>Response of <c>POST /api/comments</c>.</summary>
public sealed class CommentCreatedDto
{
    public CommentDto Comment { get; set; } = new();
}

public sealed class CaptchaDto
{
    public string CaptchaId { get; set; } = string.Empty;

    /// <summary>PNG rendered as a data URL (data:image/png;base64,...).</summary>
    public string Image { get; set; } = string.Empty;

    public int ExpiresInSeconds { get; set; }
}

/// <summary>Request body of <c>POST /api/preview</c>.</summary>
public sealed class PreviewRequest
{
    public string? Text { get; set; }
}

/// <summary>Response of <c>POST /api/preview</c>.</summary>
public sealed class PreviewResponse
{
    public bool Valid { get; set; }
    public string Html { get; set; } = string.Empty;
    public string Plain { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
}

/// <summary>Standard validation error body (docs/API.md §0).</summary>
public sealed class ErrorResponse
{
    public string Title { get; set; } = "Validation failed";
    public int Status { get; set; } = 400;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Errors { get; set; }
}

/// <summary>Dev-only captcha peek response (docs/API.md §2.10).</summary>
public sealed class CaptchaPeekDto
{
    public string CaptchaId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>Health response (docs/API.md §2.8).</summary>
public sealed class HealthDto
{
    public string Status { get; set; } = "ok";
    public string Database { get; set; } = "ok";
    public string Cache { get; set; } = "ok";
    public QueueHealthDto Queue { get; set; } = new();
    public WebSocketHealthDto Websocket { get; set; } = new();
    public string Version { get; set; } = "1.0.0";
}

public sealed class QueueHealthDto
{
    public int Pending { get; set; }
    public int Processed { get; set; }
}

public sealed class WebSocketHealthDto
{
    public int Clients { get; set; }
}
