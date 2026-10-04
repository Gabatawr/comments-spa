using System.Net;
using System.Text;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>Attachment pipeline: formats, limits, downscale, headers (docs/API.md 2.4, 2.6, 2.7).</summary>
public class AttachmentTests : IntegrationTestBase
{
    private static byte[] Txt(int bytes)
    {
        var data = new byte[bytes];
        for (var i = 0; i < bytes; i++)
        {
            data[i] = (byte)'a';
        }

        return data;
    }

    [Fact]
    public async Task Png_larger_than_320x240_is_downscaled_and_served_resized()
    {
        var comment = await CreateCommentAsync(
            "AttUser001", email: "att1@example.com",
            fileBytes: ImageFixtures.Png(640, 480), fileName: "big.png", fileContentType: "image/png");

        var attachment = comment["attachment"];
        Assert.NotNull(attachment);
        Assert.Equal("image", attachment!["kind"]!.GetValue<string>());
        Assert.True(attachment["width"]!.GetValue<int>() <= 320, "width must be <= 320");
        Assert.True(attachment["height"]!.GetValue<int>() <= 240, "height must be <= 240");
        Assert.Equal(320, attachment["width"]!.GetValue<int>());
        Assert.Equal(240, attachment["height"]!.GetValue<int>());
        Assert.NotNull(attachment["thumbUrl"]);

        var id = attachment["id"]!.GetValue<int>();
        var response = await Client.GetAsync($"/api/attachments/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var served = await response.Content.ReadAsByteArrayAsync();
        var (width, height) = ImageFixtures.Size(served);
        Assert.Equal(320, width);
        Assert.Equal(240, height);
    }

    [Theory]
    [InlineData(1000, 300, 320, 96)]
    [InlineData(300, 600, 120, 240)]
    [InlineData(321, 241, 320, 240)]
    [InlineData(640, 480, 320, 240)]
    public async Task Downscale_preserves_aspect_ratio(int width, int height, int expectedWidth, int expectedHeight)
    {
        var comment = await CreateCommentAsync(
            "AttRatio1", email: "ratio@example.com",
            fileBytes: ImageFixtures.Png(width, height), fileName: "r.png", fileContentType: "image/png");

        var attachment = comment["attachment"]!;
        Assert.InRange(attachment["width"]!.GetValue<int>(), expectedWidth - 2, expectedWidth + 2);
        Assert.InRange(attachment["height"]!.GetValue<int>(), expectedHeight - 2, expectedHeight + 2);
        Assert.True(attachment["width"]!.GetValue<int>() <= 320);
        Assert.True(attachment["height"]!.GetValue<int>() <= 240);
    }

    [Fact]
    public async Task Small_image_is_not_upscaled()
    {
        var comment = await CreateCommentAsync(
            "AttSmall1", email: "small@example.com",
            fileBytes: ImageFixtures.Png(100, 80), fileName: "s.png", fileContentType: "image/png");

        var attachment = comment["attachment"]!;
        Assert.Equal(100, attachment["width"]!.GetValue<int>());
        Assert.Equal(80, attachment["height"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    public async Task Accepted_image_formats(string extension, string contentType)
    {
        var comment = await CreateCommentAsync(
            "AttFmt01", email: "fmt@example.com",
            fileBytes: ImageFixtures.Save(320, 240, extension),
            fileName: $"a.{extension}",
            fileContentType: contentType);

        Assert.Equal("image", comment["attachment"]!["kind"]!.GetValue<string>());
        Assert.Equal(contentType, comment["attachment"]!["contentType"]!.GetValue<string>());
    }

    [Fact]
    public async Task Image_over_5mb_is_rejected()
    {
        var bytes = new byte[5 * 1024 * 1024 + 1];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;

        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "AttBig01", email: "big@example.com",
            fileBytes: bytes, fileName: "big.png", fileContentType: "image/png"));

        Assert.NotNull(errors["attachment"]);
    }

    [Fact]
    public async Task Real_png_over_5mb_is_rejected()
    {
        var bytes = ImageFixtures.RandomNoisePng(1600, 1200);
        Assert.True(bytes.Length > 5 * 1024 * 1024, $"fixture is only {bytes.Length} bytes; not a true >5MB image");

        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "AttBig02", email: "big2@example.com",
            fileBytes: bytes, fileName: "huge.png", fileContentType: "image/png"));

        Assert.NotNull(errors["attachment"]);
    }

