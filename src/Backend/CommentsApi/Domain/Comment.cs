using System.ComponentModel.DataAnnotations;

namespace CommentsApi.Domain;

/// <summary>
/// A user comment. Root comments have <see cref="ParentId"/> == null, answers point to their parent.
/// </summary>
public class Comment
{
    public int Id { get; set; }

    /// <summary>Self-reference to the parent comment; null for a root comment.</summary>
    public int? ParentId { get; set; }

    public Comment? Parent { get; set; }

    public ICollection<Comment> Replies { get; set; } = new List<Comment>();

    [MaxLength(50)]
    public string UserName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? HomePage { get; set; }

    /// <summary>Sanitised HTML (allowlist: a[href,title], code, i, strong).</summary>
    public string TextHtml { get; set; } = string.Empty;

    /// <summary>Same text without any markup.</summary>
    public string TextPlain { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string? ClientIp { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public int? AttachmentId { get; set; }

    public Attachment? Attachment { get; set; }
}
