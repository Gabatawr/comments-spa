using Comments.Domain;

namespace Comments.Application.Abstractions.Persistence;

/// <summary>
/// Sorting/paging window handed to the persistence adapter. The adapter translates it to SQL;
/// it must use <c>lower(value)</c> for case-insensitive userName/email sorting (docs/API-v2.md §4).
/// </summary>
public sealed record CommentPageRequest(
    string SortBy,
    string SortDir,
    int Skip,
    int Take,
    CommentCursor? Cursor)
{
    public bool Ascending => SortDir == "asc";
}

/// <summary>Decoded keyset cursor (docs/API-v2.md §4.5).</summary>
public sealed record CommentCursor(string SortBy, string SortDir, string Value, int Id);

/// <summary>One page of roots plus whether the database holds another row after the window.</summary>
public sealed record RootPageResult(IReadOnlyList<Comment> Items, bool HasMore);

/// <summary>Aggregate counters for <c>GET /api/stats</c> (docs/API-v2.md §3.5).</summary>
public sealed record CommentTotals(
    long TotalComments,
    long TotalRoots,
    long TotalAttachments,
    DateTime? OldestAt,
    DateTime? NewestAt);

/// <summary>Bulk-generation request for <c>POST /api/dev/seed</c> (docs/API-v2.md §3.4).</summary>
public sealed record SeedOptions(int Count, int Roots, int Depth, int BatchSize, bool Clear);

/// <summary>Result of a bulk seed run.</summary>
public sealed record SeedOutcome(long Created, long Roots, long ElapsedMs);

/// <summary>
/// Comment tree/page reads and writes. Implemented by EF Core + Npgsql in Infrastructure.
/// All LINQ is parameterised; no string concatenation ever reaches SQL.
/// </summary>
public interface ICommentRepository
{
    /// <summary>Number of root comments (ParentId == null).</summary>
    Task<int> CountRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One window of roots (with attachments), ordered exactly as requested, plus a
    /// <c>HasMore</c> flag computed by peeking one row past the window.
    /// </summary>
    Task<RootPageResult> GetRootPageAsync(CommentPageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Single comment (root or reply) with its attachment, or null.</summary>
    Task<Comment?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Direct children of the given parents with attachments, ordered createdAt ASC, id ASC.</summary>
    Task<IReadOnlyList<Comment>> GetChildrenAsync(
        IReadOnlyCollection<int> parentIds,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default);

    Task AddAsync(Comment comment, CancellationToken cancellationToken = default);

    /// <summary>Aggregate counters for stats.</summary>
    Task<CommentTotals> GetTotalsAsync(CancellationToken cancellationToken = default);
}