    [Theory]
    [InlineData("bmp")]
    [InlineData("webp")]
    [InlineData("tiff")]
    public async Task Unsupported_image_formats_are_rejected(string extension)
    {
        // Real bytes of an unsupported format (not a PNG with a lying extension).
        var (bytes, contentType) = extension switch
        {
            "bmp" => (ImageFixtures.Bmp(100, 100), "image/bmp"),
            "tiff" => (ImageFixtures.Tiff(100, 100), "image/tiff"),
            _ => (ImageFixtures.Webp(), "image/webp"),
        };

        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "AttBad01", email: "bad@example.com",
            fileBytes: bytes, fileName: $"a.{extension}", fileContentType: contentType));

        Assert.NotNull(errors["attachment"]);
    }

    [Fact]
    public async Task Png_with_unsupported_declared_extension_is_rejected()
    {
        // The uploader enforces the declared extension as well as magic bytes: a real PNG
        // declared as .bmp is outside the JPG/JPEG/PNG/GIF allowlist and is rejected.
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "AttMagic1", email: "magic@example.com",
            fileBytes: ImageFixtures.Png(100, 100), fileName: "actually.png.bmp", fileContentType: "image/bmp"));

        Assert.NotNull(errors["attachment"]);
    }

    [Fact]
    public async Task Txt_under_limit_is_accepted_and_served_inline_as_utf8()
    {
        var comment = await CreateCommentAsync(
            "TxtUser01", email: "txt1@example.com",
            fileBytes: Encoding.UTF8.GetBytes("hello \u00e9\u00fc world"),
            fileName: "notes.txt",
            fileContentType: "text/plain");

        var attachment = comment["attachment"]!;
        Assert.Equal("text", attachment["kind"]!.GetValue<string>());
        Assert.True(attachment["width"] is null, "text attachment width must be null");
        Assert.True(attachment["height"] is null, "text attachment height must be null");
        Assert.True(attachment["thumbUrl"] is null, "text attachment thumbUrl must be null");

        var id = attachment["id"]!.GetValue<int>();
        var response = await Client.GetAsync($"/api/attachments/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("utf-8", response.Content.Headers.ContentType!.CharSet ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(
            response.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Contains("nosniff"),
            "X-Content-Type-Options: nosniff is required");
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Txt_at_exactly_100kb_is_accepted()
    {
        var comment = await CreateCommentAsync(
            "TxtBound1", email: "bound1@example.com",
            fileBytes: Txt(100 * 1024), fileName: "b.txt", fileContentType: "text/plain");

        Assert.Equal("text", comment["attachment"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task Txt_over_100kb_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "TxtBound2", email: "bound2@example.com",
            fileBytes: Txt(100 * 1024 + 1), fileName: "b.txt", fileContentType: "text/plain"));

        Assert.NotNull(errors["attachment"]);
    }

    [Theory]
    [InlineData("notes.pdf", "application/pdf", "%PDF-1.7 fake pdf")]
    [InlineData("archive.zip", "application/zip", "PK\u0003\u0004 fake zip")]
    [InlineData("page.html", "text/html", "<html><body>x</body></html>")]
    public async Task Non_txt_non_image_files_are_rejected(string fileName, string contentType, string content)
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "BadFile01", email: "badfile@example.com",
            fileBytes: Encoding.UTF8.GetBytes(content), fileName: fileName, fileContentType: contentType));

        Assert.NotNull(errors["attachment"]);
    }

    [Fact]
    public async Task Spoofed_content_type_with_binary_garbage_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(
            userName: "SpoofUser1", email: "spoof@example.com",
            fileBytes: Encoding.UTF8.GetBytes("this is definitely not a png"),
            fileName: "evil.png",
            fileContentType: "image/png"));

        Assert.NotNull(errors["attachment"]);
    }

    [Fact]
    public async Task Empty_attachment_is_rejected()
    {
        var response = await PostCommentRawAsync(
            userName: "EmptyAtt1", email: "empty@example.com",
            fileBytes: Array.Empty<byte>(), fileName: "x.png", fileContentType: "image/png");

        Assert.True(
            response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Created,
            $"empty file -> HTTP {(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var comment = UnwrapComment(await ReadJsonAsync(response));
            Assert.True(comment["attachment"] is null, "empty attachment must not be persisted");
        }
    }

    [Fact]
    public async Task Image_served_with_inline_and_nosniff_headers()
    {
        var comment = await CreateCommentAsync(
            "HdrUser01", email: "hdr@example.com",
            fileBytes: ImageFixtures.Png(100, 100), fileName: "p.png", fileContentType: "image/png");

        var id = comment["attachment"]!["id"]!.GetValue<int>();
        var response = await Client.GetAsync($"/api/attachments/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("image/", response.Content.Headers.ContentType!.MediaType!);
        Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var values) && values.Contains("nosniff"));
    }

    [Fact]
    public async Task Thumb_is_image_and_within_limits()
    {
        var comment = await CreateCommentAsync(
            "ThumbUser1", email: "thumb@example.com",
            fileBytes: ImageFixtures.Png(640, 480), fileName: "t.png", fileContentType: "image/png");

        var id = comment["attachment"]!["id"]!.GetValue<int>();
        var response = await Client.GetAsync($"/api/attachments/{id}/thumb");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var (width, height) = ImageFixtures.Size(await response.Content.ReadAsByteArrayAsync());
        Assert.True(width <= 320 && height <= 240, $"thumb is {width}x{height}");
    }

    [Fact]
    public async Task Thumb_for_txt_is_404()
    {
        var comment = await CreateCommentAsync(
            "ThumbTxt1", email: "thumbtxt@example.com",
            fileBytes: Encoding.UTF8.GetBytes("text"),
            fileName: "t.txt",
            fileContentType: "text/plain");

        var id = comment["attachment"]!["id"]!.GetValue<int>();
        var response = await Client.GetAsync($"/api/attachments/{id}/thumb");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_attachment_is_404()
    {
        var response = await Client.GetAsync("/api/attachments/999999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Comment_without_attachment_has_null_attachment()
    {
        var comment = await CreateCommentAsync("NoAttUser1", email: "noatt@example.com");
        Assert.True(comment["attachment"] is null);
    }
}
