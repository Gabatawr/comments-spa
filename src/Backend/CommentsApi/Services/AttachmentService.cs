using System.Security.Cryptography;
using System.Text;
using CommentsApi.Data;
using CommentsApi.Domain;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace CommentsApi.Services;

/// <summary>Storage configuration (docs/ARCHITECTURE.md).</summary>
public sealed class AttachmentStorageOptions
{
    /// <summary>Root directory for uploads; relative paths are resolved against the content root.</summary>
    public string Root { get; set; } = "storage";
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

public interface IAttachmentService
{
    /// <summary>Validates size/type/magic bytes and (for images) downscales to fit 320x240.</summary>
    Task<AttachmentValidation> ValidateAsync(IFormFile file, CancellationToken cancellationToken = default);

    /// <summary>Writes the bytes to storage and persists the <see cref="Attachment"/> row.</summary>
    Task<Attachment> PersistAsync(PreparedAttachment prepared, CancellationToken cancellationToken = default);

    string GetStorageRoot();

    string GetFullPath(Attachment attachment);
}

/// <summary>
/// Upload pipeline: magic-byte sniffing, size limits (5 MB image / 100 KB TXT), ImageSharp
/// proportional downscale (never upscale) and SHA-256 integrity hashing.
/// </summary>
public sealed class AttachmentService : IAttachmentService
{
    public const long MaxImageBytes = 5 * 1024 * 1024;
    public const long MaxTextBytes = 102400;
    public const int MaxImageWidth = 320;
    public const int MaxImageHeight = 240;

    public const string ErrorEmpty = "Uploaded file is empty.";
    public const string ErrorTooLargeImage = "Image is too large. Maximum allowed size is 5 MB.";
    public const string ErrorTooLargeText = "Text file is too large. Maximum allowed size is 100 KB.";
    public const string ErrorUnsupported = "Only JPG, JPEG, PNG, GIF images or TXT text files are allowed.";
    public const string ErrorBadImage = "File is not a valid image.";
    public const string ErrorNotText = "TXT file must be valid UTF-8 text.";
    public const string ErrorContentMismatch = "File content does not match an allowed image format (JPG, JPEG, PNG, GIF).";

    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _environment;
    private readonly IOptions<AttachmentStorageOptions> _options;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(
        AppDbContext db,
        IWebHostEnvironment environment,
        IOptions<AttachmentStorageOptions> options,
        ILogger<AttachmentService> logger)
    {
        _db = db;
        _environment = environment;
        _options = options;
        _logger = logger;
    }

    public async Task<AttachmentValidation> ValidateAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return AttachmentValidation.Fail(ErrorEmpty);
        }

        byte[] bytes;
        await using (var buffer = new MemoryStream())
        {
            await file.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
        {
            return AttachmentValidation.Fail(ErrorEmpty);
        }

        var originalName = SanitizeFileName(file.FileName);
        var extension = Path.GetExtension(originalName).ToLowerInvariant();

        if (IsPng(bytes))
        {
            return ValidateImage(bytes, originalName, ImageFormat.Png);
        }

        if (IsJpeg(bytes))
        {
            return ValidateImage(bytes, originalName, ImageFormat.Jpeg);
        }

        if (IsGif(bytes))
        {
            return ValidateImage(bytes, originalName, ImageFormat.Gif);
        }

        if (extension == ".txt")
        {
            if (!IsUtf8Text(bytes))
            {
                return AttachmentValidation.Fail(ErrorNotText);
            }

            if (bytes.Length > MaxTextBytes)
            {
                return AttachmentValidation.Fail(ErrorTooLargeText);
            }

            return AttachmentValidation.Ok(new PreparedAttachment(
                originalName,
                ".txt",
                "text/plain",
                AttachmentKinds.Text,
                bytes,
                null,
                null,
                null,
                null,
                Hash(bytes)));
        }

        if (IsImageExtension(extension))
        {
            return AttachmentValidation.Fail(ErrorContentMismatch);
        }

        return AttachmentValidation.Fail(ErrorUnsupported);
    }

    public async Task<Attachment> PersistAsync(PreparedAttachment prepared, CancellationToken cancellationToken = default)
    {
        var root = GetStorageRoot();
        Directory.CreateDirectory(root);

        var storedName = Guid.NewGuid().ToString("N") + prepared.Extension;
        var fullPath = Path.Combine(root, storedName);

        await File.WriteAllBytesAsync(fullPath, prepared.Bytes, cancellationToken);

        var attachment = new Attachment
        {
            FileName = prepared.FileName,
            StoredName = storedName,
            StoragePath = storedName,
            ContentType = prepared.ContentType,
            Kind = prepared.Kind,
            SizeBytes = prepared.Bytes.Length,
            Width = prepared.Width,
            Height = prepared.Height,
            OriginalWidth = prepared.OriginalWidth,
            OriginalHeight = prepared.OriginalHeight,
            CreatedAt = DateTime.UtcNow,
            Sha256 = prepared.Sha256,
        };

        _db.Attachments.Add(attachment);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            TryDelete(fullPath);
            throw;
        }

