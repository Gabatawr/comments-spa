using CommentsApi.Data;
using CommentsApi.Domain;
using CommentsApi.Dtos;
using Microsoft.EntityFrameworkCore;

namespace CommentsApi.Services;

public interface ICommentQueryService
{
    /// <summary>Page of root comments with their full nested reply trees.</summary>
    Task<CommentPageDto> GetRootPageAsync(
        int page,
        int pageSize,
        string? sortBy,
        string? sortDir,
        CancellationToken cancellationToken = default);

    /// <summary>A single comment (root or reply) with its full reply tree, or null.</summary>
    Task<CommentDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Read model for comments. Root paging/sorting happens in SQL (EF Core LINQ, fully
/// parameterised); the reply tree is assembled breadth-first, level by level.
/// </summary>
public sealed class CommentQueryService : ICommentQueryService
{
    private readonly AppDbContext _db;

    public CommentQueryService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CommentPageDto> GetRootPageAsync(
        int page,
        int pageSize,
        string? sortBy,
        string? sortDir,
        CancellationToken cancellationToken = default)
    {
        var (normalizedSortBy, normalizedSortDir) = CommentSortOptions.Normalize(sortBy, sortDir);

        if (page < 1)
        {
            page = 1;
        }

        if (pageSize < 1)
        {
            pageSize = CommentSortOptions.DefaultPageSize;
        }
        else if (pageSize > CommentSortOptions.MaxPageSize)
        {
            pageSize = CommentSortOptions.MaxPageSize;
        }

        var roots = _db.Comments.AsNoTracking().Where(c => c.ParentId == null);

        var totalItems = await roots.CountAsync(cancellationToken);
        var totalPages = totalItems == 0 ? 0 : (int)Math.Ceiling(totalItems / (double)pageSize);

        var ascending = normalizedSortDir == "asc";
        IOrderedQueryable<Comment> ordered = normalizedSortBy switch
        {
            "userName" => ascending
                ? roots.OrderBy(c => EF.Functions.Collate(c.UserName, "NOCASE"))
                : roots.OrderByDescending(c => EF.Functions.Collate(c.UserName, "NOCASE")),
            "email" => ascending
                ? roots.OrderBy(c => EF.Functions.Collate(c.Email, "NOCASE"))
                : roots.OrderByDescending(c => EF.Functions.Collate(c.Email, "NOCASE")),
            _ => ascending
                ? roots.OrderBy(c => c.CreatedAt)
                : roots.OrderByDescending(c => c.CreatedAt),
        };

        var rootEntities = await ordered
            .ThenBy(c => c.Id)
            .Include(c => c.Attachment)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var byId = new Dictionary<int, CommentDto>();
        var frontier = new List<int>();

        foreach (var root in rootEntities)
        {
            var dto = CommentMapper.ToDto(root);
            byId[dto.Id] = dto;
            frontier.Add(dto.Id);
        }

        await AttachDescendantsAsync(byId, frontier, cancellationToken);
        FinalizeCounts(byId.Values);

        return new CommentPageDto
        {
            Items = byId.Values.Where(dto => dto.ParentId == null).ToList(),
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            TotalPages = totalPages,
            SortBy = normalizedSortBy,
            SortDir = normalizedSortDir,
        };
    }

    public async Task<CommentDto?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _db.Comments
            .AsNoTracking()
            .Include(c => c.Attachment)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

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
    /// Breadth-first load of every descendant of the given frontier, all queries parameterised.
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
            var parents = frontier;
            var children = await _db.Comments
                .AsNoTracking()
                .Include(c => c.Attachment)
                .Where(c => c.ParentId != null && parents.Contains(c.ParentId.Value))
                .OrderBy(c => c.CreatedAt)
                .ThenBy(c => c.Id)
                .ToListAsync(cancellationToken);

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
}
