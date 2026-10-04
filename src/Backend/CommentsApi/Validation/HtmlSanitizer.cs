using System.Net;
using System.Text;

namespace CommentsApi.Validation;

/// <summary>
/// Pure (no HTTP dependency) allowlist HTML sanitiser.
///
/// Rules (docs/API.md §0, TASK.md «Регулярные выражения»):
///  - allowlist tags: <c>a[href,title]</c>, <c>code</c>, <c>i</c>, <c>strong</c>;
///  - <c>&lt;b&gt;</c> is mapped to <c>strong</c>, <c>&lt;em&gt;</c> to <c>i</c> (still reported as a non-allowlisted tag);
///  - every other tag is escaped and reported as an error;
///  - attributes outside the allowlist are dropped and reported;
///  - <c>javascript:</c> / <c>data:</c> hrefs are dropped and reported;
///  - unclosed / mismatched tags are auto-closed and reported, so the output is valid XHTML.
///
/// <see cref="Sanitize(string)"/> never throws and always produces a safe fragment.
/// </summary>
public sealed class HtmlSanitizer
{
    public const int MaxTextLength = 5000;

    public const string ErrorTextRequired = "Text is required.";
    public const string ErrorTextTooLong = "Text must be at most 5000 characters.";

    private const int MaxReportedErrors = 100;

    private static readonly HashSet<string> AllowedTags =
        new(StringComparer.OrdinalIgnoreCase) { "a", "code", "i", "strong" };

    private static readonly Dictionary<string, string> TagAliases =
        new(StringComparer.OrdinalIgnoreCase) { ["b"] = "strong", ["em"] = "i" };

