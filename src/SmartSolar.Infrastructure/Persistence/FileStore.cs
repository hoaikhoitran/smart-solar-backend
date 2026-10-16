using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Files;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class FileStore(AppDbContext db) : IFileStore
{
    public Task<StoredFile?> FindAsync(Guid fileId, CancellationToken cancellationToken)
        => db.StoredFiles.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

    public async Task<IReadOnlyList<StoredFile>> FindManyAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        var ids = fileIds.ToList();
        return await db.StoredFiles.AsNoTracking().Where(f => ids.Contains(f.Id)).ToListAsync(cancellationToken);
    }

    public Task<bool> ExistsAsync(string bucket, string objectKey, CancellationToken cancellationToken)
        => db.StoredFiles.AsNoTracking().AnyAsync(f => f.Bucket == bucket && f.ObjectKey == objectKey, cancellationToken);

    public void Add(StoredFile file) => db.StoredFiles.Add(file);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new FileConcurrencyException(ex);
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new DuplicateObjectKeyException(ex);
        }
    }
}
