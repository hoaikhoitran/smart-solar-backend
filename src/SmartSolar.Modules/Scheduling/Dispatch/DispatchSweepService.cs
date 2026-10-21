using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;

namespace SmartSolar.Modules.Scheduling.Dispatch;

/// <summary>
/// The dispatch worker's sweep. It acts ONLY on persisted snapshots (review_due_at, escalation_due_at, the reminder interval stored on
/// the request, a proposal's expires_at) — never on the current settings — and every change is conditional, so several API instances,
/// retries and concurrent customer/staff actions never double-apply anything:
/// <list type="bullet">
/// <item>Proposal expiry: PROPOSED with expires_at ≤ now → EXPIRED, and the request PROPOSAL_PENDING → OPEN (dispatch lock, then the
/// proposal row: the same lock order as acceptance). Never touches the request's terminal state or any appointment.</item>
/// <item>Overdue: OPEN with review_due_at ≤ now → overdue_at (staff notified once).</item>
/// <item>Escalation: OPEN with escalation_due_at ≤ now → escalated_at (Admins notified once).</item>
/// <item>Reminders: OPEN and overdue, snapshot interval &gt; 0, (last reminder or overdue_at) + interval ≤ now → one reminder
/// (deduplicated on reminder_count).</item>
/// </list>
/// The sweep NEVER changes a dispatch request to a terminal state: requests do not expire. SLA metadata does not bump the request's
/// revision, so staff actions in flight are not invalidated by it. One failing row is logged (id only) and retried on the next run.
/// </summary>
public sealed class DispatchSweepService(ISchedulingUnitOfWork scheduling, IDispatchUnitOfWork dispatch, TimeProvider time, ILogger<DispatchSweepService>? logger = null)
{
    public sealed record SweepResult(int ExpiredProposals, int MarkedOverdue, int Escalated, int Reminders)
    {
        public int Total => ExpiredProposals + MarkedOverdue + Escalated + Reminders;
    }

    public async Task<SweepResult> RunOnceAsync(int limit, CancellationToken cancellationToken)
        => new(await ExpireProposalsAsync(limit, cancellationToken), await MarkOverdueAsync(limit, cancellationToken),
            await EscalateAsync(limit, cancellationToken), await RemindAsync(limit, cancellationToken));