    private static readonly Dictionary<string, string[]> AllowedAttributes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new[] { "href", "title" },
            ["code"] = Array.Empty<string>(),
            ["i"] = Array.Empty<string>(),
            ["strong"] = Array.Empty<string>(),
        };

    /// <summary>
    /// Sanitises <paramref name="input"/> and returns the safe XHTML fragment, its plain-text
    /// projection and the list of detected problems (empty when the input is fully valid).
    /// </summary>
    public (string html, string plain, IReadOnlyList<string> errors) Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return (string.Empty, string.Empty, Array.Empty<string>());
        }

        var html = new StringBuilder(input.Length + 16);
        var plain = new StringBuilder(input.Length + 16);
        var errors = new List<string>();
        var openTags = new Stack<string>();

        var i = 0;
        while (i < input.Length)
        {
            var lt = input.IndexOf('<', i);
            if (lt < 0)
            {
                AppendText(html, plain, input, i, input.Length - i);
                break;
            }

            if (lt > i)
            {
                AppendText(html, plain, input, i, lt - i);
            }

            // "<!-- ... -->" comments are dropped entirely.
            if (lt + 3 < input.Length && input[lt + 1] == '!' && input[lt + 2] == '-' && input[lt + 3] == '-')
            {
                var close = input.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (close < 0)
                {
                    break; // unterminated comment: drop the tail
                }

                i = close + 3;
                continue;
            }

            var next = lt + 1 < input.Length ? input[lt + 1] : '\0';
            var looksLikeTag = char.IsLetter(next) || next == '/';

            if (!looksLikeTag)
            {
                // A bare '<' (e.g. "a < b") or "<!DOCTYPE ...>": escape it as text.
                html.Append("&lt;");
                plain.Append('<');
                i = lt + 1;
                continue;
            }

            var end = FindTagEnd(input, lt);
            if (end < 0)
            {
                Report(errors, "Tag is not terminated.");
                AppendText(html, plain, input, lt, input.Length - lt);
                break;
            }

            var inner = input.Substring(lt + 1, end - lt - 1);
            var token = ParseTag(inner);
            ProcessTag(token, html, errors, openTags);
            i = end + 1;
        }

        while (openTags.Count > 0)
        {
            var name = openTags.Pop();
            html.Append("</").Append(name).Append('>');
            Report(errors, $"Tag <{name}> was not closed.");
        }

        return (html.ToString(), plain.ToString(), errors);
    }

    /// <summary>Validates length + sanitises in one call; convenience for controllers.</summary>
    public (string html, string plain, IReadOnlyList<string> errors) SanitizeAndValidate(string? input)
    {
        var result = Sanitize(input);
        var errors = new List<string>(result.errors);

        if (string.IsNullOrWhiteSpace(input))
        {
            errors.Insert(0, ErrorTextRequired);
        }
        else if (input.Length > MaxTextLength)
        {
            errors.Insert(0, ErrorTextTooLong);
        }

        return (result.html, result.plain, errors);
    }

    private static void ProcessTag(
        TagToken token,
        StringBuilder html,
        List<string> errors,
        Stack<string> openTags)
    {
        if (token.Name.Length == 0)
        {
            Report(errors, "Malformed tag.");
            return;
        }

        var mappedName = TagAliases.TryGetValue(token.Name, out var alias) ? alias : token.Name.ToLowerInvariant();

        if (!AllowedTags.Contains(mappedName))
        {
            // Disallowed tags are neutralised: the tag name is escaped (so it can never become an
            // element) but its attributes are dropped, because they may carry XSS payloads
            // (e.g. onerror=...). Nothing is added to the plain-text projection.
            Report(errors, $"Tag <{token.Name}> is not allowed.");
            AppendNeutralisedTag(html, token.Name, token.IsClosing);
            return;
        }

        if (!string.Equals(mappedName, token.Name, StringComparison.OrdinalIgnoreCase))
        {
            Report(errors, $"Tag <{token.Name}> is not allowed.");
        }

        if (token.IsClosing)
        {
            if (openTags.Contains(mappedName))
            {
                while (openTags.Count > 0)
                {
                    var popped = openTags.Pop();
                    html.Append("</").Append(popped).Append('>');
                    if (popped == mappedName)
                    {
                        break;
                    }

                    Report(errors, $"Tag <{popped}> was not closed.");
                }
            }
            else
            {
                Report(errors, $"Closing tag </{token.Name}> has no matching opening tag.");
                AppendNeutralisedTag(html, token.Name, isClosing: true);
            }

            return;
        }

        var allowedAttrs = AllowedAttributes[mappedName];
        html.Append('<').Append(mappedName);

        foreach (var (attrName, attrValue) in token.Attributes)
        {
            var lowerAttr = attrName.ToLowerInvariant();
            if (!allowedAttrs.Contains(lowerAttr, StringComparer.OrdinalIgnoreCase))
            {
                Report(errors, $"Attribute '{attrName}' is not allowed on <{mappedName}>.");
                continue;
            }

            if (lowerAttr == "href")
            {
                if (!IsSafeHref(attrValue))
                {
                    Report(errors, "URL scheme in 'href' is not allowed.");
                    continue;
                }
            }

            html.Append(' ').Append(lowerAttr).Append("=\"").Append(EscapeAttribute(attrValue)).Append('"');
        }

        if (token.IsSelfClosing)
        {
            html.Append("></").Append(mappedName).Append('>');
        }
        else
        {
            html.Append('>');
            openTags.Push(mappedName);
        }
    }

    /// <summary>A href must be a real link; javascript:/data: (also entity-encoded or whitespace-split) are rejected.</summary>
    public static bool IsSafeHref(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return false;
        }

        // Decode entities ("&#x6a;avascript:") then remove whitespace/control chars ("java\tscript:").
        var decoded = WebUtility.HtmlDecode(href);
        var normalized = new StringBuilder(decoded.Length);
        foreach (var ch in decoded)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsControl(ch))
            {
                normalized.Append(char.ToLowerInvariant(ch));
            }
        }

        var value = normalized.ToString();
        if (value.Length == 0)
        {
            return false;
        }

        return !value.StartsWith("javascript:", StringComparison.Ordinal)
               && !value.StartsWith("data:", StringComparison.Ordinal)
               && !value.StartsWith("vbscript:", StringComparison.Ordinal);
    }

    private static void AppendText(StringBuilder html, StringBuilder plain, string input, int start, int length)
    {
        // Decode entity references once, then escape the decoded text: input "&amp;" must render
        // as "&", not "&amp;amp;".
        var decoded = WebUtility.HtmlDecode(input.Substring(start, length));
        html.Append(EscapeText(decoded));
        plain.Append(decoded);
    }

    /// <summary>
    /// Renders a disallowed tag as inert visible text ("&lt;img&gt;" / "&lt;/img&gt;") without
    /// reproducing its attributes, so payload text such as onerror= never reaches the output.
    /// </summary>
    private static void AppendNeutralisedTag(StringBuilder html, string name, bool isClosing)
    {
        html.Append("&lt;");
        if (isClosing)
        {
            html.Append('/');
        }

        html.Append(EscapeText(name.ToLowerInvariant()));
        html.Append("&gt;");
    }

    private static string EscapeText(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    private static string EscapeAttribute(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    private static void Report(List<string> errors, string message)
    {
        if (errors.Count < MaxReportedErrors && !errors.Contains(message))
        {
            errors.Add(message);
        }
    }

    /// <summary>Finds the '&gt;' closing a tag, ignoring '&gt;' inside quoted attribute values.</summary>
    private static int FindTagEnd(string input, int start)
    {
        char quote = '\0';
        for (var i = start + 1; i < input.Length; i++)
        {
            var c = input[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static TagToken ParseTag(string inner)
    {
        var s = inner;
        var p = 0;
        var closing = false;
        var selfClosing = false;

        while (p < s.Length && char.IsWhiteSpace(s[p]))
        {
            p++;
        }

        if (p < s.Length && s[p] == '/')
        {
            closing = true;
            p++;
        }

        var nameStart = p;
        while (p < s.Length && (char.IsLetterOrDigit(s[p]) || s[p] is '-' or '_' or ':'))
        {
            p++;
        }

        var name = s.Substring(nameStart, p - nameStart);
        var attributes = new List<(string Name, string Value)>();

        if (!closing)
        {
            while (p < s.Length)
            {
                while (p < s.Length && char.IsWhiteSpace(s[p]))
                {
                    p++;
                }

                if (p >= s.Length)
                {
                    break;
                }

                if (s[p] == '/')
                {
                    selfClosing = true;
                    p++;
                    continue;
                }

                var attrStart = p;
                while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != '=' && s[p] != '/')
                {
                    p++;
                }

                var attrName = s.Substring(attrStart, p - attrStart);
                while (p < s.Length && char.IsWhiteSpace(s[p]))
                {
                    p++;
                }

                var value = string.Empty;
                if (p < s.Length && s[p] == '=')
                {
                    p++;
                    while (p < s.Length && char.IsWhiteSpace(s[p]))
                    {
                        p++;
                    }

                    if (p < s.Length && (s[p] == '"' || s[p] == '\''))
                    {
                        var quote = s[p++];
                        var valueStart = p;
                        while (p < s.Length && s[p] != quote)
                        {
                            p++;
                        }

                        value = s.Substring(valueStart, p - valueStart);
                        if (p < s.Length)
                        {
                            p++;
                        }
                    }
                    else
                    {
                        var valueStart = p;
                        while (p < s.Length && !char.IsWhiteSpace(s[p]) && s[p] != '>')
                        {
                            p++;
                        }

                        value = s.Substring(valueStart, p - valueStart);
                    }
                }

                if (attrName.Length > 0)
                {
                    attributes.Add((attrName, value));
                }
            }
        }

        return new TagToken(closing, selfClosing, name, attributes);
    }

    private readonly record struct TagToken(
        bool IsClosing,
        bool IsSelfClosing,
        string Name,
        List<(string Name, string Value)> Attributes);
}
