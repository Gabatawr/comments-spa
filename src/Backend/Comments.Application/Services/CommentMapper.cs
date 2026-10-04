using Comments.Application.Dtos;
using Comments.Domain;

namespace Comments.Application.Services;

/// <summary>Maps entities to the DTOs defined in docs/API-v2.md §1.</summary>
public static class CommentMapper
{
    public static AttachmentDto? ToDto(Attachment? attachment)
    {
        if (attachment is null)
        {
            return null;
        }

        return new AttachmentDto
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            ContentType = attachment.ContentType,
            Kind = attachment.Kind,
            SizeBytes = attachment.SizeBytes,
            Width = attachment.Width,
            Height = attachment.Height,
            Url = $"/api/attachments/{attachment.Id}",
            ThumbUrl = attachment.Kind == AttachmentKinds.Image ? $"/api/attachments/{attachment.Id}/thumb" : null,
        };
    }

    public static CommentDto ToDto(Comment comment)
    {
        return new CommentDto
        {
            Id = comment.Id,
            ParentId = comment.ParentId,
            UserName = comment.UserName,
            Email = comment.Email,
            HomePage = comment.HomePage,
            Text = comment.TextHtml,
            TextPlain = comment.TextPlain,
            QuotedText = comment.QuotedText,
            CreatedAt = AsUtc(comment.CreatedAt),
            ClientIp = comment.ClientIp,
            UserAgent = comment.UserAgent,
            Attachment = ToDto(comment.Attachment),
            Replies = new List<CommentDto>(),
        };
    }

    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
