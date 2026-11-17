using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.PasswordRecovery;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>
/// Outbox rows of password-reset emails. Every state change is one conditional <c>UPDATE</c>, so concurrent callers (the request, the
/// worker on several instances, a redelivered consumer) never double-claim a row and nothing depends on the change tracker.
/// </summary>
public sealed class PasswordResetDispatchStore(AppDbContext db) : IPasswordResetDispatchStore
{
    private const int ErrorMaxLength = 200;

    public async Task<ClaimedDispatch?> TryClaimAsync(Guid id, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        var claimed = await db.PasswordResetDispatches
            .Where(d => d.Id == id && d.Status == PasswordResetDispatchStatuses.Pending && d.NextAttemptAt <= now && d.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, now + lease).SetProperty(d => d.PublishAttempts, d => d.PublishAttempts + 1), cancellationToken);
        if (claimed != 1) return null;
        // The kind of email is the type of the token the row belongs to (the FK cascades, so the token always exists).
        var row = await db.PasswordResetDispatches.AsNoTracking()
            .Join(db.AuthActionTokens.AsNoTracking(), d => d.ActionTokenId, t => t.Id, (d, t) => new { Dispatch = d, t.Type })
            .FirstOrDefaultAsync(x => x.Dispatch.Id == id, cancellationToken);
        return row is null ? null : new ClaimedDispatch(row.Dispatch, row.Type);
    }

    public async Task<bool> ReturnForRetryAsync(Guid id, string url, DateTimeOffset now, string error, CancellationToken cancellationToken)
    {
        var attempts = await db.PasswordResetDispatches.AsNoTracking().Where(d => d.Id == id).Select(d => (int?)d.PublishAttempts).FirstOrDefaultAsync(cancellationToken);
        if (attempts is null) return false;
        var next = now + PasswordResetDispatcher.Backoff(attempts.Value);
        return await db.PasswordResetDispatches
            .Where(d => d.Id == id && d.Status == PasswordResetDispatchStatuses.Published && d.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, PasswordResetDispatchStatuses.Pending).SetProperty(d => d.ResetUrl, url)
                .SetProperty(d => d.NextAttemptAt, next).SetProperty(d => d.LastError, Truncate(error)), cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<Guid>> ListDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
        => await db.PasswordResetDispatches.AsNoTracking()
            .Where(d => d.Status == PasswordResetDispatchStatuses.Pending && d.NextAttemptAt <= now && d.ExpiresAt > now)
            .OrderBy(d => d.NextAttemptAt).Select(d => d.Id).Take(limit).ToListAsync(cancellationToken);

    public Task MarkPublishedAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
        => db.PasswordResetDispatches.Where(d => d.Id == id && d.Status == PasswordResetDispatchStatuses.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, PasswordResetDispatchStatuses.Published).SetProperty(d => d.PublishedAt, now)
                .SetProperty(d => d.ResetUrl, (string?)null).SetProperty(d => d.LastError, (string?)null), cancellationToken);

    public Task RecordPublishFailureAsync(Guid id, DateTimeOffset nextAttemptAt, string error, CancellationToken cancellationToken)
        => db.PasswordResetDispatches.Where(d => d.Id == id && d.Status == PasswordResetDispatchStatuses.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, nextAttemptAt).SetProperty(d => d.LastError, Truncate(error)), cancellationToken);

    public Task<int> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => db.PasswordResetDispatches.Where(d => d.Status == PasswordResetDispatchStatuses.Pending && d.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, PasswordResetDispatchStatuses.Expired).SetProperty(d => d.ResetUrl, (string?)null), cancellationToken);

    public Task<string?> GetStatusAsync(Guid id, CancellationToken cancellationToken)
        => db.PasswordResetDispatches.AsNoTracking().Where(d => d.Id == id).Select(d => d.Status).FirstOrDefaultAsync(cancellationToken);

    public Task MarkSentAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
        => db.PasswordResetDispatches.Where(d => d.Id == id && d.Status != PasswordResetDispatchStatuses.Sent)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, PasswordResetDispatchStatuses.Sent).SetProperty(d => d.SentAt, now)
                .SetProperty(d => d.ResetUrl, (string?)null).SetProperty(d => d.LastError, (string?)null), cancellationToken);

    public Task RecordSendFailureAsync(Guid id, string error, CancellationToken cancellationToken)
        => db.PasswordResetDispatches.Where(d => d.Id == id && d.Status != PasswordResetDispatchStatuses.Sent)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastError, Truncate(error)), cancellationToken);

    private static string Truncate(string error) => error.Length <= ErrorMaxLength ? error : error[..ErrorMaxLength];
}
