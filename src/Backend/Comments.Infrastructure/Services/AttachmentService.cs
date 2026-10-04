using System.Security.Cryptography;
using System.Text;
using Comments.Application.Abstractions.Persistence;
using Comments.Application.Abstractions.Storage;
using Comments.Application.Services;
using Comments.Domain;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Comments.Infrastructure.Services;

/// <summary>
/// Upload pipeline: magic-byte sniffing, size limits (5 MB image / 100 KB TXT), ImageSharp
/// proportional downscale (never upscale) and SHA-256 integrity hashing. The file bytes go
/// through <see cref="IFileStorage"/>; metadata goes through <see cref="IAttachmentRepository"/>.
/// </summary>
public sealed class AttachmentService : IAttachmentService
{
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private readonly IAttachmentRepository _attachments;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IFileStorage _storage;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(
        IAttachmentRepository attachments,
        IUnitOfWork unitOfWork,
        IFileStorage storage,
        ILogger<AttachmentService> logger)
    {
        _attachments = attachments;
        _unitOfWork = unitOfWork;
        _storage = storage;
        _logger = logger;
    }

    public async Task<AttachmentValidation> ValidateAsync(UploadedFile file, CancellationToken cancellationToken = default)
    {
        if (file is null || file.Length == 0)
        {
            return AttachmentValidation.Fail(AttachmentErrors.Empty);
        }

        byte[] bytes;
        await using (var buffer = new MemoryStream())
        {
            await using (var source = file.OpenReadStream())
            {
                await source.CopyToAsync(buffer, cancellationToken);
            }

            bytes = buffer.ToArray();
        }

        if (bytes.Length == 0)
        {
            return AttachmentValidation.Fail(AttachmentErrors.Empty);
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
                return AttachmentValidation.Fail(AttachmentErrors.NotText);
            }

            if (bytes.Length > AttachmentErrors.MaxTextBytes)
            {
                return AttachmentValidation.Fail(AttachmentErrors.TooLargeText);
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
            return AttachmentValidation.Fail(AttachmentErrors.ContentMismatch);
        }

        return AttachmentValidation.Fail(AttachmentErrors.Unsupported);
    }

    public async Task<Attachment> PersistAsync(PreparedAttachment prepared, CancellationToken cancellationToken = default)
    {
        var storedName = Guid.NewGuid().ToString("N") + prepared.Extension;
        await using (var content = new MemoryStream(prepared.Bytes, writable: false))
        {
            await _storage.SaveAsync(storedName, content, cancellationToken);
        }

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

        await _attachments.AddAsync(attachment, cancellationToken);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await _storage.DeleteAsync(storedName, CancellationToken.None);
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

    public string GetStorageRoot() => _storage.GetFullPath(string.Empty);

    public string GetFullPath(Attachment attachment) => _storage.GetFullPath(attachment.StoragePath);

    private AttachmentValidation ValidateImage(byte[] bytes, string originalName, ImageFormat format)
    {
        // The declared format must also be allowlisted: a real PNG named a.bmp / a.webp must be
        // rejected (the client cannot smuggle a non-allowed format past the magic-byte check).
        var declaredExtension = Path.GetExtension(originalName).ToLowerInvariant();
        if (declaredExtension.Length > 0 && !IsImageExtension(declaredExtension))
        {
            return AttachmentValidation.Fail(AttachmentErrors.Unsupported);
        }

        if (bytes.Length > AttachmentErrors.MaxImageBytes)
        {
            return AttachmentValidation.Fail(AttachmentErrors.TooLargeImage);
        }

        Image image;
        try
        {
            image = Image.Load(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ImageSharp failed to decode an upload");
            return AttachmentValidation.Fail(AttachmentErrors.BadImage);
        }

        using (image)
        {
            var originalWidth = image.Width;
            var originalHeight = image.Height;
            if (originalWidth <= 0 || originalHeight <= 0)
            {
                return AttachmentValidation.Fail(AttachmentErrors.BadImage);
            }

            var scale = Math.Min(1.0, Math.Min(
                (double)AttachmentErrors.MaxImageWidth / originalWidth,
                (double)AttachmentErrors.MaxImageHeight / originalHeight));
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

    private enum ImageFormat
    {
        Png,
        Jpeg,
        Gif,
    }
}
