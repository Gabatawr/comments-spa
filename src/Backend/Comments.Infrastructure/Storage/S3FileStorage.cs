using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Comments.Application.Abstractions.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Comments.Infrastructure.Storage;

/// <summary>
/// S3-compatible object storage settings (docs/API-v2.md §11).
/// Works against AWS S3, MinIO, Yandex Object Storage, Cloudflare R2 and DigitalOcean Spaces —
/// only <see cref="ServiceUrl"/> / <see cref="ForcePathStyle"/> change between them.
/// </summary>
public sealed class S3StorageOptions
{
    /// <summary>Custom endpoint for non-AWS S3, e.g. <c>http://minio:9000</c>. Empty = AWS.</summary>
    public string? ServiceUrl { get; set; }

    public string? Bucket { get; set; }

    /// <summary>Key prefix inside the bucket, e.g. <c>attachments/prod</c>.</summary>
    public string? Prefix { get; set; }

    public string? Region { get; set; } = "us-east-1";

    /// <summary>MinIO and most self-hosted gateways require path-style addressing.</summary>
    public bool ForcePathStyle { get; set; }

    public bool UseHttp { get; set; }

    /// <summary>Static credentials. Leave both empty in the cloud to use the default AWS chain
    /// (environment variables, shared profile, ECS task role, EC2 instance role).</summary>
    public string? AccessKey { get; set; }

    public string? SecretKey { get; set; }

    /// <summary>Per-request HTTP timeout. Kept short so an unreachable bucket cannot stall /api/health.</summary>
    public int TimeoutSeconds { get; set; } = 15;
}

/// <summary>
/// S3-compatible attachment storage. Selected with <c>Providers:Storage=s3</c>.
/// There is no local path, so <see cref="TryGetLocalPath"/> always answers <c>false</c> and the
/// web layer streams downloads through <see cref="OpenReadAsync"/>.
/// </summary>
public sealed class S3FileStorage : IFileStorage
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly ILogger<S3FileStorage> _logger;

    private readonly object _probeLock = new();
    private bool _lastProbe;
    private DateTime _lastProbeAt = DateTime.MinValue;

    public S3FileStorage(IOptions<S3StorageOptions> options, ILogger<S3FileStorage> logger)
    {
        var settings = options.Value;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(settings.Bucket))
        {
            throw new InvalidOperationException(
                "Providers:Storage=s3 requires Storage:S3:Bucket (docs/API-v2.md §11).");
        }

        _bucket = settings.Bucket.Trim();
        _prefix = settings.Prefix?.Trim().Trim('/') ?? string.Empty;
        _client = CreateClient(settings);

        _logger.LogInformation(
            "S3 storage: bucket={Bucket} prefix={Prefix} endpoint={Endpoint} pathStyle={PathStyle}",
            _bucket,
            string.IsNullOrEmpty(_prefix) ? "(root)" : _prefix,
            string.IsNullOrWhiteSpace(settings.ServiceUrl) ? "aws-default" : settings.ServiceUrl,
            settings.ForcePathStyle);
    }

    public string Provider => "s3";

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

                    _client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = _bucket,
                        MaxKeys = 1,
                        Prefix = string.IsNullOrEmpty(_prefix) ? null : _prefix + "/",
                    }, probeTimeout.Token).GetAwaiter().GetResult();

                    _lastProbe = true;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "S3 probe against bucket {Bucket} failed", _bucket);
                    _lastProbe = false;
                }

                _lastProbeAt = DateTime.UtcNow;
                return _lastProbe;
            }
        }
    }

    public async Task<string> SaveAsync(string storedName, Stream content, CancellationToken cancellationToken = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = BuildKey(storedName),
            InputStream = content,
            AutoCloseStream = false,
        };

        if (ContentTypeFor(storedName) is { } contentType)
        {
            request.ContentType = contentType;
        }

        await _client.PutObjectAsync(request, cancellationToken);
        return storedName;
    }

    public async Task<Stream?> OpenReadAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, BuildKey(storagePath), cancellationToken);

            // The AWS SDK response stream is not seekable and is tied to the response lifetime, so
            // buffer it: attachments are capped at 100 KB (docs/API-v2.md §4).
            var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            buffer.Position = 0;
            return buffer;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAsync(string storagePath, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucket,
                Key = BuildKey(storagePath),
            }, cancellationToken);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete S3 object {Key}", BuildKey(storagePath));
            return false;
        }
    }

    public bool Exists(string storagePath)
    {
        try
        {
            _client.GetObjectMetadataAsync(_bucket, BuildKey(storagePath)).GetAwaiter().GetResult();
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stat S3 object {Key}", BuildKey(storagePath));
            return false;
        }
    }

    /// <summary>Object stores have no local path — the caller must stream instead.</summary>
    public bool TryGetLocalPath(string storagePath, out string fullPath)
    {
        fullPath = string.Empty;
        return false;
    }

    private string BuildKey(string storagePath) =>
        string.IsNullOrEmpty(_prefix) ? storagePath : $"{_prefix}/{storagePath}";

    private static string? ContentTypeFor(string storedName) => Path.GetExtension(storedName).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".txt" => "text/plain; charset=utf-8",
        _ => null,
    };

    private static IAmazonS3 CreateClient(S3StorageOptions settings)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.ForcePathStyle,
            UseHttp = settings.UseHttp,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 300)),
        };

        if (!string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            config.ServiceURL = settings.ServiceUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(settings.Region))
        {
            config.AuthenticationRegion = settings.Region;

            // RegionEndpoint is only meaningful (and only harmless) for the real AWS endpoint.
            if (string.IsNullOrWhiteSpace(settings.ServiceUrl))
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
            }
        }

        // Empty credentials deliberately fall through to the SDK's default chain, which on AWS
        // resolves environment variables, the shared profile, or the ECS/EC2 instance role.
        return string.IsNullOrWhiteSpace(settings.AccessKey) || string.IsNullOrWhiteSpace(settings.SecretKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(settings.AccessKey, settings.SecretKey), config);
    }
}