    public async Task<int> ExpireProposalsAsync(int limit, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var id in await dispatch.ListDueProposalExpiriesAsync(time.GetUtcNow(), limit, cancellationToken))
            if (await SafeAsync(id, "Expiring dispatch proposal", () => TryExpireProposalAsync(id, cancellationToken))) count++;
        return count;
    }

    /// <summary>
    /// Expires one proposal when it is still PROPOSED and its persisted expires_at passed; false otherwise (already answered, accepted,
    /// withdrawn, or expired by a concurrent sweep). Shared with an acceptance that arrives after the expiry.
    /// </summary>
    public async Task<bool> TryExpireProposalAsync(Guid proposalId, CancellationToken cancellationToken)
    {
        var target = (await dispatch.FindProposalAsync(proposalId, cancellationToken));
        if (target is null) return false;
        try
        {
            return await scheduling.ExecuteInTransactionAsync<bool>(async token =>
            {
                var request = await dispatch.LockAsync(target.DispatchRequestId, token);
                var proposal = await dispatch.FindProposalForUpdateAsync(proposalId, token);
                var now = time.GetUtcNow();
                if (request is null || proposal is null || proposal.Status != ProposalStatuses.Proposed || proposal.ExpiresAt > now) return (false, false);
                proposal.Status = ProposalStatuses.Expired;
                proposal.Revision++;
                if (request.Status == DispatchStatuses.ProposalPending)
                {
                    request.Status = DispatchStatuses.Open;
                    request.UpdatedAt = now;
                    request.Revision++;
                }
                DispatchHistory.Record(dispatch, request.Id, DispatchEventTypes.ProposalExpired, null, now, $"expired at {proposal.ExpiresAt:O}", proposal.Id,
                    [proposal.ProposedByUserId, request.CustomerUserId]);
                await dispatch.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken);
        }
        catch (DispatchConflictException)
        {
            return false;   // somebody answered it at the same moment: their change stands
        }
    }

    public async Task<int> MarkOverdueAsync(int limit, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var id in await dispatch.ListDueOverdueAsync(time.GetUtcNow(), limit, cancellationToken))
            if (await SafeAsync(id, "Marking dispatch request overdue", () => scheduling.ExecuteInTransactionAsync<bool>(async token =>
                {
                    var now = time.GetUtcNow();
                    if (!await dispatch.TryMarkOverdueAsync(id, now, token)) return (false, false);
                    var request = await dispatch.GetAsync(id, token);
                    // Admins hear about an overdue request once, through the escalation: when it is due right away, leave them out here.
                    var recipients = await DispatchHistory.StaffRecipientsAsync(dispatch, request!, token);
                    if (request!.EscalationDueAt <= now) recipients = await WithoutAdminsAsync(recipients, token);
                    DispatchHistory.Record(dispatch, id, DispatchEventTypes.Overdue, null, now, $"review was due {request.ReviewDueAt:O}", null, recipients);
                    await dispatch.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken))) count++;
        return count;
    }

    public async Task<int> EscalateAsync(int limit, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var id in await dispatch.ListDueEscalationsAsync(time.GetUtcNow(), limit, cancellationToken))
            if (await SafeAsync(id, "Escalating dispatch request", () => scheduling.ExecuteInTransactionAsync<bool>(async token =>
                {
                    var now = time.GetUtcNow();
                    if (!await dispatch.TryEscalateAsync(id, now, token)) return (false, false);
                    var request = await dispatch.GetAsync(id, token);
                    var admins = await dispatch.ListActiveUserIdsWithRoleAsync(RoleCodes.Admin, token);
                    DispatchHistory.Record(dispatch, id, DispatchEventTypes.Escalated, null, now,
                        $"review was due {request!.ReviewDueAt:O}; escalation due {request.EscalationDueAt:O}", null, admins);
                    await dispatch.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken))) count++;
        return count;
    }

    public async Task<int> RemindAsync(int limit, CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        foreach (var candidate in await dispatch.ListReminderCandidatesAsync(limit, cancellationToken))
        {
            var due = (candidate.LastReminderAt ?? candidate.OverdueAt).AddMinutes(candidate.ReminderIntervalMinutes);
            if (due > now) continue;
            if (await SafeAsync(candidate.Id, "Reminding about dispatch request", () => scheduling.ExecuteInTransactionAsync<bool>(async token =>
                {
                    var at = time.GetUtcNow();
                    if (!await dispatch.TryRecordReminderAsync(candidate.Id, candidate.ReminderCount, at, token)) return (false, false);
                    var request = await dispatch.GetAsync(candidate.Id, token);
                    // Reminders are for the reviewing staff; Admins were notified once by the escalation.
                    var recipients = await DispatchHistory.StaffRecipientsAsync(dispatch, request!, token);
                    if (request!.EscalatedAt is not null) recipients = await WithoutAdminsAsync(recipients, token);
                    DispatchHistory.Record(dispatch, candidate.Id, DispatchEventTypes.Reminder, null, at, $"reminder {candidate.ReminderCount + 1}", null, recipients);
                    await dispatch.SaveChangesAsync(token);
                    return (true, true);
                }, cancellationToken))) count++;
        }
        return count;
    }

    private async Task<IReadOnlyList<Guid>> WithoutAdminsAsync(IReadOnlyList<Guid> recipients, CancellationToken token)
    {
        if (recipients.Count == 0) return recipients;
        var admins = (await dispatch.ListActiveUserIdsWithRoleAsync(RoleCodes.Admin, token)).ToHashSet();
        return recipients.Where(r => !admins.Contains(r)).ToList();
    }

    private async Task<bool> SafeAsync(Guid id, string what, Func<Task<bool>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ids only: exception messages may carry row data.
            logger?.LogWarning("{What} {DispatchId} failed ({ErrorType}); retrying on the next run.", what, id, ex.GetType().Name);
            return false;
        }
    }
}
