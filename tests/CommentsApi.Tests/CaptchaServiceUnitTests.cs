using Comments.Application.Services;
using CommentsApi.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CommentsApi.Tests;

/// <summary>Direct unit tests for <see cref="ICaptchaService"/> resolved from the app's DI.</summary>
public class CaptchaServiceUnitTests : IntegrationTestBase
{
    private ICaptchaService Captcha => Factory.Services.GetRequiredService<ICaptchaService>();

    [Fact]
    public void Generate_returns_png_data_url_and_never_exposes_the_code_in_the_challenge()
    {
        var challenge = Captcha.Generate();

        Assert.False(string.IsNullOrWhiteSpace(challenge.CaptchaId));
        Assert.StartsWith("data:image/png;base64,", challenge.Image);
        Assert.Equal(300, challenge.ExpiresInSeconds);

        var bytes = Convert.FromBase64String(challenge.Image["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
        Assert.True(bytes.Length > 100);

        // The only way to obtain the code is the internal peek hook.
        var code = Captcha.PeekAnswer(challenge.CaptchaId);
        Assert.NotNull(code);
        Assert.Equal(6, code!.Length);
        Assert.Matches("^[A-HJ-NP-Z2-9]{6}$", code);
    }

    [Fact]
    public void Generated_codes_are_distinct()
    {
        var first = Captcha.Generate();
        var second = Captcha.Generate();

        Assert.NotEqual(first.CaptchaId, second.CaptchaId);
        Assert.NotEqual(Captcha.PeekAnswer(first.CaptchaId), Captcha.PeekAnswer(second.CaptchaId));
    }

    [Fact]
    public void Validate_accepts_the_correct_answer_case_insensitively_and_consumes_it()
    {
        var challenge = Captcha.Generate();
        var code = Captcha.PeekAnswer(challenge.CaptchaId)!;

        Assert.True(Captcha.Validate(challenge.CaptchaId, code.ToLowerInvariant()));
        Assert.False(Captcha.Validate(challenge.CaptchaId, code));
    }

    [Fact]
    public void Validate_rejects_a_wrong_answer_and_still_consumes_the_challenge()
    {
        var challenge = Captcha.Generate();
        var code = Captcha.PeekAnswer(challenge.CaptchaId)!;
        var wrong = code[0] == 'A' ? "BBBBBB" : "AAAAAA";

        Assert.False(Captcha.Validate(challenge.CaptchaId, wrong));
        Assert.False(Captcha.Validate(challenge.CaptchaId, code));
    }

    [Theory]
    [InlineData(null, "ABCDEF")]
    [InlineData("", "ABCDEF")]
    [InlineData("not-a-guid", "ABCDEF")]
    [InlineData("6f1c1e0e-6f2a-4f2e-9d0a-2b6c9f0f2a11", null)]
    [InlineData("6f1c1e0e-6f2a-4f2e-9d0a-2b6c9f0f2a11", "")]
    public void Validate_rejects_missing_or_unknown_identifiers(string? id, string? answer)
    {
        Assert.False(Captcha.Validate(id, answer));
    }

    [Fact]
    public void PeekAnswer_returns_null_for_unknown_id()
    {
        Assert.Null(Captcha.PeekAnswer(Guid.NewGuid().ToString()));
        Assert.Null(Captcha.PeekAnswer(null));
    }
}
