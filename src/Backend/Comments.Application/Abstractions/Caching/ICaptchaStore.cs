namespace Comments.Application.Abstractions.Caching;

/// <summary>
/// Stores CAPTCHA codes behind the synchronous v1 <c>ICaptchaService</c> contract
/// (Redis by default, in-memory fallback).
/// </summary>
public interface ICaptchaStore
{
    void Set(string captchaId, string code, TimeSpan ttl);

    /// <summary>Reads the code without consuming it (dev peek).</summary>
    string? Get(string captchaId);

    /// <summary>Reads and removes the code in one step (one-time use).</summary>
    string? Consume(string captchaId);

    /// <summary>True when the backing provider is reachable.</summary>
    bool IsAvailable { get; }
}
