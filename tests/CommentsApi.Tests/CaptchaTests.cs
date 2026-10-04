using System.Net;
using CommentsApi.Tests.Infrastructure;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>CAPTCHA issuance, one-time consumption, TTL and charset (docs/API.md 2.1, 2.10).</summary>
public class CaptchaTests : IntegrationTestBase
{
    [Fact]
    public async Task Issue_returns_png_data_url_without_the_code()
    {
        var response = await Client.GetAsync("/api/captcha");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        var node = await ReadJsonAsync(response);

        var id = node["captchaId"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(id));

        var image = node["image"]?.GetValue<string>() ?? string.Empty;
        Assert.StartsWith("data:image/png;base64,", image);

        var bytes = Convert.FromBase64String(image["data:image/png;base64,".Length..]);
        Assert.True(bytes.Length > 100, $"CAPTCHA PNG is suspiciously small ({bytes.Length} bytes).");
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());

        Assert.Equal(300, node["expiresInSeconds"]?.GetValue<int>());
        Assert.DoesNotContain("\"code\"", raw);
    }

    [Fact]
    public async Task Code_is_6_chars_and_excludes_ambiguous_0_O_1_I()
    {
        var (_, code) = await NewCaptchaAsync();

        Assert.Equal(6, code.Length);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", code);
    }

    [Fact]
    public async Task Wrong_answer_is_rejected()
    {
        var (id, code) = await NewCaptchaAsync();
        var wrong = code[0] == 'A' ? "BBBBBB" : "AAAAAA";
        Assert.NotEqual(code, wrong);

        var errors = await ExpectValidationErrorAsync(
            await PostCommentRawAsync(captchaId: id, captchaAnswer: wrong));
        Assert.NotNull(errors["captcha"]);
    }

    [Fact]
    public async Task Reusing_a_captcha_id_is_rejected()
    {
        var (id, code) = await NewCaptchaAsync();

        var first = await PostCommentRawAsync(captchaId: id, captchaAnswer: code, userName: "ReuseUser1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostCommentRawAsync(captchaId: id, captchaAnswer: code, userName: "ReuseUser2");
        var errors = await ExpectValidationErrorAsync(second);
        Assert.NotNull(errors["captcha"]);
    }

    [Fact]
    public async Task Unknown_captcha_id_is_rejected()
    {
        var errors = await ExpectValidationErrorAsync(
            await PostCommentRawAsync(captchaId: Guid.NewGuid().ToString(), captchaAnswer: "ABCDEF"));
        Assert.NotNull(errors["captcha"]);
    }

    [Fact]
    public async Task Answer_comparison_is_case_insensitive()
    {
        var (id, code) = await NewCaptchaAsync();

        var response = await PostCommentRawAsync(
            captchaId: id,
            captchaAnswer: code.ToLowerInvariant(),
            userName: "CaseUser1");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Every_issued_captcha_has_a_distinct_id_and_solves_once()
    {
        var (id1, code1) = await NewCaptchaAsync();
        var (id2, code2) = await NewCaptchaAsync();

        Assert.NotEqual(id1, id2);
        Assert.NotEqual(code1, code2);

        var r1 = await PostCommentRawAsync(captchaId: id1, captchaAnswer: code1, userName: "CapUser01", email: "cap1@example.com");
        var r2 = await PostCommentRawAsync(captchaId: id2, captchaAnswer: code2, userName: "CapUser02", email: "cap2@example.com");
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
    }
}
