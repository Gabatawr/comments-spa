namespace Comments.Application.Abstractions.Storage;

/// <summary>
/// Binary attachment storage port (docs/ARCHITECTURE-v2.md §3, docs/API-v2.md §11).
/// Local implementation: <c>FileSystemStorage</c>; S3 / Azure Blob are swap-in points.
/// </summary>
public interface IFileStorage
{
    /// <summary>Persists content under <paramref name="storedName"/>; returns the storage-relative path.</summary>
    Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading, or null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default);

    bool Exists(string storagePath);

    /// <summary>Absolute path for filesystem fast-path serving. Throws for non-filesystem providers.</summary>
    string GetFullPath(string storagePath);

    string Provider { get; }

    bool IsAvailable { get; }
}
