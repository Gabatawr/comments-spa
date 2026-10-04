using CommentsApi.Data;
using CommentsApi.Domain;
using CommentsApi.Dtos;

namespace CommentsApi.Services;

/// <summary>Validated, sanitised input for <see cref="ICommentCreateService"/>.</summary>
public sealed class CommentCreateModel
{
    public string UserName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? HomePage { get; init; }
    public string TextHtml { get; init; } = string.Empty;
    public string TextPlain { get; init; } = string.Empty;
    public int? ParentId { get; init; }
    public int? AttachmentId { get; init; }
    public string? ClientIp { get; init; }
    public string? UserAgent { get; init; }
    public DateTime? CreatedAt { get; init; }
}

public interface ICommentCreateService
{
    Task<CommentDto> CreateAsync(CommentCreateModel model, CancellationToken cancellationToken = default);
}

public sealed class CommentCreateService : ICommentCreateService
{
    private readonly AppDbContext _db;

    public CommentCreateService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<CommentDto> CreateAsync(CommentCreateModel model, CancellationToken cancellationToken = default)
    {
        var comment = new Comment
        {
            ParentId = model.ParentId,
            UserName = model.UserName,
            Email = model.Email,
            HomePage = model.HomePage,
            TextHtml = model.TextHtml,
            TextPlain = model.TextPlain,
            CreatedAt = model.CreatedAt ?? DateTime.UtcNow,
            ClientIp = model.ClientIp,
            UserAgent = model.UserAgent,
            AttachmentId = model.AttachmentId,
        };

        _db.Comments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        if (comment.AttachmentId is { } attachmentId)
        {
            comment.Attachment = await _db.Attachments.FindAsync(new object?[] { attachmentId }, cancellationToken);
        }

        return CommentMapper.ToDto(comment);
    }
}
