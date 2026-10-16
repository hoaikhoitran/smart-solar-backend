namespace SmartSolar.Modules.Common.Persistence;

/// <summary>
/// Raised by Installation Flow units of work when the database rejects a write because of a concurrent change:
/// a stale revision (optimistic concurrency), a unique index or an exclusion constraint.
/// <see cref="Constraint"/> carries the database constraint name when known, so handlers can map it
/// to a precise business answer.
/// </summary>
public sealed class PersistenceConflictException(string kind, string? constraint, Exception inner)
    : Exception($"Persistence conflict: {kind} ({constraint ?? "n/a"}).", inner)
{
    public const string StaleRevision = "STALE_REVISION";
    public const string UniqueViolation = "UNIQUE_VIOLATION";
    public const string ExclusionViolation = "EXCLUSION_VIOLATION";

    public string Kind { get; } = kind;
    public string? Constraint { get; } = constraint;
}

/// <summary>Short database transaction boundary shared by the Installation Flow units of work.</summary>
public interface ITransactionalUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="work"/> in one transaction inside the retrying execution strategy and
    /// commits only when it returns Commit = true. Never call external providers inside.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken);

    /// <summary>Saves tracked changes; throws <see cref="PersistenceConflictException"/> for conflicts.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
