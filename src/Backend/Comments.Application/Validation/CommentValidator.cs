using System.Text.RegularExpressions;

namespace Comments.Application.Validation;

/// <summary>
/// Pure field validators shared by the API and (mirrored by) the SPA client.
/// No HTTP dependency, so unit tests can call it directly.
/// </summary>
public static partial class CommentValidator
{
    public const int UserNameMinLength = 3;
    public const int UserNameMaxLength = 50;
    public const int EmailMaxLength = 100;
    public const int HomePageMaxLength = 200;
    public const int TextMinLength = 1;
    public const int TextMaxLength = HtmlSanitizer.MaxTextLength;

    public const string ErrorUserNameRequired = "User Name is required.";
    public const string ErrorUserNameTooShort = "User Name must be at least 3 characters long.";
    public const string ErrorUserNameTooLong = "User Name must be at most 50 characters long.";
    public const string ErrorUserNameCharset = "User Name may contain only latin letters and digits.";
    public const string ErrorEmailRequired = "E-mail is required.";
    public const string ErrorEmailTooLong = "E-mail must be at most 100 characters long.";
    public const string ErrorEmailInvalid = "E-mail is not valid.";
    public const string ErrorHomePageTooLong = "Home page must be at most 200 characters long.";
    public const string ErrorHomePageInvalid = "Home page must be a valid absolute http(s) URL.";
    public const string ErrorCaptchaRequired = "CAPTCHA is required.";
    public const string ErrorCaptchaInvalid = "CAPTCHA answer is invalid or expired.";
    public const string ErrorParentNotFound = "Parent comment does not exist.";
    public const string ErrorParentInvalid = "Parent comment id is not valid.";

    [GeneratedRegex("^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?:\\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex UserNameCharsetRegex();

    /// <summary>Returns the first error for a user name, or null when valid.</summary>
    public static string? ValidateUserName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ErrorUserNameRequired;
        }

        if (value.Length < UserNameMinLength)
        {
            return ErrorUserNameTooShort;
        }

        if (value.Length > UserNameMaxLength)
        {
            return ErrorUserNameTooLong;
        }

        return UserNameCharsetRegex().IsMatch(value) ? null : ErrorUserNameCharset;
    }

    /// <summary>Returns the first error for an e-mail, or null when valid.</summary>
    public static string? ValidateEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ErrorEmailRequired;
        }

        if (value.Length > EmailMaxLength)
        {
            return ErrorEmailTooLong;
        }

        // No leading/trailing whitespace, and an RFC-lite anchored pattern: exactly one '@',
        // a dot-separated domain with a >=2 char TLD, no spaces or markup characters.
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return ErrorEmailInvalid;
        }

        return EmailRegex().IsMatch(value) ? null : ErrorEmailInvalid;
    }

    /// <summary>
    /// Normalises the optional home page: empty/whitespace => null.
    /// Returns the error message for invalid input.
    /// </summary>
    public static string? ValidateHomePage(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > HomePageMaxLength)
        {
            return ErrorHomePageTooLong;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ErrorHomePageInvalid;
        }

        normalized = trimmed;
        return null;
    }

    /// <summary>Required + length check (tag validity is reported by <see cref="HtmlSanitizer"/>).</summary>
    public static string? ValidateTextLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return HtmlSanitizer.ErrorTextRequired;
        }

        return value.Length > TextMaxLength ? HtmlSanitizer.ErrorTextTooLong : null;
    }
}
