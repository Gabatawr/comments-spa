using System.Text.RegularExpressions;

namespace Comments.Application.Services;

/// <summary>
/// Builds the functional quote snapshot stored on a reply
/// (docs/DESIGN-v2.1-decisions.md §1.1, docs/DESIGN-v2.md §4).
///
/// The input is always the parent's <c>TextPlain</c> — text that already went through the
/// sanitizer, so it never contains markup. This helper only normalises whitespace and truncates;
/// XSS safety on render is the frontend's job (Angular interpolation).
/// </summary>
public static partial class QuoteSnapshot
{
    /// <summary>Maximum number of source characters kept before the ellipsis.</summary>
    public const int MaxLength = 160;

    /// <summary>Truncation marker (U+2026), added only when the text was actually shortened.</summary>
    public const string Ellipsis = "\u2026";

    /// <summary>
    /// Collapses whitespace runs to single spaces, trims, truncates to
    /// <see cref="MaxLength"/> on a word boundary where possible and appends
    /// <see cref="Ellipsis"/>. Returns <c>null</c> for null/blank input.
    /// The result is at most <see cref="MaxLength"/> + 1 characters.
    /// </summary>
    public static string? FromPlainText(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return null;
        }

        var collapsed = Whitespace().Replace(plainText, " ").Trim();
        if (collapsed.Length == 0)
        {
            return null;
        }

        if (collapsed.Length <= MaxLength)
        {
            return collapsed;
        }

        var cut = collapsed[..MaxLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            cut = cut[..lastSpace];
        }

        return cut + Ellipsis;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
