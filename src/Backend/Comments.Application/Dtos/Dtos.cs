using System.Text.Json.Serialization;

namespace Comments.Application.Dtos;

/// <summary>Attachment projection for <see cref="CommentDto"/> (docs/API-v2.md §1).</summary>
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

/// <summary>Comment projection for <see cref="CommentDto"/> (docs/API-v2.md §1).</summary>
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

    /// <summary>
    /// Snapshot of the parent's flat text taken when this reply was created; null for root
    /// comments, legacy rows and replies whose parent had no text. Plain text, never HTML.
    /// Additive optional field (docs/DESIGN-v2.1-decisions.md §1), the frozen docs/API-v2.md is
    /// not changed.
    /// </summary>
    public string? QuotedText { get; set; }

    public DateTime CreatedAt { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public AttachmentDto? Attachment { get; set; }
    public int ReplyCount { get; set; }
    public List<CommentDto> Replies { get; set; } = new();
}

/// <summary>Paged root-comment response (docs/API-v2.md §2.3, §4).</summary>
public sealed class CommentPageDto
{
    public List<CommentDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
    public string SortBy { get; set; } = "createdAt";
    public string SortDir { get; set; } = "desc";

    /// <summary>Opaque keyset cursor for the next page, or null when this is the last page (docs/API-v2.md §4.5).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NextCursor { get; set; }
}

/// <summary>Response of <c>POST /api/comments</c> and <c>POST /api/comments/{id}/child</c>.</summary>
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

/// <summary>Standard error body (docs/API-v2.md §0).</summary>
public sealed class ErrorResponse
{
    public string Title { get; set; } = "Validation failed";
    public int Status { get; set; } = 400;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Errors { get; set; }
}

/// <summary>Dev-only captcha peek response (docs/API-v2.md §2.8).</summary>
public sealed class CaptchaPeekDto
{
    public string CaptchaId { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
}

/// <summary>GraphQL validation error (docs/API-v2.md §5).</summary>
public sealed class ValidationErrorDto
{
    public string Field { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>Request body of the JSON create path <c>POST /api/comments/{id}/child</c> (docs/API-v2.md §3.3).</summary>
public sealed class CreateCommentRequest
{
    public string? UserName { get; set; }
    public string? Email { get; set; }
    public string? HomePage { get; set; }
    public string? Text { get; set; }
    public int? ParentId { get; set; }
    public string? CaptchaId { get; set; }
    public string? CaptchaAnswer { get; set; }
}

/// <summary>Response of <c>POST /api/stats</c> (docs/API-v2.md §3.5).</summary>
public sealed class StatsDto
{
    public long TotalComments { get; set; }
    public long TotalRoots { get; set; }
    public long TotalAttachments { get; set; }
    public DateTime? OldestAt { get; set; }
    public DateTime? NewestAt { get; set; }
    public double CacheHitRate { get; set; }
}

/// <summary>Response of <c>POST /api/dev/seed</c> (docs/API-v2.md §3.4).</summary>
public sealed class SeedRequest
{
    public int Count { get; set; } = 10000;
    public int Roots { get; set; } = 1000;
    public int Depth { get; set; } = 3;
    public int BatchSize { get; set; } = 1000;
    public bool Clear { get; set; }
}

public sealed class SeedResultDto
{
    public long Created { get; set; }
    public long Roots { get; set; }
    public long ElapsedMs { get; set; }
    public double CommentsPerSecond { get; set; }
}
