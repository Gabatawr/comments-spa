using Comments.Application.Abstractions;
using Comments.Application.Abstractions.Persistence;
using Comments.Domain;
using Microsoft.EntityFrameworkCore;

namespace Comments.Infrastructure.Persistence;

public sealed class AttachmentRepository : IAttachmentRepository
{
    private readonly AppDbContext _db;

    public AttachmentRepository(AppDbContext db)
    {
        _db = db;
    }

    public Task<Attachment?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
        => _db.Attachments.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task AddAsync(Attachment attachment, CancellationToken cancellationToken = default)
        => await _db.Attachments.AddAsync(attachment, cancellationToken);

    public void Remove(Attachment attachment) => _db.Attachments.Remove(attachment);
}

public sealed class UnitOfWork : IUnitOfWork
{
    private readonly AppDbContext _db;

    public UnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _db.SaveChangesAsync(cancellationToken);
}

public sealed class DatabaseHealthCheck : IDatabaseHealthCheck
{
    private readonly AppDbContext _db;

    public DatabaseHealthCheck(AppDbContext db)
    {
        _db = db;
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _db.Database.CanConnectAsync(cancellationToken);
        }
        catch
        {
            return false;
        }
    }
}
