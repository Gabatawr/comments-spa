namespace Comments.Application.Services;

/// <summary>Sorting/paging defaults from docs/API-v2.md §2.3, §4.</summary>
public static class CommentSortOptions
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const string DefaultSortBy = "createdAt";
    public const string DefaultSortDir = "desc";

    public static readonly string[] AllowedSortBy = { "createdAt", "userName", "email" };
    public static readonly string[] AllowedSortDir = { "asc", "desc" };

    /// <summary>
    /// Canonicalises query parameters. Unknown values fall back to the defaults instead of
    /// throwing — this also guarantees no attacker-controlled string ever reaches SQL.
    /// </summary>
    public static (string SortBy, string SortDir) Normalize(string? sortBy, string? sortDir)
    {
        var normalizedSortBy = DefaultSortBy;
        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            var candidate = sortBy.Trim();
            foreach (var allowed in AllowedSortBy)
            {
                if (string.Equals(allowed, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedSortBy = allowed;
                    break;
                }
            }
        }

        var normalizedSortDir = DefaultSortDir;
        if (!string.IsNullOrWhiteSpace(sortDir))
        {
            var candidate = sortDir.Trim();
            foreach (var allowed in AllowedSortDir)
            {
                if (string.Equals(allowed, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    normalizedSortDir = allowed;
                    break;
                }
            }
        }

        return (normalizedSortBy, normalizedSortDir);
    }

    /// <summary>Clamps a requested page size to the contract range 1..100 (default 25).</summary>
    public static int NormalizePageSize(int? pageSize)
        => pageSize switch
        {
            null or < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize.Value,
        };

    /// <summary>Clamps a requested page number to >= 1.</summary>
    public static int NormalizePage(int? page)
        => page is null or < 1 ? 1 : page.Value;

    public static int TotalPages(int totalItems, int pageSize)
        => totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);
}
