using Comments.Application.Abstractions.Storage;
using Comments.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Comments.Infrastructure.Storage;

/// <summary>
/// Local filesystem storage (docs/API-v2.md §11). Path traversal is blocked on every access.
/// </summary>
public sealed class FileSystemStorage : IFileStorage
{
    private readonly string _root;
    private readonly ILogger<FileSystemStorage> _logger;

    public FileSystemStorage(
        IOptions<AttachmentStorageOptions> options,
        IHostEnvironment environment,
        ILogger<FileSystemStorage> logger)
    {
        _logger = logger;
        var configured = options.Value.Root;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "storage";
        }

        _root = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, configured));
    }

    public string Provider => "filesystem";

    public bool IsAvailable
    {
        get
        {
            try
            {
                Directory.CreateDirectory(_root);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Storage root {Root} is not writable", _root);
                return false;
            }
        }
    }

    public async Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_root);
        var fullPath = GetFullPath(storedName);

        await using var file = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, cancellationToken);
        return storedName;
    }

    public Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var fullPath = GetFullPath(storagePath);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = GetFullPath(storagePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                return Task.FromResult(true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete stored file {Path}", storagePath);
        }

        return Task.FromResult(false);
    }

    public bool Exists(string storagePath) => File.Exists(GetFullPath(storagePath));

    public string GetFullPath(string storagePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(_root, storagePath ?? string.Empty));

        // Defence against path traversal (stored names are server-generated, but keep it hard).
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(fullPath, _root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved storage path escapes the storage root.");
        }

        return fullPath;
    }
}

/// <summary>
/// S3 swap-in point (docs/API-v2.md §11). Deliberately not implemented locally; selecting this
/// provider must fail loudly with a clear message instead of silently doing nothing.
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    public const string Message =
        "Storage:Provider=s3 is a documented cloud swap-in point and is not implemented locally. " +
        "Use Storage:Provider=filesystem for local/compose runs.";

    public string Provider => "s3";

    public bool IsAvailable => false;

    private static NotSupportedException NotSupported() => new(Message);

    public Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default) => throw NotSupported();

    public Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default) => throw NotSupported();

    public Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default) => throw NotSupported();

    public bool Exists(string storagePath) => throw NotSupported();

    public string GetFullPath(string storagePath) => throw NotSupported();
}

/// <summary>Azure Blob swap-in point (docs/API-v2.md §11). See <see cref="S3FileStorage"/>.</summary>
public sealed class AzureBlobFileStorage : IFileStorage
{
    public const string Message =
        "Storage:Provider=azureblob is a documented cloud swap-in point and is not implemented locally. " +
        "Use Storage:Provider=filesystem for local/compose runs.";

    public string Provider => "azureblob";

    public bool IsAvailable => false;

    private static NotSupportedException NotSupported() => new(Message);

    public Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default) => throw NotSupported();

    public Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default) => throw NotSupported();

    public Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default) => throw NotSupported();

    public bool Exists(string storagePath) => throw NotSupported();

    public string GetFullPath(string storagePath) => throw NotSupported();
}
