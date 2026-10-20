using SmartSolar.Modules.Scheduling.Contracts.Persistence;

namespace SmartSolar.Modules.Scheduling.Dispatch;

/// <summary>Raised when the database rejects a dispatch write because of a concurrent change.</summary>
public sealed class DispatchConflictException(string kind, Exception inner) : Exception($"Dispatch conflict: {kind}.", inner)
{
    /// <summary>23505 on ux_dispatch_request_active_survey_request: a second OPEN/PROPOSAL_PENDING request for one survey request.</summary>
    public const string ActiveRequestExists = "ACTIVE_REQUEST_EXISTS";

    /// <summary>23505 on ux_dispatch_proposal_live / ux_dispatch_proposal_accepted: a second live (or accepted) proposal.</summary>
    public const string ProposalExists = "PROPOSAL_EXISTS";

    /// <summary>The revision token changed since the row was read.</summary>
    public const string Stale = "STALE";

    public string Kind { get; } = kind;
}

public sealed record DispatchQueueFilter(string? Status, string? Reason, bool? Overdue, int Skip, int Take);

/// <summary>A dispatch request with what the staff queue shows next to it.</summary>
public sealed record DispatchQueueRow(DispatchRequest Request, string CustomerName, string PropertyName, string Province, string? CommuneCode,
    ZoneRef? Zone, DispatchProposal? LiveProposal);

/// <summary>Worker input for reminders: an OPEN, overdue request whose snapshot enables reminders.</summary>
public sealed record DispatchReminderCandidate(Guid Id, DateTimeOffset OverdueAt, DateTimeOffset? LastReminderAt, int ReminderIntervalMinutes, int ReminderCount);

/// <summary>
/// Dispatch persistence. It shares the request's DbContext with <see cref="ISchedulingUnitOfWork"/>, so dispatch rows written inside
/// a scheduling transaction (direct assignment, proposal acceptance) commit or roll back with the appointment. Lock order everywhere:
/// technician_profile → property_site → operational_zone (FOR SHARE) → dispatch_request → dispatch_proposal. Workers and staff
/// actions that do not book take only dispatch_request → dispatch_proposal. SQLite (tests) skips the row locks: one writer at a time.
/// </summary>
public interface IDispatchUnitOfWork
{
    Task<DispatchRequest?> GetAsync(Guid dispatchRequestId, CancellationToken cancellationToken);

    /// <summary>Row-locks the request (FOR UPDATE on PostgreSQL) and returns it tracked; null when it does not exist.</summary>
    Task<DispatchRequest?> LockAsync(Guid dispatchRequestId, CancellationToken cancellationToken);

    /// <summary>The OPEN/PROPOSAL_PENDING request of the survey request, if any.</summary>
    Task<DispatchRequest?> FindActiveForSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>The newest request of the survey request (any status), if any.</summary>
    Task<DispatchRequest?> FindLatestForSurveyRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    Task<IReadOnlyList<DispatchPreferredWindow>> ListWindowsAsync(Guid dispatchRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DispatchProposal>> ListProposalsAsync(Guid dispatchRequestId, CancellationToken cancellationToken);
    Task<DispatchProposal?> FindProposalAsync(Guid proposalId, CancellationToken cancellationToken);

    /// <summary>The proposal, tracked (call after <see cref="LockAsync"/> of its request: lock order request → proposal).</summary>
    Task<DispatchProposal?> FindProposalForUpdateAsync(Guid proposalId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DispatchEvent>> ListEventsAsync(Guid dispatchRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CrossZoneAuthorization>> ListAuthorizationsAsync(Guid dispatchRequestId, CancellationToken cancellationToken);

    /// <summary>The staff queue, oldest review deadline first; <see cref="DispatchQueueFilter.Overdue"/> filters on (overdue_at set or review_due_at passed) while OPEN.</summary>
    Task<(IReadOnlyList<DispatchQueueRow> Items, int Total)> ListQueueAsync(DispatchQueueFilter filter, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<DispatchQueueRow?> GetRowAsync(Guid dispatchRequestId, CancellationToken cancellationToken);

    // ---- Worker (persisted deadlines only) ----

    /// <summary>PROPOSED proposals with expires_at &lt;= now, oldest first.</summary>
    Task<IReadOnlyList<Guid>> ListDueProposalExpiriesAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>OPEN requests with review_due_at &lt;= now and no overdue_at yet.</summary>
    Task<IReadOnlyList<Guid>> ListDueOverdueAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>OPEN requests with escalation_due_at &lt;= now and no escalated_at yet.</summary>
    Task<IReadOnlyList<Guid>> ListDueEscalationsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>OPEN, overdue requests whose snapshot interval is &gt; 0.</summary>
    Task<IReadOnlyList<DispatchReminderCandidate>> ListReminderCandidatesAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Conditional: overdue_at = now while OPEN, not yet overdue and review_due_at &lt;= now.</summary>
    Task<bool> TryMarkOverdueAsync(Guid dispatchRequestId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Conditional: escalated_at = now (and overdue_at when missing) while OPEN, not yet escalated and escalation_due_at &lt;= now.</summary>
    Task<bool> TryEscalateAsync(Guid dispatchRequestId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Conditional: last_reminder_at = now, reminder_count + 1 while OPEN and reminder_count is still <paramref name="expectedCount"/>.</summary>
    Task<bool> TryRecordReminderAsync(Guid dispatchRequestId, int expectedCount, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // ---- Recipients ----

    /// <summary>Display names of the given users in one query (unknown ids are absent).</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);

    /// <summary>Active, non-deleted users holding the role.</summary>
    Task<IReadOnlyList<Guid>> ListActiveUserIdsWithRoleAsync(string roleCode, CancellationToken cancellationToken);

    /// <summary>The zone's responsible manager when set and still an active Manager/Admin.</summary>
    Task<Guid?> GetActiveResponsibleManagerAsync(Guid zoneId, CancellationToken cancellationToken);

    // ---- Writes ----
    void Add<TEntity>(TEntity entity) where TEntity : class;

    /// <summary>Saves tracked changes; throws <see cref="DispatchConflictException"/> for the partial unique indexes and stale revisions.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
