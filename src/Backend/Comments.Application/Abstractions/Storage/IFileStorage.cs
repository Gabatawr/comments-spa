namespace Comments.Application.Abstractions.Storage;

/// <summary>
/// Binary attachment storage port (docs/ARCHITECTURE-v2.md §3, docs/API-v2.md §11).
///
/// Implementations: <c>FileSystemStorage</c> (local or shared volume), <c>S3FileStorage</c>
/// (any S3-compatible object store: AWS S3, MinIO, Yandex Object Storage, Cloudflare R2,
/// DigitalOcean Spaces) and <c>AzureBlobFileStorage</c>. Selected by <c>Providers:Storage</c>;
/// callers never branch on the provider name.
/// </summary>
public interface IFileStorage
{
    /// <summary>Persists content under <paramref name="storedName"/>; returns the storage-relative path.</summary>
    Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading, or null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default);

    bool Exists(string storagePath);

    /// <summary>
    /// Filesystem fast path. Returns <c>true</c> only for providers that can hand out an absolute
    /// local path, so the web layer may use sendfile and range requests; object stores answer
    /// <c>false</c> and the caller streams through <see cref="OpenReadAsync"/> instead.
    /// Deliberately non-throwing: "no local path" is a normal answer, not an error.
    /// </summary>
    bool TryGetLocalPath(string storagePath, out string fullPath);

    /// <summary>Configured provider name, for health/info reporting only — never for branching.</summary>
    string Provider { get; }

    /// <summary>Cheap reachability probe; never throws.</summary>
    bool IsAvailable { get; }
}
