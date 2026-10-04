namespace Comments.Application.Services;

/// <summary>CAPTCHA challenge returned by <see cref="ICaptchaService.Generate"/>.</summary>
public sealed class CaptchaChallenge
{
    public string CaptchaId { get; init; } = string.Empty;

    /// <summary>PNG data URL (<c>data:image/png;base64,...</c>).</summary>
    public string Image { get; init; } = string.Empty;

    public int ExpiresInSeconds { get; init; } = 300;

    /// <summary>Never serialised / returned to clients — used only inside the API process.</summary>
    internal string Code { get; init; } = string.Empty;
}

/// <summary>
/// CAPTCHA service contract. Public so integration tests can resolve it from DI
/// (<c>factory.Services.GetRequiredService&lt;ICaptchaService&gt;()</c>).
/// The v1 contract (Generate/Validate/PeekAnswer) is frozen — docs/ARCHITECTURE-v2.md §3.
/// </summary>
public interface ICaptchaService
{
    /// <summary>Creates a new one-time challenge (6 chars, latin uppercase + digits, no 0/O/1/I).</summary>
    CaptchaChallenge Generate();

    /// <summary>
    /// Validates and consumes the challenge (one-time use, case-insensitive).
    /// The entry is removed even when the answer is wrong.
    /// </summary>
    bool Validate(string? captchaId, string? answer);

    /// <summary>Returns the stored code without consuming it (dev peek endpoint / tests).</summary>
    string? PeekAnswer(string? captchaId);
}
