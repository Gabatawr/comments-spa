using System.ComponentModel.DataAnnotations;

namespace CommentsApi.Domain;

public static class AttachmentKinds
{
    public const string Image = "image";
    public const string Text = "text";
}

/// <summary>
/// An uploaded file attached to a comment. Images are re-encoded/resized to fit 320x240.
/// </summary>
public class Attachment
{
    public int Id { get; set; }

    /// <summary>Original client file name.</summary>
    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>Name on disk (GUID + extension).</summary>
    [MaxLength(260)]
    public string StoredName { get; set; } = string.Empty;

    /// <summary>Path relative to <c>Storage:Root</c>.</summary>
    [MaxLength(512)]
    public string StoragePath { get; set; } = string.Empty;

    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>"image" or "text".</summary>
    [MaxLength(10)]
    public string Kind { get; set; } = AttachmentKinds.Text;

    public long SizeBytes { get; set; }

    /// <summary>Final dimensions after proportional downscale (images only).</summary>
    public int? Width { get; set; }

    public int? Height { get; set; }

    /// <summary>Dimensions as uploaded (images only).</summary>
    public int? OriginalWidth { get; set; }

    public int? OriginalHeight { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
}
