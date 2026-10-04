using Comments.Domain;

namespace Comments.Application.Abstractions.Persistence;

/// <summary>Attachment metadata store (docs/ARCHITECTURE-v2.md §3).</summary>
public interface IAttachmentRepository
{
    Task<Attachment?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default);
}
