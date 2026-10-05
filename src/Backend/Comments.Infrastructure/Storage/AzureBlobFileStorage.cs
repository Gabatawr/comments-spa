using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Comments.Application.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Comments.Infrastructure.Storage;

/// <summary>
/// Azure Blob Storage settings (docs/API-v2.md §11). Authentication is picked in this order:
/// connection string → account URL + SAS token → account URL + <see cref="DefaultAzureCredential"/>
/// (managed identity, workload identity, Azure CLI — the cloud-native path that needs no secret).
/// </summary>
public sealed class AzureBlobStorageOptions
{
    /// <summary>Full connection string (also what the Azurite emulator hands out).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>e.g. <c>https://myaccount.blob.core.windows.net</c>. Used when no connection string is set.</summary>
    public string? AccountUrl { get; set; }

    /// <summary>Optional SAS token when the account URL does not carry one.</summary>
    public string? SasToken { get; set; }

    public string? Container { get; set; }

    /// <summary>Blob prefix inside the container, e.g. <c>attachments/prod</c>.</summary>
    public string? Prefix { get; set; }

    /// <summary>Create the container on first write when it is missing (handy for Azurite/dev).</summary>
    public bool CreateContainerIfMissing { get; set; } = true;

    /// <summary>Per-request network timeout. Kept short so an unreachable container cannot stall /api/health.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// Azure Blob attachment storage. Selected with <c>Providers:Storage=azureblob</c>.
/// Blobs have no local path, so <see cref="TryGetLocalPath"/> always answers <c>false</c>.
/// </summary>
public sealed class AzureBlobFileStorage : IFileStorage
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix;
    private readonly bool _createContainerIfMissing;
    private readonly ILogger<AzureBlobFileStorage> _logger;

    private readonly object _probeLock = new();
    private bool _lastProbe;
    private DateTime _lastProbeAt = DateTime.MinValue;

    public AzureBlobFileStorage(IOptions<AzureBlobStorageOptions> options, ILogger<AzureBlobFileStorage> logger)
    {
        var settings = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(settings.Container))
        {
            throw new InvalidOperationException(
                "Providers:Storage=azureblob requires Storage:AzureBlob:Container (docs/API-v2.md §11).");
        }

        _prefix = settings.Prefix?.Trim().Trim('/') ?? string.Empty;
        _createContainerIfMissing = settings.CreateContainerIfMissing;
        _container = CreateClient(settings).GetBlobContainerClient(settings.Container.Trim());

        _logger.LogInformation(
            "Azure Blob storage: container={Container} prefix={Prefix} auth={Auth}",
            _container.Name,
            string.IsNullOrEmpty(_prefix) ? "(root)" : _prefix,
            AuthMode(settings));
    }

    public string Provider => "azureblob";

    public bool IsAvailable
    {
        get
        {
            lock (_probeLock)
            {
                if (DateTime.UtcNow - _lastProbeAt < TimeSpan.FromSeconds(15))
                {
                    return _lastProbe;
                }

                try
                {
                    using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    _lastProbe = _container.ExistsAsync(probeTimeout.Token).GetAwaiter().GetResult().Value;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Azure Blob probe against container {Container} failed", _container.Name);
                    _lastProbe = false;
                }

                _lastProbeAt = DateTime.UtcNow;
                return _lastProbe;
            }
        }
    }

    public async Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default)
    {
        if (_createContainerIfMissing)
        {
            await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        }

        var blob = _container.GetBlobClient(BuildBlobName(storedName));
        await blob.UploadAsync(content, new BlobHttpHeaders { ContentType = ContentTypeFor(storedName) }, cancellationToken: cancellationToken);
        return storedName;
    }

    public async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var blob = _container.GetBlobClient(BuildBlobName(storagePath));
            var download = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);

            // Buffer it: the download stream is tied to the response and attachments are <= 100 KB.
            var buffer = new MemoryStream();
            await download.Value.Content.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var blob = _container.GetBlobClient(BuildBlobName(storagePath));
            var response = await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete blob {Blob}", BuildBlobName(storagePath));
            return false;
        }
    }

    public bool Exists(string storagePath)
    {
        try
        {
            return _container.GetBlobClient(BuildBlobName(storagePath)).Exists();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stat blob {Blob}", BuildBlobName(storagePath));
            return false;
        }
    }

    /// <summary>Blobs have no local path — the caller must stream instead.</summary>
    public bool TryGetLocalPath(string storagePath, out string fullPath)
    {
        fullPath = string.Empty;
        return false;
    }

    private string BuildBlobName(string storagePath) =>
        string.IsNullOrEmpty(_prefix) ? storagePath : $"{_prefix}/{storagePath}";

    private static string ContentTypeFor(string storedName) =>
        Path.GetExtension(storedName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };

    private static string AuthMode(AzureBlobStorageOptions settings) =>
        !string.IsNullOrWhiteSpace(settings.ConnectionString) ? "connection-string"
        : !string.IsNullOrWhiteSpace(settings.SasToken) ? "sas"
        : "DefaultAzureCredential";

    private static BlobServiceClient CreateClient(AzureBlobStorageOptions settings)
    {
        var clientOptions = new BlobClientOptions();
        clientOptions.Retry.NetworkTimeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 300));
        clientOptions.Retry.MaxRetries = 2;

        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return new BlobServiceClient(settings.ConnectionString, clientOptions);
        }

        if (string.IsNullOrWhiteSpace(settings.AccountUrl))
        {
            throw new InvalidOperationException(
                "Providers:Storage=azureblob needs Storage:AzureBlob:ConnectionString or Storage:AzureBlob:AccountUrl.");
        }

        var uri = new Uri(settings.AccountUrl.TrimEnd('/'));

        if (!string.IsNullOrWhiteSpace(settings.SasToken))
        {
            return new BlobServiceClient(uri, new AzureSasCredential(settings.SasToken.TrimStart('?')), clientOptions);
        }

        // No secret anywhere: managed identity / workload identity / developer sign-in.
        return new BlobServiceClient(uri, (TokenCredential)new DefaultAzureCredential(), clientOptions);
    }
}
