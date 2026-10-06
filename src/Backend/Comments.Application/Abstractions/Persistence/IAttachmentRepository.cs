using Comments.Domain;

namespace Comments.Application.Abstractions.Persistence;

/// <summary>Attachment metadata store (docs/ARCHITECTURE-v2.md §3).</summary>
public interface IAttachmentRepository
{
    Task<Attachment?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the metadata row for deletion. Used when an upload has to be undone because the
    /// comment it belonged to was never created (see <c>IAttachmentService.DeleteAsync</c>).
    /// </summary>
    void Remove(Attachment attachment);
}