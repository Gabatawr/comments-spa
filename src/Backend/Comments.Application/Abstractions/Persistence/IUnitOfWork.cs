namespace Comments.Application.Abstractions.Persistence;

/// <summary>Transactional boundary over the persistence adapter.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
