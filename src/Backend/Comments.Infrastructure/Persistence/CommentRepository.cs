using System.Globalization;
using Comments.Application.Abstractions.Persistence;
using Comments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comments.Infrastructure.Persistence;

/// <summary>
/// EF Core + Npgsql comment repository. Sorting is fully parameterised; case-insensitive
/// userName/email ordering uses <c>lower(value)</c> in SQL (never a collation), matching the
/// functional indexes from the initial migration.
/// </summary>
public sealed class CommentRepository : ICommentRepository
{
    private readonly AppDbContext _db;

    public CommentRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<int> CountRootsAsync(CancellationToken cancellationToken = default)
        => _db.Comments.AsNoTracking().CountAsync(c => c.ParentId == null, cancellationToken);

    public async Task<RootPageResult> GetRootPageAsync(
        CommentPageRequest request,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Comment> roots = _db.Comments.AsNoTracking().Where(c => c.ParentId == null);

        if (request.Cursor is not null)
        {
            roots = ApplyCursor(roots, request);
        }

        var ordered = request.SortBy switch
        {
            "userName" => request.Ascending
                ? roots.OrderBy(c => c.UserName.ToLower())
                : roots.OrderByDescending(c => c.UserName.ToLower()),
            "email" => request.Ascending
                ? roots.OrderBy(c => c.Email.ToLower())
                : roots.OrderByDescending(c => c.Email.ToLower()),
            _ => request.Ascending
                ? roots.OrderBy(c => c.CreatedAt)
                : roots.OrderByDescending(c => c.CreatedAt),
        };

        // Deterministic tie-breaker everywhere (docs/API-v2.md §4.3): id ASC.
        var window = ordered.ThenBy(c => c.Id).Include(c => c.Attachment);

        var take = request.Take < 1 ? 1 : request.Take;
        var rows = await window
            .Skip(request.Skip)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > take;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        return new RootPageResult(rows, hasMore);
    }

    public Task<Comment?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => _db.Comments
            .AsNoTracking()
            .Include(c => c.Attachment)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Comment>> GetChildrenAsync(
        IReadOnlyCollection<int> parentIds,
        CancellationToken cancellationToken = default)
    {
        var ids = parentIds as int[] ?? parentIds.ToArray();
        if (ids.Length == 0)
        {
            return Array.Empty<Comment>();
        }

        return await _db.Comments
            .AsNoTracking()
            .Include(c => c.Attachment)
            .Where(c => c.ParentId != null && ids.Contains(c.ParentId.Value))
            .OrderBy(c => c.CreatedAt)
            .ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(int id, CancellationToken cancellationToken = default)
        => _db.Comments.AsNoTracking().AnyAsync(c => c.Id == id, cancellationToken);

    public async Task AddAsync(Comment comment, CancellationToken cancellationToken = default)
        => await _db.Comments.AddAsync(comment, cancellationToken);

    public async Task<CommentTotals> GetTotalsAsync(CancellationToken cancellationToken = default)
    {
        var totalComments = await _db.Comments.LongCountAsync(cancellationToken);
        var totalRoots = await _db.Comments.LongCountAsync(c => c.ParentId == null, cancellationToken);
        var totalAttachments = await _db.Attachments.LongCountAsync(cancellationToken);
        var oldest = await _db.Comments.MinAsync(c => (DateTime?)c.CreatedAt, cancellationToken);
        var newest = await _db.Comments.MaxAsync(c => (DateTime?)c.CreatedAt, cancellationToken);

        return new CommentTotals(totalComments, totalRoots, totalAttachments, oldest, newest);
    }

    /// <summary>
    /// Keyset predicate (docs/API-v2.md §4.5). The tie-breaker is id ASC in every direction, so
    /// for equal sort values the next page continues with a strictly larger id — this keeps
    /// pagination duplicate-free and consistent with the emitted ORDER BY.
    /// </summary>
    private static IQueryable<Comment> ApplyCursor(IQueryable<Comment> query, CommentPageRequest request)
    {
        var cursor = request.Cursor!;

        if (request.SortBy == "userName")
        {
            var value = cursor.Value;
            return request.Ascending
                ? query.Where(c => c.UserName.ToLower().CompareTo(value) > 0
                                   || (c.UserName.ToLower() == value && c.Id > cursor.Id))
                : query.Where(c => c.UserName.ToLower().CompareTo(value) < 0
                                   || (c.UserName.ToLower() == value && c.Id > cursor.Id));
        }

        if (request.SortBy == "email")
        {
            var value = cursor.Value;
            return request.Ascending
                ? query.Where(c => c.Email.ToLower().CompareTo(value) > 0
                                   || (c.Email.ToLower() == value && c.Id > cursor.Id))
                : query.Where(c => c.Email.ToLower().CompareTo(value) < 0
                                   || (c.Email.ToLower() == value && c.Id > cursor.Id));
        }

        var createdAt = DateTime.SpecifyKind(
            DateTime.Parse(cursor.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeKind.Utc);

        return request.Ascending
            ? query.Where(c => c.CreatedAt > createdAt || (c.CreatedAt == createdAt && c.Id > cursor.Id))
            : query.Where(c => c.CreatedAt < createdAt || (c.CreatedAt == createdAt && c.Id > cursor.Id));
    }
}
