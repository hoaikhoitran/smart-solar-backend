using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>Transaction and conflict translation shared by the Installation Flow units of work.</summary>
public abstract class TransactionalUnitOfWork(AppDbContext db) : ITransactionalUnitOfWork
{
    private const string PostgresUnique = "23505";
    private const string PostgresExclusion = "23P01";

    protected AppDbContext Db { get; } = db;

    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken)
    {
        var strategy = Db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(cancellationToken, async token =>
        {
            Db.ChangeTracker.Clear();
            await using var transaction = await Db.Database.BeginTransactionAsync(token);
            try
            {
                var (result, commit) = await work(token);
                if (commit) await transaction.CommitAsync(token);
                else await transaction.RollbackAsync(token);
                return result;
            }
            finally
            {
                Db.ChangeTracker.Clear();
            }
        });
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new PersistenceConflictException(PersistenceConflictException.StaleRevision, null, ex);
        }
        catch (DbUpdateException ex)
        {
            var (state, constraint) = DbExceptionClassifier.PostgresDetails(ex);
            if (state == PostgresExclusion) throw new PersistenceConflictException(PersistenceConflictException.ExclusionViolation, constraint, ex);
            if (state == PostgresUnique || DbExceptionClassifier.IsUniqueViolation(ex))
                throw new PersistenceConflictException(PersistenceConflictException.UniqueViolation, constraint ?? SqliteConstraint(ex), ex);
            throw;
        }
    }

    /// <summary>SQLite reports "UNIQUE constraint failed: table.column[, table.column]"; that text stands in for the index name.</summary>
    private static string? SqliteConstraint(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? "";
        var index = message.IndexOf("UNIQUE constraint failed:", StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : message[(index + "UNIQUE constraint failed:".Length)..].Trim().TrimEnd('\'', '.');
    }

    /// <summary>Row lock on the given technicians' scheduling profiles, in id order (PostgreSQL only; SQLite serialises writers).</summary>
    protected async Task LockTechnicianProfilesAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken)
    {
        if (!Db.Database.IsNpgsql()) return;
        foreach (var id in technicianUserIds.Distinct().OrderBy(x => x))
            await Db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM technician_profile WHERE user_id = {id} FOR UPDATE", cancellationToken);
    }
}
