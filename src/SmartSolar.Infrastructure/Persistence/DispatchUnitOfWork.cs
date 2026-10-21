using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence.Configurations.Scheduling;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>
/// Dispatch persistence over the request's <see cref="AppDbContext"/> (shared with <see cref="SchedulingUnitOfWork"/>, so dispatch rows
/// commit with the appointment in one transaction). Concurrency: the partial unique indexes (one active request per survey request,
/// one live and one accepted proposal per request), revision tokens on requests and proposals, FOR UPDATE on the request row
/// (PostgreSQL), and conditional UPDATEs for the worker's SLA metadata.
/// </summary>
public sealed class DispatchUnitOfWork(AppDbContext db) : IDispatchUnitOfWork
{
    private const string UniqueViolation = "23505";

    private IQueryable<DispatchRequest> Requests => db.Set<DispatchRequest>();
    private IQueryable<DispatchProposal> Proposals => db.Set<DispatchProposal>();

    public Task<DispatchRequest?> GetAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
        => Requests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == dispatchRequestId, cancellationToken);

    public async Task<DispatchRequest?> LockAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM dispatch_request WHERE id = {dispatchRequestId} FOR UPDATE", cancellationToken);
        return await db.Set<DispatchRequest>().FirstOrDefaultAsync(r => r.Id == dispatchRequestId, cancellationToken);
    }

    public Task<DispatchRequest?> FindActiveForSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => Requests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SurveyRequestId == surveyRequestId && (r.Status == DispatchStatuses.Open || r.Status == DispatchStatuses.ProposalPending),
                cancellationToken);

    // The active request (at most one, unique index) first, then the newest: a request reopened after a release is never shadowed by
    // the ASSIGNED one it replaced, even when both carry the same timestamp.
    public Task<DispatchRequest?> FindLatestForSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => Requests.AsNoTracking().Where(r => r.SurveyRequestId == surveyRequestId)
            .OrderByDescending(r => r.Status == DispatchStatuses.Open || r.Status == DispatchStatuses.ProposalPending)
            .ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<DispatchPreferredWindow>> ListWindowsAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
        => await db.Set<DispatchPreferredWindow>().AsNoTracking().Where(w => w.DispatchRequestId == dispatchRequestId).OrderBy(w => w.StartsAt)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DispatchProposal>> ListProposalsAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
        => await Proposals.AsNoTracking().Where(p => p.DispatchRequestId == dispatchRequestId).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);

    public Task<DispatchProposal?> FindProposalAsync(Guid proposalId, CancellationToken cancellationToken)
        => Proposals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken);

    public Task<DispatchProposal?> FindProposalForUpdateAsync(Guid proposalId, CancellationToken cancellationToken)
        => db.Set<DispatchProposal>().FirstOrDefaultAsync(p => p.Id == proposalId, cancellationToken);

    public async Task<IReadOnlyList<DispatchEvent>> ListEventsAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
        => (await db.Set<DispatchEvent>().AsNoTracking().Where(e => e.DispatchRequestId == dispatchRequestId).ToListAsync(cancellationToken))
            .OrderBy(e => e.CreatedAt).ToList();

    public async Task<IReadOnlyList<CrossZoneAuthorization>> ListAuthorizationsAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
        => (await db.Set<CrossZoneAuthorization>().AsNoTracking().Where(a => a.DispatchRequestId == dispatchRequestId).ToListAsync(cancellationToken))
            .OrderBy(a => a.AuthorizedAt).ToList();

    public async Task<(IReadOnlyList<DispatchQueueRow> Items, int Total)> ListQueueAsync(DispatchQueueFilter filter, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var query = Requests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(r => r.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Reason)) query = query.Where(r => r.Reason == filter.Reason);
        if (filter.Overdue is true) query = query.Where(r => r.Status == DispatchStatuses.Open && r.ReviewDueAt <= nowUtc);
        if (filter.Overdue is false) query = query.Where(r => r.Status != DispatchStatuses.Open || r.ReviewDueAt > nowUtc);
        var total = await query.CountAsync(cancellationToken);
        var page = await query.OrderBy(r => r.ReviewDueAt).ThenBy(r => r.Id).Skip(filter.Skip).Take(filter.Take).ToListAsync(cancellationToken);
        return (await RowsAsync(page, cancellationToken), total);
    }

    public async Task<DispatchQueueRow?> GetRowAsync(Guid dispatchRequestId, CancellationToken cancellationToken)
        => await GetAsync(dispatchRequestId, cancellationToken) is { } request ? (await RowsAsync([request], cancellationToken))[0] : null;

    /// <summary>Constant number of queries for a page: sites with customers, zones, live proposals.</summary>
    private async Task<IReadOnlyList<DispatchQueueRow>> RowsAsync(IReadOnlyList<DispatchRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0) return [];
        var siteIds = requests.Select(r => r.PropertySiteId).Distinct().ToList();
        var sites = await db.PropertySites.AsNoTracking().Where(p => siteIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Province, p.CommuneCode, CustomerName = p.Customer.User.FullName })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var zoneIds = requests.Where(r => r.OperationalZoneId != null).Select(r => r.OperationalZoneId!.Value).Distinct().ToList();
        var zones = await db.OperationalZones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, cancellationToken);
        var ids = requests.Select(r => r.Id).ToList();
        var live = await Proposals.AsNoTracking().Where(p => ids.Contains(p.DispatchRequestId) && p.Status == ProposalStatuses.Proposed)
            .ToDictionaryAsync(p => p.DispatchRequestId, cancellationToken);
        return requests.Select(r =>
        {
            var site = sites.GetValueOrDefault(r.PropertySiteId);
            var zone = r.OperationalZoneId is { } zid && zones.TryGetValue(zid, out var z) ? new ZoneRef(z.Id, z.Code, z.Name, z.IsServiceable) : null;
            return new DispatchQueueRow(r, site?.CustomerName ?? "", site?.Name ?? "", site?.Province ?? "", site?.CommuneCode, zone, live.GetValueOrDefault(r.Id));
        }).ToList();
    }

    // ---- Worker ----

    public async Task<IReadOnlyList<Guid>> ListDueProposalExpiriesAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => await Proposals.AsNoTracking().Where(p => p.Status == ProposalStatuses.Proposed && p.ExpiresAt <= nowUtc)
            .OrderBy(p => p.ExpiresAt).Take(limit).Select(p => p.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDueOverdueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => await Requests.AsNoTracking().Where(r => r.Status == DispatchStatuses.Open && r.OverdueAt == null && r.ReviewDueAt <= nowUtc)
            .OrderBy(r => r.ReviewDueAt).Take(limit).Select(r => r.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDueEscalationsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => await Requests.AsNoTracking().Where(r => r.Status == DispatchStatuses.Open && r.EscalatedAt == null && r.EscalationDueAt <= nowUtc)
            .OrderBy(r => r.EscalationDueAt).Take(limit).Select(r => r.Id).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DispatchReminderCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken cancellationToken)
        // Least recently reminded first: a request just reminded moves to the back, so a backlog larger than the batch cannot starve others.
        => await Requests.AsNoTracking().Where(r => r.Status == DispatchStatuses.Open && r.OverdueAt != null && r.ReminderIntervalMinutes > 0)
            .OrderBy(r => r.LastReminderAt ?? r.OverdueAt).ThenBy(r => r.Id).Take(limit)
            .Select(r => new DispatchReminderCandidate(r.Id, r.OverdueAt!.Value, r.LastReminderAt, r.ReminderIntervalMinutes, r.ReminderCount))
            .ToListAsync(cancellationToken);

    // SLA metadata never bumps the revision: staff actions in flight stay valid.
    public async Task<bool> TryMarkOverdueAsync(Guid dispatchRequestId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await db.Set<DispatchRequest>()
            .Where(r => r.Id == dispatchRequestId && r.Status == DispatchStatuses.Open && r.OverdueAt == null && r.ReviewDueAt <= nowUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OverdueAt, nowUtc), cancellationToken) == 1;

    public async Task<bool> TryEscalateAsync(Guid dispatchRequestId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await db.Set<DispatchRequest>()
            // Only after the overdue step recorded its event and staff notice (it runs first in every sweep), so neither is skipped.
            .Where(r => r.Id == dispatchRequestId && r.Status == DispatchStatuses.Open && r.EscalatedAt == null && r.OverdueAt != null
                        && r.EscalationDueAt <= nowUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.EscalatedAt, nowUtc), cancellationToken) == 1;

    public async Task<bool> TryRecordReminderAsync(Guid dispatchRequestId, int expectedCount, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await db.Set<DispatchRequest>()
            .Where(r => r.Id == dispatchRequestId && r.Status == DispatchStatuses.Open && r.ReminderCount == expectedCount)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.LastReminderAt, nowUtc)
                .SetProperty(r => r.ReminderCount, r => r.ReminderCount + 1), cancellationToken) == 1;

    // ---- Recipients ----

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
        => userIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await db.UserAccounts.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListActiveUserIdsWithRoleAsync(string roleCode, CancellationToken cancellationToken)
        => await db.UserAccounts.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.Status == UserStatus.Active && u.UserRoles.Any(r => r.Role.Code == roleCode))
            .OrderBy(u => u.Id).Select(u => u.Id).ToListAsync(cancellationToken);

    public async Task<Guid?> GetActiveResponsibleManagerAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var manager = await db.OperationalZones.AsNoTracking().Where(z => z.Id == zoneId).Select(z => z.ResponsibleManagerUserId).FirstOrDefaultAsync(cancellationToken);
        if (manager is not { } id) return null;
        var active = await db.UserAccounts.AsNoTracking().AnyAsync(u => u.Id == id && u.DeletedAt == null && u.Status == UserStatus.Active
            && u.UserRoles.Any(r => r.Role.Code == RoleCodes.Manager || r.Role.Code == RoleCodes.Admin), cancellationToken);
        return active ? id : null;
    }

    // ---- Writes ----

    public void Add<TEntity>(TEntity entity) where TEntity : class => db.Set<TEntity>().Add(entity);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new DispatchConflictException(DispatchConflictException.Stale, ex);
        }
        catch (DbUpdateException ex)
        {
            var (state, constraint) = DbExceptionClassifier.PostgresDetails(ex);
            if ((state == UniqueViolation && constraint == DispatchRequestConfiguration.ActiveSurveyRequestIndex)
                || DbExceptionClassifier.IsSqliteUniqueOn(ex, "dispatch_request.survey_request_id"))
                throw new DispatchConflictException(DispatchConflictException.ActiveRequestExists, ex);
            if ((state == UniqueViolation && constraint is DispatchProposalConfiguration.LiveIndex or DispatchProposalConfiguration.AcceptedIndex)
                || DbExceptionClassifier.IsSqliteUniqueOn(ex, "dispatch_proposal.dispatch_request_id"))
                throw new DispatchConflictException(DispatchConflictException.ProposalExists, ex);
            throw;
        }
    }
}
