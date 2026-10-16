using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.KnowledgeBase;

namespace SmartSolar.Infrastructure.Persistence.KnowledgeBase;

public sealed class KnowledgeUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IKnowledgeUnitOfWork
{
    public Task<KnowledgeDocument?> FindDocumentAsync(Guid documentId, bool track, CancellationToken cancellationToken)
        => (track ? Db.KnowledgeDocuments : Db.KnowledgeDocuments.AsNoTracking()).FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeDocument>> ListDocumentsAsync(string? status, string? category, CancellationToken cancellationToken)
        => (await Db.KnowledgeDocuments.AsNoTracking().Where(d => (status == null || d.Status == status) && (category == null || d.Category == category)).ToListAsync(cancellationToken))
            .OrderByDescending(d => d.UpdatedAt).ToList();

    public Task<KnowledgeDocumentVersion?> FindVersionAsync(Guid versionId, bool track, CancellationToken cancellationToken)
        => (track ? Db.KnowledgeDocumentVersions : Db.KnowledgeDocumentVersions.AsNoTracking()).FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);

    public async Task<IReadOnlyList<KnowledgeDocumentVersion>> GetVersionsAsync(Guid documentId, bool track, CancellationToken cancellationToken)
        => await (track ? Db.KnowledgeDocumentVersions : Db.KnowledgeDocumentVersions.AsNoTracking()).Where(v => v.DocumentId == documentId).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<KnowledgeIngestionJob>> GetJobsAsync(Guid documentId, CancellationToken cancellationToken)
        => await Db.KnowledgeIngestionJobs.AsNoTracking()
            .Where(j => Db.KnowledgeDocumentVersions.Any(v => v.Id == j.VersionId && v.DocumentId == documentId)).ToListAsync(cancellationToken);

    public Task<KnowledgeIngestionJob?> FindJobAsync(Guid jobId, bool track, CancellationToken cancellationToken)
        => (track ? Db.KnowledgeIngestionJobs : Db.KnowledgeIngestionJobs.AsNoTracking()).FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

    public Task<int> CountJobsAsync(Guid versionId, string kind, CancellationToken cancellationToken)
        => Db.KnowledgeIngestionJobs.AsNoTracking().CountAsync(j => j.VersionId == versionId && j.Kind == kind, cancellationToken);

    public async Task<bool> TryLeaseJobAsync(Guid jobId, DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken cancellationToken)
        => await Due(now).Where(j => j.Id == jobId && j.Attempts < j.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Running).SetProperty(j => j.LockedUntil, leaseUntil)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1).SetProperty(j => j.UpdatedAt, now), cancellationToken) == 1;

    public async Task<bool> TryFailExhaustedJobAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
        => await Due(now).Where(j => j.Id == jobId && j.Attempts >= j.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Failed).SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.LastError, "No attempts left (the last run did not finish).").SetProperty(j => j.UpdatedAt, now), cancellationToken) == 1;

    public async Task<bool> TryReleaseLeaseAsync(Guid jobId, DateTimeOffset leaseUntil, DateTimeOffset now, CancellationToken cancellationToken)
        => await Db.KnowledgeIngestionJobs.Where(j => j.Id == jobId && j.Status == JobStatuses.Running && j.LockedUntil == leaseUntil)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, JobStatuses.Pending).SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.Attempts, j => j.Attempts - 1).SetProperty(j => j.NextAttemptAt, now).SetProperty(j => j.UpdatedAt, now), cancellationToken) == 1;

    private IQueryable<KnowledgeIngestionJob> Due(DateTimeOffset now)
        => Db.KnowledgeIngestionJobs.Where(j => (j.Status == JobStatuses.Pending || j.Status == JobStatuses.RetryWaiting
                                                 || (j.Status == JobStatuses.Running && j.LockedUntil < now)) && j.NextAttemptAt <= now);

    public async Task<IReadOnlyList<Guid>> ListDueJobsAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
        => await Db.KnowledgeIngestionJobs.AsNoTracking()
            .Where(j => (j.Status == JobStatuses.Pending || j.Status == JobStatuses.RetryWaiting || (j.Status == JobStatuses.Running && j.LockedUntil < now)) && j.NextAttemptAt <= now)
            .OrderBy(j => j.NextAttemptAt).Take(limit).Select(j => j.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RagAllowedVersion>> GetRetrievableVersionsAsync(CancellationToken cancellationToken)
        => await Db.KnowledgeDocuments.AsNoTracking()
            .Where(d => d.Status == DocumentStatuses.Published && d.PublishedVersionId != null)
            .Join(Db.KnowledgeDocumentVersions, d => d.PublishedVersionId, v => v.Id, (d, v) => new { d, v })
            .Where(x => x.v.Status == VersionStatuses.Published)
            .Select(x => new RagAllowedVersion(x.v.Id, x.d.Id, x.d.Title, x.v.VersionNumber))
            .ToListAsync(cancellationToken);

    public Task<ChatSession?> FindSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        => Db.ChatSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public async Task<IReadOnlyList<ChatSession>> ListSessionsAsync(Guid userId, CancellationToken cancellationToken)
        => (await Db.ChatSessions.AsNoTracking().Where(s => s.UserId == userId).ToListAsync(cancellationToken)).OrderByDescending(s => s.UpdatedAt).Take(100).ToList();

    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(Guid sessionId, int? lastCount, CancellationToken cancellationToken)
    {
        var all = (await Db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sessionId).ToListAsync(cancellationToken)).OrderBy(m => m.CreatedAt).ToList();
        return lastCount is { } n ? all.TakeLast(n).ToList() : all;
    }

    public Task<ChatMessage?> FindMessageByClientIdAsync(Guid sessionId, string clientMessageId, CancellationToken cancellationToken)
        => Db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.SessionId == sessionId && m.ClientMessageId == clientMessageId, cancellationToken);

    public Task<ChatMessage?> FindReplyAsync(Guid userMessageId, CancellationToken cancellationToken)
        => Db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m => m.ReplyToMessageId == userMessageId, cancellationToken);

    public async Task<IReadOnlyList<ChatCitation>> GetCitationsAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        var ids = messageIds.ToList();
        return await Db.ChatCitations.AsNoTracking().Where(c => ids.Contains(c.MessageId)).ToListAsync(cancellationToken);
    }

    public Task<UsageReservation> TryReserveUsageAsync(Guid userId, DateOnly date, long tokens, RagOptions limits, DateTimeOffset now,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
        => ExecuteInTransactionAsync<UsageReservation>(async token =>
        {
            var (user, global) = await LockLedgerAsync(userId, date, now, token);

            // Expired, never-completed reservations (crashed or abandoned requests) are charged at their estimate.
            var expired = await Db.RagUsageReservations.Where(r => r.UserId == userId && r.UsageDate == date && r.ExpiresAt <= now).ToListAsync(token);
            if (expired.Count > 0)
            {
                var estimate = expired.Sum(r => r.ReservedTokens);
                user.EstimatedTokens += estimate;
                global.EstimatedTokens += estimate;
                Db.RagUsageReservations.RemoveRange(expired);
            }

            var open = await Db.RagUsageReservations.Where(r => r.UserId == userId && r.UsageDate == date && r.ExpiresAt > now).Select(r => r.ReservedTokens).ToListAsync(token);
            var everyone = await Db.RagUsageReservations.Where(r => r.UsageDate == date && r.UserId != userId).Select(r => r.ReservedTokens).ToListAsync(token);

            string? limit = null;
            if (open.Count >= limits.MaxConcurrentGenerationsPerUser) limit = "CONCURRENCY";
            else if (user.Requests >= limits.DailyRequestsPerUser || user.ChargedTokens + open.Sum() + tokens > limits.DailyTokensPerUser) limit = "USER_QUOTA";
            else if (global.ChargedTokens + open.Sum() + everyone.Sum() + tokens > limits.GlobalDailyTokens) limit = "GLOBAL_QUOTA";

            Guid? reservationId = null;
            if (limit is null)
            {
                reservationId = Guid.NewGuid();
                Db.RagUsageReservations.Add(new RagUsageReservation
                {
                    Id = reservationId.Value, UserId = userId, UsageDate = date, ReservedTokens = tokens, CreatedAt = now, ExpiresAt = expiresAt,
                });
                user.Requests++;
                global.Requests++;
                user.UpdatedAt = global.UpdatedAt = now;
            }
            // Expired reservations are converted even when the new request is refused.
            await SaveChangesAsync(token);
            return (new UsageReservation(reservationId, limit), true);
        }, cancellationToken);

    public Task<bool> CompleteUsageAsync(Guid reservationId, long inputTokens, long outputTokens, DateTimeOffset now, CancellationToken cancellationToken)
        => ExecuteInTransactionAsync<bool>(async token =>
        {
            var reservation = await Db.RagUsageReservations.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reservationId, token);
            if (reservation is null) return (false, false);
            var (user, global) = await LockLedgerAsync(reservation.UserId, reservation.UsageDate, now, token);
            // Re-read under the ledger locks: a concurrent completion or expiry conversion may have won.
            var tracked = await Db.RagUsageReservations.FirstOrDefaultAsync(r => r.Id == reservationId, token);
            if (tracked is null) return (false, false);
            Db.RagUsageReservations.Remove(tracked);
            user.InputTokens += Math.Max(0, inputTokens);
            user.OutputTokens += Math.Max(0, outputTokens);
            global.InputTokens += Math.Max(0, inputTokens);
            global.OutputTokens += Math.Max(0, outputTokens);
            user.UpdatedAt = global.UpdatedAt = now;
            await SaveChangesAsync(token);
            return (true, true);
        }, cancellationToken);

    /// <summary>
    /// Creates the day's global and user ledger rows if needed and locks them, global first, so every
    /// reservation and completion takes the locks in the same order (no deadlocks across replicas).
    /// </summary>
    private async Task<(RagUsageDaily User, RagUsageDaily Global)> LockLedgerAsync(Guid userId, DateOnly date, DateTimeOffset now, CancellationToken token)
    {
        var ids = new[] { Guid.Empty, userId }.Distinct().ToList();
        if (Db.Database.IsNpgsql())
        {
            foreach (var id in ids)
                await Db.Database.ExecuteSqlInterpolatedAsync(
                    $"INSERT INTO rag_usage_daily (user_id, usage_date, requests, input_tokens, output_tokens, estimated_tokens, updated_at) VALUES ({id}, {date}, 0, 0, 0, 0, {now.ToUniversalTime()}) ON CONFLICT DO NOTHING",
                    token);
            await Db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM rag_usage_daily WHERE usage_date = {date} AND user_id = {Guid.Empty} FOR UPDATE", token);
            await Db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM rag_usage_daily WHERE usage_date = {date} AND user_id = {userId} FOR UPDATE", token);
        }
        else
        {
            // SQLite (tests) serialises writers; create the rows through EF.
            foreach (var id in ids)
                if (!await Db.RagUsageDaily.AnyAsync(u => u.UserId == id && u.UsageDate == date, token))
                    Db.RagUsageDaily.Add(new RagUsageDaily { UserId = id, UsageDate = date, UpdatedAt = now });
            await Db.SaveChangesAsync(token);
        }
        var global = await Db.RagUsageDaily.SingleAsync(u => u.UserId == Guid.Empty && u.UsageDate == date, token);
        var user = userId == Guid.Empty ? global : await Db.RagUsageDaily.SingleAsync(u => u.UserId == userId && u.UsageDate == date, token);
        return (user, global);
    }

    public void Add<TEntity>(TEntity entity) where TEntity : class => Db.Set<TEntity>().Add(entity);
}
