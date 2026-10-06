using Comments.Domain;

namespace Comments.Application.Services;

/// <summary>Upload size limits and user-facing messages (v1-compatible).</summary>
public static class AttachmentErrors
{
    public const long MaxImageBytes = 5 * 1024 * 1024;
    public const long MaxTextBytes = 102400;
    public const int MaxImageWidth = 320;
    public const int MaxImageHeight = 240;

    public const string Empty = "Uploaded file is empty.";
    public const string TooLargeImage = "Image is too large. Maximum allowed size is 5 MB.";
    public const string TooLargeText = "Text file is too large. Maximum allowed size is 100 KB.";
    public const string Unsupported = "Only JPG, JPEG, PNG, GIF images or TXT text files are allowed.";
    public const string BadImage = "File is not a valid image.";
    public const string NotText = "TXT file must be valid UTF-8 text.";
    public const string ContentMismatch = "File content does not match an allowed image format (JPG, JPEG, PNG, GIF).";
}

/// <summary>
/// Framework-neutral view of an uploaded file. <c>Comments.Api</c> adapts <c>IFormFile</c> to this
/// type so the Application layer never references ASP.NET (docs/ARCHITECTURE-v2.md §1).
/// </summary>
public sealed class UploadedFile
{
    public string FileName { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public long Length { get; init; }

    /// <summary>Opens the upload stream. The caller owns disposal.</summary>
    public Func<Stream> OpenReadStream { get; init; } = () => Stream.Null;
}

/// <summary>Validated, in-memory representation of an upload, ready to be persisted.</summary>
public sealed record PreparedAttachment(
    string FileName,
    string Extension,
    string ContentType,
    string Kind,
    byte[] Bytes,
    int? Width,
    int? Height,
    int? OriginalWidth,
    int? OriginalHeight,
    string Sha256);

public sealed record AttachmentValidation(PreparedAttachment? Prepared, string? Error)
{
    public bool IsValid => Prepared is not null;

    public static AttachmentValidation Ok(PreparedAttachment prepared) => new(prepared, null);

    public static AttachmentValidation Fail(string error) => new(null, error);
}

/// <summary>
/// Upload pipeline port: validates size/type/magic bytes and (for images) downscales to fit
/// 320x240. Implementation lives in Infrastructure (ImageSharp + IFileStorage).
/// v1 methods are preserved (docs/ARCHITECTURE-v2.md §3).
/// </summary>
public interface IAttachmentService
{
    /// <summary>Validates size/type/magic bytes and (for images) downscales to fit 320x240.</summary>
    Task<AttachmentValidation> ValidateAsync(UploadedFile file, CancellationToken cancellationToken = default);

    /// <summary>Writes the bytes to storage and persists the <see cref="Attachment"/> row.</summary>
    Task<Attachment> PersistAsync(PreparedAttachment prepared, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes <see cref="PersistAsync"/>: removes the metadata row and the stored bytes. Needed
    /// because the bytes are written before the comment that references them exists, so a failure
    /// in between would otherwise leave an attachment nothing can ever reach.
    /// Best effort by contract — it must not throw while another error is being surfaced.
    /// </summary>
    Task DeleteAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Filesystem fast path for serving a stored attachment: <c>true</c> plus the absolute path
    /// when the provider keeps bytes on disk, <c>false</c> for object stores — the caller then
    /// streams through <see cref="Abstractions.Storage.IFileStorage.OpenReadAsync"/>.
    /// </summary>
    bool TryGetLocalPath(Attachment attachment, out string fullPath);
}
