using CommentsApi.Validation;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>Direct unit tests for the pure <see cref="CommentValidator"/>.</summary>
public class CommentValidatorUnitTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UserName_required(string? value)
    {
        Assert.Equal(CommentValidator.ErrorUserNameRequired, CommentValidator.ValidateUserName(value));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    public void UserName_too_short(string value)
    {
        Assert.Equal(CommentValidator.ErrorUserNameTooShort, CommentValidator.ValidateUserName(value));
    }

    [Fact]
    public void UserName_too_long()
    {
        Assert.Equal(CommentValidator.ErrorUserNameTooLong, CommentValidator.ValidateUserName(new string('a', 51)));
    }

    [Theory]
    [InlineData("Иван")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("a_b")]
    [InlineData("a.b")]
    [InlineData("a@b")]
    [InlineData("naïve")]
    [InlineData("a\tb")]
    public void UserName_charset_rejected(string value)
    {
        Assert.Equal(CommentValidator.ErrorUserNameCharset, CommentValidator.ValidateUserName(value));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("ABC123")]
    [InlineData("A1B2C3")]
    [InlineData("0123456789012345678901234567890123456789012345678")] // 49 chars
    public void UserName_valid(string value)
    {
        Assert.Null(CommentValidator.ValidateUserName(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Email_required(string? value)
    {
        Assert.Equal(CommentValidator.ErrorEmailRequired, CommentValidator.ValidateEmail(value));
    }

    [Fact]
    public void Email_too_long()
    {
        var value = new string('a', 95) + "@example.com";
        Assert.Equal(CommentValidator.ErrorEmailTooLong, CommentValidator.ValidateEmail(value));
    }

    [Theory]
    [InlineData("plainaddress")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("@missing-local.com")]
    [InlineData("spaces in@example.com")]
    [InlineData("a@b@c.com")]
    [InlineData("trailing@")]
    public void Email_invalid(string value)
    {
        Assert.Equal(CommentValidator.ErrorEmailInvalid, CommentValidator.ValidateEmail(value));
    }

    [Theory]
    [InlineData("qa.user@example.com")]
    [InlineData("qa+tag@example.co.uk")]
    [InlineData("A_B-c@sub.example.org")]
    public void Email_valid(string value)
    {
        Assert.Null(CommentValidator.ValidateEmail(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void HomePage_optional_empty_becomes_null(string? value)
    {
        Assert.Null(CommentValidator.ValidateHomePage(value, out var normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void HomePage_too_long()
    {
        var value = "https://example.com/" + new string('a', 200);
        Assert.Equal(CommentValidator.ErrorHomePageTooLong, CommentValidator.ValidateHomePage(value, out _));
    }

    [Theory]
    [InlineData("notaurl")]
    [InlineData("ftp://example.com")]
    [InlineData("//example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://")]
    [InlineData("mailto:a@b.com")]
    public void HomePage_invalid(string value)
    {
        Assert.Equal(CommentValidator.ErrorHomePageInvalid, CommentValidator.ValidateHomePage(value, out var normalized));
        Assert.Null(normalized);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/path?q=1#frag")]
    [InlineData("https://sub.example.co.uk:8443/x")]
    public void HomePage_valid(string value)
    {
        Assert.Null(CommentValidator.ValidateHomePage(value, out var normalized));
        Assert.Equal(value, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_required(string? value)
    {
        Assert.Equal(HtmlSanitizer.ErrorTextRequired, CommentValidator.ValidateTextLength(value));
    }

    [Fact]
    public void Text_too_long()
    {
        Assert.Equal(HtmlSanitizer.ErrorTextTooLong, CommentValidator.ValidateTextLength(new string('a', 5001)));
    }

    [Fact]
    public void Text_at_max_length_is_valid()
    {
        Assert.Null(CommentValidator.ValidateTextLength(new string('a', 5000)));
    }
}
