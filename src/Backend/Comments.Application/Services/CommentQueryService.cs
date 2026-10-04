using System.Globalization;
using Comments.Application.Abstractions.Persistence;
using Comments.Application.Dtos;
using Comments.Domain;

namespace Comments.Application.Services;

public interface ICommentQueryService
{
    /// <summary>Page of root comments with their full nested reply trees (v1 signature).</summary>
    Task<CommentPageDto> GetRootPageAsync(
        int page,
        int pageSize,
        string? sortBy,
        string? sortDir,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Same as above plus the Middle+ keyset <paramref name="cursor"/> (docs/API-v2.md §4.5).
    /// When the cursor is set, <paramref name="page"/> is ignored. A cursor whose sort does not
    /// match throws <see cref="CommentCursorException"/>.
    /// </summary>
    Task<CommentPageDto> GetRootPageAsync(
        int page,
        int pageSize,
        string? sortBy,
        string? sortDir,
        string? cursor,
        CancellationToken cancellationToken = default);

    /// <summary>A single comment (root or reply) with its full reply tree, or null.</summary>
    Task<CommentDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model for comments. Normalisation, cursor handling and the breadth-first tree assembly
/// live here; the SQL (ordering via <c>lower()</c>, offset/keyset predicates) lives in the
/// repository adapter. Every query is parameterised.
/// </summary>
public sealed class CommentQueryService : ICommentQueryService
{
    private readonly ICommentRepository _repo;

    public CommentQueryService(ICommentRepository repo)
    {
        _repo = repo;
    }

    public Task<CommentPageDto> GetRootPageAsync(
        int page,
        int pageSize,
        string? sortBy,
        string? sortDir,
        CancellationToken cancellationToken = default)
        => GetRootPageAsync(page, pageSize, sortBy, sortDir, cursor: null, cancellationToken);

    public async Task<CommentPageDto> GetRootPageAsync(
        int page,
        int pageSize,
        string? sortBy,
        string? sortDir,
        string? cursor,
        CancellationToken cancellationToken = default)
    {
        var (normalizedSortBy, normalizedSortDir) = CommentSortOptions.Normalize(sortBy, sortDir);
        page = CommentSortOptions.NormalizePage(page);
        pageSize = CommentSortOptions.NormalizePageSize(pageSize);

        CommentCursor? parsedCursor = null;
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            parsedCursor = CommentCursorCodec.Decode(cursor);
            if (!string.Equals(parsedCursor.SortBy, normalizedSortBy, StringComparison.Ordinal)
                || !string.Equals(parsedCursor.SortDir, normalizedSortDir, StringComparison.Ordinal))
            {
                throw new CommentCursorException("Cursor does not match sort.");
            }
        }

        var totalItems = await _repo.CountRootsAsync(cancellationToken);
        var totalPages = CommentSortOptions.TotalPages(totalItems, pageSize);

        // 64-bit offset math: page can be int.MaxValue, and (page - 1) * pageSize overflows int32
        // to a negative OFFSET that PostgreSQL rejects (docs/API-v2.md §2.3, §4.7).
        long skipLong = parsedCursor is null ? ((long)page - 1) * pageSize : 0;
        if (parsedCursor is null && skipLong >= totalItems)
        {
            // Out-of-range page is an empty 200, not a query and never a 500.
            return new CommentPageDto
            {
                Items = new List<CommentDto>(),
                Page = page,
                PageSize = pageSize,
                TotalItems = totalItems,
                TotalPages = totalPages,
                SortBy = normalizedSortBy,
                SortDir = normalizedSortDir,
                NextCursor = null,
            };
        }

        var request = new CommentPageRequest(normalizedSortBy, normalizedSortDir, (int)skipLong, pageSize, parsedCursor);
        var result = await _repo.GetRootPageAsync(request, cancellationToken);

        var byId = new Dictionary<int, CommentDto>();
        var roots = new List<CommentDto>(result.Items.Count);
        foreach (var root in result.Items)
        {
            var dto = CommentMapper.ToDto(root);
            byId[dto.Id] = dto;
            roots.Add(dto);
        }

        await AttachDescendantsAsync(byId, roots.Select(r => r.Id).ToList(), cancellationToken);
        FinalizeCounts(byId.Values);

        string? nextCursor = null;
        if (result.Items.Count > 0)
        {
            var last = result.Items[^1];
            var hasNextPage = parsedCursor is null
                ? (long)page * pageSize < totalItems
                : result.HasMore;

            if (hasNextPage)
            {
                nextCursor = CommentCursorCodec.Encode(new CommentCursor(
                    normalizedSortBy,
                    normalizedSortDir,
                    SortValue(last, normalizedSortBy),
                    last.Id));
            }
        }

        return new CommentPageDto
        {
            Items = roots,
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            SortBy = normalizedSortBy,
            SortDir = normalizedSortDir,
            NextCursor = nextCursor,
        };
    }

    public async Task<CommentDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _repo.GetByIdAsync(id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var dto = CommentMapper.ToDto(entity);
        var byId = new Dictionary<int, CommentDto> { [dto.Id] = dto };
        await AttachDescendantsAsync(byId, new List<int> { dto.Id }, cancellationToken);
        FinalizeCounts(byId.Values);
        return dto;
    }

    /// <summary>
    /// Breadth-first load of every descendant of the given frontier, one repository query per
    /// level (docs/API-v2.md §4.6).
    /// </summary>
    private async Task AttachDescendantsAsync(
        Dictionary<int, CommentDto> byId,
        List<int> frontier,
        CancellationToken cancellationToken)
    {
        // Guard against corrupt data / cycles.
        var guard = 0;
        while (frontier.Count > 0 && guard++ < 1000)
        {
            var children = await _repo.GetChildrenAsync(frontier, cancellationToken);
            if (children.Count == 0)
            {
                break;
            }

            var next = new List<int>();
            foreach (var child in children)
            {
                if (byId.ContainsKey(child.Id))
                {
                    continue;
                }

                var childDto = CommentMapper.ToDto(child);
                if (child.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
                {
                    parent.Replies.Add(childDto);
                }

                byId[child.Id] = childDto;
                next.Add(child.Id);
            }

            frontier = next;
        }
    }

    private static void FinalizeCounts(IEnumerable<CommentDto> all)
    {
        foreach (var dto in all)
        {
            dto.ReplyCount = dto.Replies.Count;
            FinalizeCounts(dto.Replies);
        }
    }

    /// <summary>
    /// Cursor value for the last row: ISO-8601 UTC for createdAt, <c>lower(value)</c> otherwise
    /// (docs/API-v2.md §4.5).
    /// </summary>
    private static string SortValue(Comment comment, string sortBy) => sortBy switch
    {
        "userName" => comment.UserName.ToLowerInvariant(),
        "email" => comment.Email.ToLowerInvariant(),
        _ => CommentMapper.AsUtc(comment.CreatedAt).ToString("O", CultureInfo.InvariantCulture),
    };
}
