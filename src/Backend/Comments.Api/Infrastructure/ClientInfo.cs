using System.Net;

namespace Comments.Api.Infrastructure;

/// <summary>
/// Client IP / User-Agent capture rules from docs/API-v2.md §6.
///
/// The API is always behind nginx in production, so <c>X-Forwarded-For</c> is authoritative:
/// the left-most non-empty entry is the real client. <c>UseForwardedHeaders</c> is enabled in
/// Program.cs as well, but this helper reads the raw headers deliberately so that the contract
/// ("first hop wins") is honoured regardless of how many proxies are trusted.
/// </summary>
public static class ClientInfo
{
    public const int MaxIpLength = 64;
    public const int MaxUserAgentLength = 512;

    /// <summary>Resolves the client IP and user agent for a request, applying the §6 truncation rules.</summary>
    public static (string? ClientIp, string? UserAgent) Resolve(HttpRequest request, ILogger? logger = null)
        => (ResolveClientIp(request, logger), ResolveUserAgent(request));

    public static string? ResolveClientIp(HttpRequest request, ILogger? logger = null)
    {
        var value = FirstForwardedForEntry(request.Headers["X-Forwarded-For"]);

        if (string.IsNullOrWhiteSpace(value))
        {
            value = request.Headers["X-Real-IP"].ToString().Trim();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = request.HttpContext.Connection.RemoteIpAddress?.ToString();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        // The contract keeps malformed values as-is (they are stored verbatim) but logs them.
        if (!IPAddress.TryParse(value, out _))
        {
            logger?.LogDebug("Client IP header value {ClientIp} is not a valid IP address; storing as-is", value);
        }

        return Truncate(value, MaxIpLength);
    }

    public static string? ResolveUserAgent(HttpRequest request)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        return Truncate(userAgent, MaxUserAgentLength);
    }

    /// <summary>Left-most non-empty value of a (possibly repeated, possibly comma-joined) XFF header.</summary>
    private static string? FirstForwardedForEntry(Microsoft.Extensions.Primitives.StringValues forwardedFor)
    {
        if (forwardedFor.Count == 0)
        {
            return null;
        }

        foreach (var headerValue in forwardedFor)
        {
            if (string.IsNullOrEmpty(headerValue))
            {
                continue;
            }

            foreach (var part in headerValue.Split(','))
            {
                var candidate = part.Trim();
                if (candidate.Length > 0)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
