using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Infrastructure.Persistence.KnowledgeBase;

/// <summary>Read-only queries of the AI cost ledger (no tracking; filtering on indexed columns).</summary>
public sealed class AiUsageQueries(AppDbContext db) : IAiUsageQueries
{
    public async Task<IReadOnlyList<AiUsageRow>> ListAsync(DateTimeOffset from, DateTimeOffset to, int cap, CancellationToken cancellationToken)
        => (await Project(db.AiUsageLedger.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to).Take(cap + 1)).ToListAsync(cancellationToken))
            .OrderBy(x => x.CreatedAt).ToList();

    public async Task<(IReadOnlyList<AiUsageRow> Items, int Total)> PageAsync(DateTimeOffset from, DateTimeOffset to, string? operation, string? status, string? outcome,
        Guid? userId, int skip, int take, CancellationToken cancellationToken)
    {
        var query = db.AiUsageLedger.AsNoTracking().Where(x => x.CreatedAt >= from && x.CreatedAt < to
            && (operation == null || x.Operation == operation) && (status == null || x.Status == status) && (outcome == null || x.Outcome == outcome)
            && (userId == null || x.UserId == userId));
        var total = await query.CountAsync(cancellationToken);
        if (!db.Database.IsNpgsql())
        {
            // SQLite (tests) cannot order by DateTimeOffset in SQL.
            var all = await Project(query).ToListAsync(cancellationToken);
            return (all.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(skip).Take(take).ToList(), total);
        }
        var items = await Project(query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(skip).Take(take)).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();
        return await db.UserAccounts.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);
    }

    private static IQueryable<AiUsageRow> Project(IQueryable<AiUsageLedgerEntry> query)
        => query.Select(x => new AiUsageRow(x.Id, x.Operation, x.Status, x.Outcome, x.UserId, x.UserRole, x.EmbeddingModel, x.EmbeddingTokens, x.EmbeddingCostUsd,
            x.GenerationModel, x.InputTokens, x.CachedInputTokens, x.OutputTokens, x.ReasoningTokens, x.GenerationCostUsd, x.ReservedUsd, x.CostUsd, x.UsageReported,
            x.LatencyMs, x.ConfigVersion, x.CreatedAt, x.SettledAt, x.ReviewedByUserId, x.ReviewedAt, x.ReviewNote));
}
