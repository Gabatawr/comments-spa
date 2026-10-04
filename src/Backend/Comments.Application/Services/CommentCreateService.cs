using Comments.Application.Abstractions;
using Comments.Application.Abstractions.Persistence;
using Comments.Application.Dtos;
using Comments.Domain;

namespace Comments.Application.Services;

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

/// <summary>
/// Persists a validated comment. Keeps the v1 behaviour (including created-at override for tests
/// and re-loading the attachment after commit) without depending on EF Core.
/// </summary>
public sealed class CommentCreateService : ICommentCreateService
{
    private readonly ICommentRepository _comments;
    private readonly IAttachmentRepository _attachments;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CommentCreateService(
        ICommentRepository comments,
        IAttachmentRepository attachments,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _comments = comments;
        _attachments = attachments;
        _unitOfWork = unitOfWork;
        _clock = clock;
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
            CreatedAt = model.CreatedAt ?? _clock.UtcNow,
            ClientIp = model.ClientIp,
            UserAgent = model.UserAgent,
            AttachmentId = model.AttachmentId,
        };

        await _comments.AddAsync(comment, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        if (comment.AttachmentId is { } attachmentId)
        {
            comment.Attachment = await _attachments.GetByIdAsync(attachmentId, cancellationToken);
        }

        return CommentMapper.ToDto(comment);
    }
}
