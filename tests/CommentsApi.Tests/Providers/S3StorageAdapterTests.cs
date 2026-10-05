using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using Xunit;
using System.Text;
using Comments.Application.Abstractions.Storage;
using Comments.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CommentsApi.Tests.Providers;

/// <summary>
/// Drives the real <see cref="S3FileStorage"/> adapter — the actual AWS SDK, the actual SigV4
/// signing and path-style URL construction — against a tiny in-process S3 endpoint. No container
/// and no credentials, so this runs anywhere and proves the adapter (not just the DI switch):
/// round-trip, prefixing, content type, "missing key → null", delete and reachability.
/// </summary>
public class S3StorageAdapterTests : IAsyncLifetime
{
    private const string Bucket = "comments-attachments";
    private const string Prefix = "attachments/test";

    private S3StubServer _server = null!;
    private S3FileStorage _storage = null!;

    public async Task InitializeAsync()
    {
        _server = await S3StubServer.StartAsync();
        _storage = new S3FileStorage(
            Options.Create(new S3StorageOptions
            {
                ServiceUrl = _server.ServiceUrl,
                Bucket = Bucket,
                Prefix = Prefix,
                Region = "us-east-1",
                ForcePathStyle = true,
                UseHttp = true,
                AccessKey = "test-access-key",
                SecretKey = "test-secret-key",
                TimeoutSeconds = 10,
            }),
            NullLogger<S3FileStorage>.Instance);
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public void Adapter_reports_no_local_path()
    {
        Assert.Equal("s3", _storage.Provider);
        Assert.False(_storage.TryGetLocalPath("abc.png", out var fullPath));
        Assert.Empty(fullPath);
    }

    [Fact]
    public async Task Round_trip_stores_under_the_configured_prefix()
    {
        var payload = Encoding.UTF8.GetBytes("attachment bytes");

        await using (var content = new MemoryStream(payload))
        {
            Assert.Equal("abc.txt", await _storage.SaveAsync("abc.txt", content));
        }

        // The adapter must place the object under the prefix — that is what keeps one bucket
        // usable by several environments.
        var stored = Assert.Single(_server.Objects);
        Assert.Equal($"{Prefix}/abc.txt", stored.Key);

        Assert.True(_storage.Exists("abc.txt"));

        await using var read = await _storage.OpenReadAsync("abc.txt");
        Assert.NotNull(read);
        using var buffer = new MemoryStream();
        await read!.CopyToAsync(buffer);
        Assert.Equal(payload, buffer.ToArray());
    }

    [Fact]
    public async Task Content_type_is_taken_from_the_stored_name()
    {
        await using (var content = new MemoryStream([1, 2, 3]))
        {
            await _storage.SaveAsync("picture.png", content);
        }

        Assert.Equal("image/png", Assert.Single(_server.Objects).Value.ContentType);
    }

    [Fact]
    public async Task Missing_key_is_null_not_an_exception()
    {
        Assert.Null(await _storage.OpenReadAsync("nope.png"));
        Assert.False(_storage.Exists("nope.png"));
    }

    [Fact]
    public async Task Delete_removes_the_object()
    {
        await using (var content = new MemoryStream([1, 2, 3]))
        {
            await _storage.SaveAsync("gone.txt", content);
        }

        Assert.True(await _storage.DeleteAsync("gone.txt"));
        Assert.False(_storage.Exists("gone.txt"));
        Assert.Empty(_server.Objects);
    }

    [Fact]
    public void Reachability_probe_reports_ok_against_a_healthy_endpoint()
    {
        Assert.True(_storage.IsAvailable);
    }

    [Fact]
    public async Task Unreachable_endpoint_is_reported_as_unavailable_not_thrown()
    {
        // Same adapter, an endpoint that is not listening: health must degrade, never crash.
        var storage = new S3FileStorage(
            Options.Create(new S3StorageOptions
            {
                ServiceUrl = $"http://127.0.0.1:{FreePort()}",
                Bucket = Bucket,
                ForcePathStyle = true,
                UseHttp = true,
                AccessKey = "k",
                SecretKey = "s",
                TimeoutSeconds = 2,
            }),
            NullLogger<S3FileStorage>.Instance);

        Assert.False(storage.IsAvailable);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>
/// The smallest thing that speaks enough S3 for the adapter: path-style PUT/GET/HEAD/DELETE plus a
/// ListObjectsV2 response (<c>IsAvailable</c> probes with a one-key list). Deliberately dumb — the
/// point is to exercise the client, not to reimplement S3.
/// </summary>
internal sealed class S3StubServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

    private S3StubServer(WebApplication app)
    {
        _app = app;
    }

    /// <summary>Set once the listener is bound, so the port is known.</summary>
    public string ServiceUrl { get; private set; } = string.Empty;

    public IReadOnlyDictionary<string, StoredObject> Objects => _objects;

    public static async Task<S3StubServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        var server = new S3StubServer(app);

        app.MapPut("/{bucket}/{**key}", async (HttpContext context, string bucket, string key) =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            var raw = buffer.ToArray();

            // AWSSDK v4 sends uploads with the S3 `aws-chunked` framing (default integrity
            // protections), so the wire body is chunked even for a small seekable payload.
            var body = context.Request.Headers.ContainsKey("x-amz-decoded-content-length")
                ? DecodeAwsChunked(raw)
                : raw;

            server._objects[key] = new StoredObject(
                body,
                context.Request.ContentType ?? "application/octet-stream");

            context.Response.Headers.ETag = "\"stub-etag\"";
            return Results.Ok();
        });

        app.MapGet("/{bucket}/{**key}", (string bucket, string key) =>
            server._objects.TryGetValue(key, out var stored)
                ? Results.Bytes(stored.Body, stored.ContentType)
                : NotFound());

        app.MapMethods("/{bucket}/{**key}", ["HEAD"], (string bucket, string key) =>
            server._objects.ContainsKey(key) ? Results.Ok() : NotFound());

        app.MapDelete("/{bucket}/{**key}", (string bucket, string key) =>
            server._objects.TryRemove(key, out _) ? Results.NoContent() : NotFound());

        app.MapGet("/{bucket}", (string bucket) => Results.Content(
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
               <Name>{bucket}</Name>
               <Prefix></Prefix>
               <KeyCount>0</KeyCount>
               <MaxKeys>1000</MaxKeys>
               <IsTruncated>false</IsTruncated>
             </ListBucketResult>
             """,
            "application/xml"));

        await app.StartAsync();

        var bound = new Uri(app.Urls.First());
        server.ServiceUrl = $"http://127.0.0.1:{bound.Port}";
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// Strips the <c>aws-chunked</c> framing:
    /// <c>&lt;hex-size&gt;[;chunk-signature=…]\r\n&lt;data&gt;\r\n</c> repeated, terminated by a
    /// zero-size chunk followed by trailers.
    /// </summary>
    private static byte[] DecodeAwsChunked(byte[] raw)
    {
        using var source = new MemoryStream(raw);
        using var result = new MemoryStream();

        while (true)
        {
            var header = ReadLine(source);
            if (header is null)
            {
                break;
            }

            var sizePart = header.Split(';', 2)[0].Trim();
            if (sizePart.Length == 0
                || !int.TryParse(sizePart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size)
                || size <= 0)
            {
                break;
            }

            var chunk = new byte[size];
            var read = 0;
            while (read < size)
            {
                var count = source.Read(chunk, read, size - read);
                if (count <= 0)
                {
                    break;
                }

                read += count;
            }

            result.Write(chunk, 0, read);
            SkipCrlf(source);
        }

        return result.ToArray();
    }

    private static string? ReadLine(Stream stream)
    {
        var line = new StringBuilder();
        var any = false;

        int current;
        while ((current = stream.ReadByte()) >= 0)
        {
            any = true;
            if (current == '\n')
            {
                return line.ToString().TrimEnd('\r');
            }

            line.Append((char)current);
        }

        return any ? line.ToString() : null;
    }

    private static void SkipCrlf(Stream stream)
    {
        var current = stream.ReadByte();
        if (current == '\r')
        {
            stream.ReadByte();
        }
        else if (current >= 0)
        {
            stream.Position--;
        }
    }

    private static IResult NotFound() => Results.Content(
        """
        <?xml version="1.0" encoding="UTF-8"?>
        <Error><Code>NoSuchKey</Code><Message>The specified key does not exist.</Message></Error>
        """,
        "application/xml",
        statusCode: StatusCodes.Status404NotFound);

    internal sealed record StoredObject(byte[] Body, string ContentType);
}
