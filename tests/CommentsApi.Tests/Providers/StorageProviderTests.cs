using Xunit;
using Comments.Application.Abstractions.Storage;
using Comments.Application.Services;
using Comments.Infrastructure.Storage;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CommentsApi.Tests.Providers;

/// <summary>
/// The one behavioural difference between the storage providers: whether the web layer can take a
/// filesystem short cut. Object stores must answer <c>false</c> (and stream instead) — that is what
/// lets <c>/api/attachments/{id}</c> serve S3 and Azure bytes without knowing the provider name
/// (docs/API-v2.md §11).
/// </summary>
public class StorageProviderTests : IDisposable
{
    private const string AzureConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=testacct;AccountKey="
        + "dGVzdGtleXRlc3RrZXl0ZXN0a2V5dGVzdGtleXRlc3RrZXk=;EndpointSuffix=core.windows.net";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "comments-storage-tests", Guid.NewGuid().ToString("N"));

    private FileSystemStorage NewFileSystemStorage() => new(
        Options.Create(new AttachmentStorageOptions { Provider = "filesystem", Root = _root }),
        new TestHostEnvironment(),
        NullLogger<FileSystemStorage>.Instance);

    [Fact]
    public void Filesystem_hands_out_an_absolute_path_under_the_root()
    {
        var storage = NewFileSystemStorage();

        Assert.True(storage.TryGetLocalPath("abc.jpg", out var fullPath));
        Assert.Equal(Path.Combine(_root, "abc.jpg"), fullPath);
    }

    [Fact]
    public void Filesystem_blocks_path_traversal()
    {
        var storage = NewFileSystemStorage();

        Assert.Throws<InvalidOperationException>(() => storage.TryGetLocalPath("../../etc/passwd", out _));
    }

    [Fact]
    public async Task Filesystem_round_trips_content()
    {
        var storage = NewFileSystemStorage();
        var payload = "hello storage"u8.ToArray();

        await using (var content = new MemoryStream(payload))
        {
            Assert.Equal("round-trip.txt", await storage.SaveAsync("round-trip.txt", content));
        }

        Assert.True(storage.Exists("round-trip.txt"));

        await using var read = await storage.OpenReadAsync("round-trip.txt");
        Assert.NotNull(read);
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());

        Assert.True(await storage.DeleteAsync("round-trip.txt"));
        Assert.False(storage.Exists("round-trip.txt"));
    }

    [Fact]
    public void S3_reports_no_local_path()
    {
        IFileStorage storage = new S3FileStorage(
            Options.Create(new S3StorageOptions { Bucket = "comments-attachments" }),
            NullLogger<S3FileStorage>.Instance);

        Assert.Equal("s3", storage.Provider);
        Assert.False(storage.TryGetLocalPath("abc.jpg", out var fullPath));
        Assert.Empty(fullPath);
    }

    [Fact]
    public void Azure_blob_reports_no_local_path()
    {
        IFileStorage storage = new AzureBlobFileStorage(
            Options.Create(new AzureBlobStorageOptions
            {
                ConnectionString = AzureConnectionString,
                Container = "comments-attachments",
            }),
            NullLogger<AzureBlobFileStorage>.Instance);

        Assert.Equal("azureblob", storage.Provider);
        Assert.False(storage.TryGetLocalPath("abc.jpg", out var fullPath));
        Assert.Empty(fullPath);
    }

    [Fact]
    public void Azure_blob_requires_a_container()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new AzureBlobFileStorage(
            Options.Create(new AzureBlobStorageOptions { ConnectionString = AzureConnectionString }),
            NullLogger<AzureBlobFileStorage>.Instance));

        Assert.Contains("Storage:AzureBlob:Container", error.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort; the folder lives under the OS temp directory.
        }

        GC.SuppressFinalize(this);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "Comments.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
