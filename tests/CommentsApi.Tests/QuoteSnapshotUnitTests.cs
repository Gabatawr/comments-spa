using Comments.Application.Services;
using Comments.Application.Validation;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>
/// Direct unit tests for the pure <see cref="QuoteSnapshot"/> helper
/// (docs/DESIGN-v2.1-decisions.md §1.1). No HTTP, no database.
/// </summary>
public class QuoteSnapshotUnitTests
{
    [Fact]
    public void Short_text_is_collapsed_trimmed_and_has_no_ellipsis()
    {
        var snapshot = QuoteSnapshot.FromPlainText("  Hello \n\t world   again  ");

        Assert.Equal("Hello world again", snapshot);
        Assert.DoesNotContain(QuoteSnapshot.Ellipsis, snapshot);
    }

    [Fact]
    public void Text_exactly_at_max_length_is_kept_verbatim()
    {
        var text = new string('a', QuoteSnapshot.MaxLength);

        var snapshot = QuoteSnapshot.FromPlainText(text);

        Assert.Equal(text, snapshot);
        Assert.Equal(160, snapshot!.Length);
        Assert.DoesNotContain(QuoteSnapshot.Ellipsis, snapshot);
    }

    [Fact]
    public void Longer_text_is_cut_on_a_word_boundary_and_ends_with_ellipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("word", 50));

        var snapshot = QuoteSnapshot.FromPlainText(text)!;

        Assert.EndsWith(QuoteSnapshot.Ellipsis, snapshot);
        Assert.True(snapshot.Length <= QuoteSnapshot.MaxLength + 1);

        // Everything before the ellipsis is a whole number of "word" tokens: the cut happened at a
        // space and the trailing space was dropped.
        var body = snapshot[..^1];
        Assert.False(body.EndsWith(' '));
        Assert.All(body.Split(' '), token => Assert.Equal("word", token));
        Assert.StartsWith(body, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_text_without_spaces_is_hard_cut_at_max_length()
    {
        var text = new string('x', 300);

        var snapshot = QuoteSnapshot.FromPlainText(text)!;

        Assert.Equal(new string('x', QuoteSnapshot.MaxLength) + QuoteSnapshot.Ellipsis, snapshot);
        Assert.Equal(QuoteSnapshot.MaxLength + 1, snapshot.Length);
    }

    [Fact]
    public void Whitespace_runs_are_collapsed_before_the_length_check()
    {
        var text = "a" + new string(' ', 500) + "b";

        var snapshot = QuoteSnapshot.FromPlainText(text);

        Assert.Equal("a b", snapshot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n ")]
    public void Blank_input_returns_null(string? input)
    {
        Assert.Null(QuoteSnapshot.FromPlainText(input));
    }

    [Fact]
    public void Snapshot_from_sanitised_script_payload_never_contains_markup()
    {
        var sanitizer = new HtmlSanitizer();
        var (_, plain, _) = sanitizer.Sanitize("<script>alert(1)</script>Hello");

        var snapshot = QuoteSnapshot.FromPlainText(plain);

        Assert.NotNull(snapshot);
        Assert.DoesNotContain("<script", snapshot, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", snapshot);
        Assert.DoesNotContain(">", snapshot);
    }

    [Fact]
    public void Snapshot_from_sanitised_img_onerror_payload_has_no_payload()
    {
        var sanitizer = new HtmlSanitizer();
        var (_, plain, _) = sanitizer.Sanitize("<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img", plain, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", plain, StringComparison.OrdinalIgnoreCase);

        var snapshot = QuoteSnapshot.FromPlainText(plain);

        // The neutralised tag contributes no plain text and its attributes are dropped.
        Assert.Equal(string.Empty, plain);
        Assert.Null(snapshot);
        Assert.DoesNotContain("<img", snapshot ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Snapshot_from_allowed_markup_contains_only_the_text()
    {
        var sanitizer = new HtmlSanitizer();
        var (_, plain, errors) = sanitizer.Sanitize("<strong>жирный</strong>");

        Assert.Empty(errors);

        var snapshot = QuoteSnapshot.FromPlainText(plain);

        Assert.Equal("жирный", snapshot);
        Assert.DoesNotContain("<", snapshot);
        Assert.DoesNotContain(">", snapshot);
    }
}
