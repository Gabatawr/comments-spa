using System.Net;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// Sanitizer behaviour, exercised through the public POST /api/preview endpoint so the tests
/// stay decoupled from backend namespaces. Covers the allowlist, tag closing / XHTML validity
/// and dangerous URL schemes.
/// </summary>
public class SanitizerTests : IntegrationTestBase
{
    private async Task<(bool Valid, string Html, string Plain, List<string> Errors)> Preview(string text)
    {
        var response = await PreviewAsync(text);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var node = await ReadJsonAsync(response);
        var errors = node["errors"]?.AsArray().Select(e => e?.GetValue<string>() ?? string.Empty).ToList()
                     ?? new List<string>();
        return (
            node["valid"]?.GetValue<bool>() ?? false,
            node["html"]?.GetValue<string>() ?? string.Empty,
            node["plain"]?.GetValue<string>() ?? string.Empty,
            errors);
    }

    [Fact]
    public async Task Allowed_tags_are_kept()
    {
        var (valid, html, _, errors) = await Preview(
            "<i>a</i> <strong>b</strong> <code>c</code> <a href=\"https://example.com\" title=\"t\">d</a>");

        Assert.True(valid, string.Join("; ", errors));
        Assert.Contains("<i>a</i>", html);
        Assert.Contains("<strong>b</strong>", html);
        Assert.Contains("<code>c</code>", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("title=\"t\"", html);
    }

    [Fact]
    public async Task Script_tag_is_not_preserved()
    {
        var (_, html, plain, _) = await Preview("<script>alert(1)</script><i>x</i>");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("</script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", plain, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<i>x</i>", html);
    }

    [Fact]
    public async Task Img_and_event_handler_are_not_preserved()
    {
        var (_, html, _, _) = await Preview("<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bold_is_not_an_allowed_element()
    {
        var (valid, html, _, errors) = await Preview("<b>hi</b>");

        Assert.False(valid);
        Assert.NotEmpty(errors);
        Assert.DoesNotContain("<b", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Only_href_and_title_attributes_survive_on_a()
    {
        var (_, html, _, _) = await Preview(
            "<a href=\"https://ok.example\" title=\"t\" onclick=\"alert(1)\" style=\"color:red\" data-x=\"1\">z</a>");

        Assert.Contains("href=\"https://ok.example\"", html);
        Assert.Contains("title=\"t\"", html);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-x", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<a href=\"   javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\"java&#115;cript:alert(1)\">x</a>")]
    [InlineData("<a href=\"javascript&#58;alert(1)\">x</a>")]
    [InlineData("<a href=\"\tjavascript:alert(1)\">x</a>")]
    public async Task Javascript_href_is_stripped(string payload)
    {
        var (_, html, _, _) = await Preview(payload);

        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>")]
    [InlineData("<a href=\"DATA:image/svg+xml,<svg onload=alert(1)>\">x</a>")]
    public async Task Data_href_is_stripped(string payload)
    {
        var (_, html, _, _) = await Preview(payload);

        Assert.DoesNotContain("data:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<i>unclosed")]
    [InlineData("<strong>a</i>")]
    [InlineData("text</code>")]
    [InlineData("<a href=\"https://x.example\">unclosed")]
    [InlineData("<strong><i>x</strong></i>")]
    public async Task Unclosed_or_mismatched_tags_are_reported(string payload)
    {
        var (valid, _, _, errors) = await Preview(payload);

        Assert.False(valid);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task Output_is_valid_xhtml()
    {
        var (valid, html, _, errors) = await Preview(
            "<i>a</i> & <strong>b</strong> <a href=\"https://e.example\" title=\"t\">c</a>");

        Assert.True(valid, string.Join("; ", errors));
        // Raw '&' must be serialized as &amp; for the fragment to be well-formed XHTML.
        var document = XDocument.Parse($"<root xmlns=\"http://www.w3.org/1999/xhtml\">{html}</root>");
        Assert.NotNull(document.Root);
    }

    [Theory]
    [InlineData("<a href=x>y</a>")]
    [InlineData("<A HREF=\"https://e.example\">y</A>")]
    [InlineData("<strong/>")]
    [InlineData("</p>")]
    [InlineData("<i><strong>x</i></strong>")]
    [InlineData("<code><i>a</strong></code>")]
    public async Task Malformed_input_never_breaks_the_endpoint(string payload)
    {
        var response = await PreviewAsync(payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var node = await ReadJsonAsync(response);
        var html = node["html"]?.GetValue<string>() ?? string.Empty;
        var valid = node["valid"]?.GetValue<bool>() ?? false;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        if (valid)
        {
            XDocument.Parse($"<root>{html}</root>");
        }
    }

    [Fact]
    public async Task Empty_text_is_invalid()
    {
        var (valid, _, _, errors) = await Preview(string.Empty);

        Assert.False(valid);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task Plain_text_contains_no_markup()
    {
        var (_, _, plain, _) = await Preview("<strong>Hello</strong> &amp; <i>welcome</i>");

        Assert.Equal("Hello & welcome", plain);
    }
}
