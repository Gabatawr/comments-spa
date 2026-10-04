using System.Net;
using System.Text.Json.Nodes;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>Server-side field validation per docs/API.md section 2.4 (error keys).</summary>
public class ValidationTests : IntegrationTestBase
{
    [Fact]
    public async Task Missing_userName_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(omitUserName: true));
        Assert.NotNull(errors["userName"]);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Иван")]
    [InlineData("a b")]
    [InlineData("a-b")]
    [InlineData("a_b")]
    [InlineData("a.b")]
    [InlineData("name@x")]
    [InlineData("naïve")]
    [InlineData("a\n")]
    public async Task Invalid_userName_is_rejected(string value)
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(userName: value));
        Assert.NotNull(errors["userName"]);
    }

    [Fact]
    public async Task UserName_longer_than_50_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(userName: new string('a', 51)));
        Assert.NotNull(errors["userName"]);
    }

    [Theory]
    [InlineData("Abc")]          // 3 chars, boundary
    [InlineData("QaUser42")]
    [InlineData("A1B2C3")]
    public async Task Valid_userName_is_accepted(string value)
    {
        var response = await PostCommentRawAsync(userName: value, email: $"{value.ToLowerInvariant()}@example.com");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Missing_email_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(omitEmail: true));
        Assert.NotNull(errors["email"]);
    }

    [Theory]
    [InlineData("plainaddress")]
    [InlineData("no-at-sign.example.com")]
    [InlineData("@missing-local.com")]
    [InlineData("spaces in@example.com")]
    [InlineData("a@b@c.com")]
    [InlineData("<script>alert(1)</script>@x.com")]
    public async Task Invalid_email_is_rejected(string value)
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(email: value));
        Assert.NotNull(errors["email"]);
    }

    [Fact]
    public async Task Email_longer_than_100_is_rejected()
    {
        var value = new string('a', 95) + "@example.com"; // 107 chars
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(email: value));
        Assert.NotNull(errors["email"]);
    }

    [Theory]
    [InlineData("qa.user@example.com")]
    [InlineData("qa+tag@example.co.uk")]
    public async Task Valid_email_is_accepted(string value)
    {
        var response = await PostCommentRawAsync(userName: "EmailUser1", email: value);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("notaurl")]
    [InlineData("ftp://example.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//example.com")]
    [InlineData("http://")]
    public async Task Invalid_homePage_is_rejected(string value)
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(homePage: value));
        Assert.NotNull(errors["homePage"]);
    }

    [Fact]
    public async Task HomePage_longer_than_200_is_rejected()
    {
        var value = "https://example.com/" + new string('a', 200); // > 200
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(homePage: value));
        Assert.NotNull(errors["homePage"]);
    }

    [Fact]
    public async Task Empty_homePage_becomes_null()
    {
        var comment = await CreateCommentAsync("HpUser01", homePage: string.Empty);
        Assert.True(comment["homePage"] is null, "empty homePage should be persisted as null");
    }

    [Fact]
    public async Task Valid_homePage_is_accepted()
    {
        var comment = await CreateCommentAsync("HpUser02", homePage: "https://example.com/path?q=1");
        Assert.Equal("https://example.com/path?q=1", comment["homePage"]?.GetValue<string>());
    }

    [Fact]
    public async Task Missing_text_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(omitText: true));
        Assert.NotNull(errors["text"]);
    }

    [Fact]
    public async Task Empty_text_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(text: string.Empty));
        Assert.NotNull(errors["text"]);
    }

    [Fact]
    public async Task Text_of_5001_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(text: new string('a', 5001)));
        Assert.NotNull(errors["text"]);
    }

    [Fact]
    public async Task Text_of_5000_is_accepted()
    {
        var response = await PostCommentRawAsync(userName: "Text5000", text: new string('a', 5000));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Missing_captcha_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(omitCaptcha: true));
        Assert.NotNull(errors["captcha"]);
    }

    [Fact]
    public async Task Nonexistent_parentId_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(parentId: "999999"));
        Assert.NotNull(errors["parentId"]);
    }

    [Fact]
    public async Task NonNumeric_parentId_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(await PostCommentRawAsync(parentId: "abc"));
        Assert.NotNull(errors["parentId"]);
    }
}
