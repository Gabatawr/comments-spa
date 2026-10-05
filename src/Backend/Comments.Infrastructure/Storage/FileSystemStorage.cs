using Comments.Application.Abstractions.Storage;
using Comments.Application.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Comments.Infrastructure.Storage;

/// <summary>
/// Local / shared-volume storage (docs/API-v2.md §11). Path traversal is blocked on every access.
/// The only provider with a local fast path, so it is the only one that answers
/// <see cref="TryGetLocalPath"/> with <c>true</c>.
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
        var fullPath = Resolve(storedName);

        await using var file = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(file, cancellationToken);
        return storedName;
    }

    public Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Resolve(storagePath);
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
            var fullPath = Resolve(storagePath);
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

    public bool Exists(string storagePath) => File.Exists(Resolve(storagePath));

    public bool TryGetLocalPath(string storagePath, out string fullPath)
    {
        fullPath = Resolve(storagePath);
        return true;
    }

    /// <summary>Absolute path under the storage root; throws when it would escape the root.</summary>
    private string Resolve(string storagePath)
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