        _logger.LogInformation(
            "Stored attachment #{AttachmentId} ({Kind}, {Bytes} bytes) as {StoredName}",
            attachment.Id,
            attachment.Kind,
            attachment.SizeBytes,
            attachment.StoredName);

        return attachment;
    }

    public string GetStorageRoot()
    {
        var configured = _options.Value.Root;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "storage";
        }

        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configured));
    }

    public string GetFullPath(Attachment attachment)
    {
        var root = GetStorageRoot();
        var fullPath = Path.GetFullPath(Path.Combine(root, attachment.StoragePath));

        // Defence against path traversal (StoragePath is server-generated, but keep it hard).
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved attachment path escapes the storage root.");
        }

        return fullPath;
    }

    private AttachmentValidation ValidateImage(byte[] bytes, string originalName, ImageFormat format)
    {
        // The declared format must also be allowlisted: a real PNG named a.bmp / a.webp must be
        // rejected (the client cannot smuggle a non-allowed format past the magic-byte check).
        var declaredExtension = Path.GetExtension(originalName).ToLowerInvariant();
        if (declaredExtension.Length > 0 && !IsImageExtension(declaredExtension))
        {
            return AttachmentValidation.Fail(ErrorUnsupported);
        }

        if (bytes.Length > MaxImageBytes)
        {
            return AttachmentValidation.Fail(ErrorTooLargeImage);
        }

        Image image;
        try
        {
            image = Image.Load(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageSharp failed to decode an upload");
            return AttachmentValidation.Fail(ErrorBadImage);
        }

        using (image)
        {
            var originalWidth = image.Width;
            var originalHeight = image.Height;
            if (originalWidth <= 0 || originalHeight <= 0)
            {
                return AttachmentValidation.Fail(ErrorBadImage);
            }

            var scale = Math.Min(1.0, Math.Min((double)MaxImageWidth / originalWidth, (double)MaxImageHeight / originalHeight));
            var width = Math.Max(1, (int)Math.Round(originalWidth * scale));
            var height = Math.Max(1, (int)Math.Round(originalHeight * scale));

            if (width != originalWidth || height != originalHeight)
            {
                image.Mutate(context => context.Resize(width, height));
            }

            using var output = new MemoryStream();
            Save(image, format, output);
            var stored = output.ToArray();

            var (contentType, extension) = format switch
            {
                ImageFormat.Jpeg => ("image/jpeg", ".jpg"),
                ImageFormat.Gif => ("image/gif", ".gif"),
                _ => ("image/png", ".png"),
            };

            return AttachmentValidation.Ok(new PreparedAttachment(
                originalName,
                extension,
                contentType,
                AttachmentKinds.Image,
                stored,
                width,
                height,
                originalWidth,
                originalHeight,
                Hash(stored)));
        }
    }

    private static void Save(Image image, ImageFormat format, Stream output)
    {
        switch (format)
        {
            case ImageFormat.Jpeg:
                image.SaveAsJpeg(output, new JpegEncoder { Quality = 85 });
                break;
            case ImageFormat.Gif:
                image.SaveAsGif(output);
                break;
            default:
                image.SaveAsPng(output);
                break;
        }
    }

    private static bool IsPng(byte[] bytes) =>
        bytes.Length >= PngMagic.Length && bytes.AsSpan(0, PngMagic.Length).SequenceEqual(PngMagic);

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsGif(byte[] bytes) =>
        bytes.Length >= 6
        && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38
        && (bytes[4] == 0x37 || bytes[4] == 0x39) && bytes[5] == 0x61;

    private static bool IsImageExtension(string extension) =>
        extension is ".png" or ".jpg" or ".jpeg" or ".gif";

    private static bool IsUtf8Text(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        foreach (var b in bytes)
        {
            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                return false;
            }
        }

        return true;
    }

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var cleaned = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (!char.IsControl(ch) && ch != '"' && ch != '\\' && ch != '/')
            {
                cleaned.Append(ch);
            }
        }

        var result = cleaned.ToString().Trim();
        if (result.Length == 0)
        {
            result = "file";
        }

        return result.Length > 200 ? result[..200] : result;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete leftover upload {Path}", path);
        }
    }

    private enum ImageFormat
    {
        Png,
        Jpeg,
        Gif,
    }
}
