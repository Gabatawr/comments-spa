using System.Net;
using System.Text;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>POST /api/preview contract (docs/API.md 2.5).</summary>
public class PreviewTests : IntegrationTestBase
{
    [Fact]
    public async Task Valid_text_returns_sanitized_html_and_plain()
    {
        var response = await PreviewAsync("<i>a</i> <strong>b</strong>");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var node = await ReadJsonAsync(response);
        Assert.True(node["valid"]!.GetValue<bool>());
        Assert.Empty(node["errors"]!.AsArray());
        Assert.Contains("<i>a</i>", node["html"]!.GetValue<string>());
        Assert.Contains("<strong>b</strong>", node["html"]!.GetValue<string>());
        Assert.Equal("a b", node["plain"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_text_returns_200_with_errors_and_safe_html()
    {
        var response = await PreviewAsync("<b>hi</b>");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var node = await ReadJsonAsync(response);
        Assert.False(node["valid"]!.GetValue<bool>());
        Assert.NotEmpty(node["errors"]!.AsArray());
        Assert.DoesNotContain("<b", node["html"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Script_is_neutralized_in_preview()
    {
        var response = await PreviewAsync("<script>alert(1)</script>");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var node = await ReadJsonAsync(response);
        Assert.DoesNotContain("<script", node["html"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.False(node["valid"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unclosed_tag_returns_errors()
    {
        var response = await PreviewAsync("<i>unclosed");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var node = await ReadJsonAsync(response);
        Assert.False(node["valid"]!.GetValue<bool>());
        Assert.NotEmpty(node["errors"]!.AsArray());
    }

    [Fact]
    public async Task Missing_text_field_is_handled()
    {
        var response = await Client.PostAsync(
            "/api/preview",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadRequest,
            $"/api/preview with {{}} -> HTTP {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Malformed_json_is_not_a_500()
    {
        var response = await Client.PostAsync(
            "/api/preview",
            new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.True(
            (int)response.StatusCode is >= 400 and < 500,
            $"/api/preview malformed JSON -> HTTP {(int)response.StatusCode}");
    }
}
