using System.Xml.Linq;
using CommentsApi.Validation;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// Direct unit tests for the pure <see cref="HtmlSanitizer"/> (no HTTP). Complements the
/// endpoint-level SanitizerTests that exercise the same code through POST /api/preview.
/// </summary>
public class HtmlSanitizerUnitTests
{
    private static readonly HtmlSanitizer Sanitizer = new();

    private static (string Html, string Plain, IReadOnlyList<string> Errors) Run(string? input)
        => Sanitizer.Sanitize(input);

    [Fact]
    public void Allowed_tags_are_kept_without_errors()
    {
        var (html, _, errors) = Run("<i>a</i> <strong>b</strong> <code>c</code> <a href=\"https://e.example\" title=\"t\">d</a>");

        Assert.Empty(errors);
        Assert.Contains("<i>a</i>", html);
        Assert.Contains("<strong>b</strong>", html);
        Assert.Contains("<code>c</code>", html);
        Assert.Contains("<a href=\"https://e.example\" title=\"t\">d</a>", html);
    }

    [Fact]
    public void B_is_aliased_to_strong_and_reported()
    {
        var (html, _, errors) = Run("<b>hi</b>");

        Assert.Contains("<strong>hi</strong>", html);
        Assert.DoesNotContain("<b", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(errors, e => e.Contains("b", StringComparison.OrdinalIgnoreCase) && e.Contains("not allowed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Em_is_aliased_to_i_and_reported()
    {
        var (html, _, errors) = Run("<em>x</em>");

        Assert.Contains("<i>x</i>", html);
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("<iframe src=\"https://evil\"></iframe>")]
    [InlineData("<style>body{background:red}</style>")]
    [InlineData("<svg onload=alert(1)>")]
    public void Disallowed_elements_are_escaped(string payload)
    {
        var (html, _, errors) = Run(payload);

        Assert.NotEmpty(errors);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<svg", html, StringComparison.OrdinalIgnoreCase);
        // Approved policy: a disallowed tag is emitted as the escaped tag NAME only,
        // with every attribute dropped (so no event handlers survive even as text).
        Assert.Contains("&lt;", html);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onload", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disallowed_attributes_are_dropped_and_reported()
    {
        var (html, _, errors) = Run(
            "<a href=\"https://ok.example\" title=\"t\" onclick=\"alert(1)\" style=\"color:red\" data-x=\"1\">z</a>");

        Assert.Contains("href=\"https://ok.example\"", html);
        Assert.Contains("title=\"t\"", html);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-x", html, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java&#115;cript:alert(1)")]
    [InlineData("javascript&#58;alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("DATA:image/svg+xml,<svg onload=alert(1)>")]
    [InlineData("vbscript:msgbox(1)")]
    public void Dangerous_href_schemes_are_dropped(string href)
    {
        var (html, _, errors) = Run($"<a href=\"{href}\">x</a>");

        Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Safe_relative_and_http_hrefs_are_kept()
    {
        var (html, _, errors) = Run("<a href=\"https://example.com/path?q=1\">x</a>");

        Assert.Empty(errors);
        Assert.Contains("href=\"https://example.com/path?q=1\"", html);
    }

    [Fact]
    public void Unclosed_tag_is_auto_closed_and_reported()
    {
        var (html, _, errors) = Run("<i>unclosed");

        Assert.Equal("<i>unclosed</i>", html);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Mismatched_tags_are_reported()
    {
        var (html, _, errors) = Run("<strong>a</i>");

        Assert.NotEmpty(errors);
        Assert.Contains("<strong>a", html);
        var document = XDocument.Parse($"<root>{html}</root>");
        Assert.NotNull(document.Root);
    }

    [Fact]
    public void Unmatched_closing_tag_is_escaped_and_reported()
    {
        var (html, _, errors) = Run("</strong>");

        Assert.NotEmpty(errors);
        Assert.DoesNotContain("</strong>", html);
    }

    [Fact]
    public void Bare_less_than_is_escaped()
    {
        var (html, plain, _) = Run("a < b");

        Assert.Equal("a &lt; b", html);
        Assert.Equal("a < b", plain);
    }

    [Fact]
    public void Ampersand_is_encoded_for_valid_xhtml()
    {
        var (html, _, errors) = Run("<i>a</i> & <strong>b</strong>");

        Assert.Empty(errors);
        Assert.Contains("&amp;", html);
        var document = XDocument.Parse($"<root xmlns=\"http://www.w3.org/1999/xhtml\">{html}</root>");
        Assert.NotNull(document.Root);
    }

    [Theory]
    [InlineData("<strong/>")]
    [InlineData("<strong />")]
    [InlineData("<a href=\"https://e.example\"/>")]
    public void Self_closing_allowed_tags_produce_valid_pairs(string payload)
    {
        var (html, _, errors) = Run(payload);

        var document = XDocument.Parse($"<root xmlns=\"http://www.w3.org/1999/xhtml\">{html}</root>");
        Assert.NotNull(document.Root);
        Assert.DoesNotContain("/>", html);
    }

    [Fact]
    public void Html_comments_are_dropped()
    {
        var (html, plain, _) = Run("a<!-- <script>alert(1)</script> -->b");

        Assert.Equal("ab", html);
        Assert.Equal("ab", plain);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plain_text_is_html_decoded_and_tag_free()
    {
        var (_, plain, _) = Run("<strong>Hello</strong> &amp; <i>welcome</i>");

        Assert.Equal("Hello & welcome", plain);
    }

    [Fact]
    public void Null_and_empty_input_are_safe()
    {
        Assert.Equal(string.Empty, Run(null).Html);
        Assert.Equal(string.Empty, Run(string.Empty).Html);
        Assert.Empty(Run(null).Errors);
        Assert.Empty(Run(string.Empty).Errors);
    }

    [Fact]
    public void SanitizeAndValidate_reports_required_and_too_long()
    {
        var required = Sanitizer.SanitizeAndValidate(string.Empty);
        Assert.Contains(required.errors, e => e.Contains("required", StringComparison.OrdinalIgnoreCase));

        var tooLong = Sanitizer.SanitizeAndValidate(new string('a', HtmlSanitizer.MaxTextLength + 1));
        Assert.Contains(tooLong.errors, e => e.Contains("5000", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("/relative", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("vbscript:x", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void IsSafeHref_matches_the_allowlist(string href, bool expected)
    {
        Assert.Equal(expected, HtmlSanitizer.IsSafeHref(href));
    }

    [Fact]
    public void Every_allowed_construct_still_produces_well_formed_xhtml()
    {
        var (html, _, _) = Run(
            "<i>a</i><strong>b</strong><code>c &amp;&amp; d</code><a href=\"https://e.example\" title=\"q &amp; a\">e</a>");

        var document = XDocument.Parse($"<root>{html}</root>");
        Assert.NotNull(document.Root);
    }
}
