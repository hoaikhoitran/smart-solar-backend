using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Identity.Constants;

namespace SmartSolar.Modules.AfterSales;

/// <summary>A Sales contact attempt with a customer (maintenance or acceptance follow-up).</summary>
public sealed record FollowUpInput(DateTimeOffset ContactedAt, string? Result, string? Note, string? Outcome);

/// <summary>
/// Shared Sales work queue. Every active Sales user sees OPEN tasks; the first to claim owns it (the revision check makes the claim
/// atomic). A Sales user cannot hand a task back; a Manager assigns open tasks or reassigns with a reason. No employee SLA timers.
/// </summary>
public sealed class SalesTaskHandlers(IAfterSalesUnitOfWork data, AfterSalesViews views, TimeProvider time)
{
    public async Task<IReadOnlyList<SalesTaskView>> ListOpenAsync(CancellationToken cancellationToken)
        => (await data.ListTasksAsync(SalesTaskStatuses.Open, null, cancellationToken)).Select(views.Task).ToList();

    public async Task<IReadOnlyList<SalesTaskView>> ListMineAsync(Guid salesUserId, CancellationToken cancellationToken)
        => (await data.ListTasksAsync(null, salesUserId, cancellationToken)).Where(t => SalesTaskStatuses.IsOpen(t.Status)).Select(views.Task).ToList();

    public async Task<IReadOnlyList<SalesTaskView>> ListAllAsync(string? status, CancellationToken cancellationToken)
        => (await data.ListTasksAsync(status, null, cancellationToken)).Select(views.Task).ToList();

    /// <summary>OPEN → CLAIMED by the caller. Two Sales users claiming at the same time: exactly one wins, the other gets 409.</summary>
    public Task<OperationResult<SalesTaskView>> ClaimAsync(Guid salesUserId, Guid taskId, CancellationToken cancellationToken)
        => MutateAsync(taskId, (task, now, _) =>
        {
            if (task.Status != SalesTaskStatuses.Open)
                return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.AlreadyClaimed, "Another Sales user already took this task."));
            task.Status = SalesTaskStatuses.Claimed;
            task.AssignedSalesUserId = salesUserId;
            task.ClaimedAt = now;
            AfterSalesHistory.Record(data, EventSubjects.SalesTask, task.Id, "CLAIMED", salesUserId, null, now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    public Task<OperationResult<SalesTaskView>> StartAsync(Guid salesUserId, Guid taskId, CancellationToken cancellationToken)
        => MutateAsync(taskId, (task, now, _) =>
        {
            if (task.AssignedSalesUserId != salesUserId || task.Status != SalesTaskStatuses.Claimed)
                return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "Only your claimed task can be started."));
            task.Status = SalesTaskStatuses.InProgress;
            task.StartedAt = now;
            AfterSalesHistory.Record(data, EventSubjects.SalesTask, task.Id, "STARTED", salesUserId, null, now);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);

    /// <summary>Manager / Admin assigns an open task, or reassigns an owned one (reason required).</summary>
    public async Task<OperationResult<SalesTaskView>> AssignAsync(Guid managerUserId, Guid taskId, Guid salesUserId, string? reason, CancellationToken cancellationToken)
    {
        if (!await data.IsActiveUserWithRoleAsync(salesUserId, RoleCodes.Sales, cancellationToken))
            return OperationResult<SalesTaskView>.Fail(AfterSalesErrorCodes.ValidationFailed, "The assignee is not an active Sales user.");
        var why = Text.Trim(reason, 500);
        return await MutateAsync(taskId, (task, now, _) =>
        {
            if (!SalesTaskStatuses.IsOpen(task.Status)) return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "The task is closed."));
            var previous = task.AssignedSalesUserId;
            if (previous is not null && why is null) return Task.FromResult(OperationResult<bool>.Fail(AfterSalesErrorCodes.ReasonRequired, "A reassignment needs a reason."));
            task.AssignedSalesUserId = salesUserId;
            task.Status = task.Status == SalesTaskStatuses.Open ? SalesTaskStatuses.Claimed : task.Status;
            task.ClaimedAt ??= now;
            AfterSalesHistory.Record(data, EventSubjects.SalesTask, task.Id, previous is null ? "ASSIGNED" : "REASSIGNED", managerUserId,
                previous is null ? $"to {salesUserId}" : $"{previous} → {salesUserId}: {why}", now);
            AfterSalesHistory.Notify(data, salesUserId, "SALES_TASK_ASSIGNED", $"A {task.TaskType} task was assigned to you.", now, task.RequestId);
            return Task.FromResult(OperationResult<bool>.Ok(true));
        }, cancellationToken);
    }

    /// <summary>
    /// Records a contact attempt (time, result, notes). A final outcome closes the task: UNREACHABLE marks the planned maintenance visit
    /// NOT_COMPLETED (it is not consumed and may still be booked while the plan is active).
    /// </summary>
    public Task<OperationResult<SalesTaskView>> RecordFollowUpAsync(Guid salesUserId, Guid taskId, FollowUpInput input, CancellationToken cancellationToken)
    {
        if (input.Outcome is not null && !FollowUpOutcomes.All.Contains(input.Outcome))
            return Task.FromResult(OperationResult<SalesTaskView>.Fail(AfterSalesErrorCodes.ValidationFailed, $"Outcome must be one of {string.Join(", ", FollowUpOutcomes.All)}."));
        var result = Text.Trim(input.Result, 300);
        if (result is null) return Task.FromResult(OperationResult<SalesTaskView>.Fail(AfterSalesErrorCodes.ValidationFailed, "Record the contact result."));
        return MutateAsync(taskId, async (task, now, token) =>
        {
            if (task.AssignedSalesUserId != salesUserId || !SalesTaskStatuses.IsOpen(task.Status))
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "Only your open task can be updated.");
            if (task.TaskType is not (SalesTaskTypes.MaintenanceFollowUp or SalesTaskTypes.AcceptanceFollowUp))
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "Quotation tasks are completed by issuing the quotation.");
            AfterSalesHistory.Record(data, EventSubjects.SalesTask, task.Id, "CONTACT_ATTEMPT", salesUserId,
                $"at {input.ContactedAt.ToUniversalTime():O}: {result}; {Text.Trim(input.Note, 1000)}", now);
            task.Note = Text.Trim(input.Note, 2000) ?? task.Note;
            task.StartedAt ??= now;
            if (task.Status == SalesTaskStatuses.Claimed) task.Status = SalesTaskStatuses.InProgress;
            if (input.Outcome is { } outcome && outcome != FollowUpOutcomes.WillBookLater)
            {
                task.Status = SalesTaskStatuses.Done;
                task.Outcome = outcome;
                task.CompletedAt = now;
                if (outcome == FollowUpOutcomes.Unreachable && task.EntitlementId is { } entitlementId)
                    await MarkNotCompletedAsync(data, entitlementId, now, token);
            }
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>The planned visit could not be arranged (customer unreachable): not consumed, still bookable while the term is active.</summary>
    public static async Task MarkNotCompletedAsync(IAfterSalesUnitOfWork data, Guid entitlementId, DateTimeOffset now, CancellationToken token)
    {
        var entitlement = await data.FindEntitlementAsync(entitlementId, true, token);
        if (entitlement is null || entitlement.Status != EntitlementStatuses.Available) return;
        entitlement.Status = EntitlementStatuses.NotCompleted;
        entitlement.UpdatedAt = now;
        entitlement.Revision++;
        AfterSalesHistory.Record(data, EventSubjects.Subscription, entitlement.SubscriptionId, "VISIT_NOT_COMPLETED", null, $"plan month {entitlement.PlanMonth}", now);
    }

    private async Task<OperationResult<SalesTaskView>> MutateAsync(Guid taskId, Func<SalesTask, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<SalesTask>>(async token =>
            {
                var task = await data.FindTaskAsync(taskId, true, token);
                if (task is null) return (OperationResult<SalesTask>.Fail(AfterSalesErrorCodes.TaskNotFound, "Task was not found."), false);
                var now = time.GetUtcNow();
                var outcome = await change(task, now, token);
                if (!outcome.Succeeded) return (outcome.As<SalesTask>(), false);
                task.UpdatedAt = now;
                task.Revision++;
                await data.SaveChangesAsync(token);
                return (OperationResult<SalesTask>.Ok(task), true);
            }, cancellationToken);
            return result.Succeeded ? OperationResult<SalesTaskView>.Ok(views.Task(result.Value!)) : result.As<SalesTaskView>();
        }
        catch (PersistenceConflictException)
        {
            // A concurrent claim (or assignment) changed the row first.
            return OperationResult<SalesTaskView>.Fail(AfterSalesErrorCodes.AlreadyClaimed, "Another user changed this task first.");
        }
    }
}

/// <summary>
/// Manual refunds: a Manager approves (or rejects) a requested refund; an Admin records the refund once the money was actually returned
/// outside the system. Nothing here calls PayOS.
/// </summary>
public sealed class RefundHandlers(IAfterSalesUnitOfWork data, AfterSalesViews views, TimeProvider time)
{
    public async Task<IReadOnlyList<RefundView>> ListAsync(string? status, CancellationToken cancellationToken)
        => (await data.ListRefundsAsync(status, cancellationToken)).Select(views.Refund).ToList();

    public Task<OperationResult<RefundView>> DecideAsync(Guid managerUserId, Guid refundId, bool approve, decimal? approvedAmount, string? note, CancellationToken cancellationToken)
        => MutateAsync(refundId, (refund, now) =>
        {
            if (refund.Status != RefundStatuses.PendingApproval) return OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "This refund is already decided.");
            var amount = approvedAmount ?? refund.OriginalAmount;
            if (approve && (amount < 0 || amount > refund.OriginalAmount || decimal.Truncate(amount) != amount))
                return OperationResult<bool>.Fail(AfterSalesErrorCodes.ValidationFailed, "The approved amount is a whole VND amount up to the original amount.");
            refund.Status = approve ? RefundStatuses.Approved : RefundStatuses.Rejected;
            refund.ApprovedAmount = approve ? amount : null;
            refund.ApprovedByUserId = managerUserId;
            refund.ApprovedAt = now;
            refund.DecisionNote = Text.Trim(note, 1000);
            AfterSalesHistory.Record(data, EventSubjects.Refund, refund.Id, approve ? "REFUND_APPROVED" : "REFUND_REJECTED", managerUserId, $"{amount:0} VND; {refund.DecisionNote}", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>Admin: the approved money was returned (bank transfer…) at <paramref name="refundedAt"/> — the date reports use.</summary>
    public Task<OperationResult<RefundView>> RecordAsync(Guid adminUserId, Guid refundId, DateTimeOffset refundedAt, string? reference, CancellationToken cancellationToken)
        => MutateAsync(refundId, (refund, now) =>
        {
            if (refund.Status != RefundStatuses.Approved) return OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, "Only an approved refund can be recorded.");
            if (refundedAt.ToUniversalTime() > now) return OperationResult<bool>.Fail(AfterSalesErrorCodes.ValidationFailed, "The refund date cannot be in the future.");
            refund.Status = RefundStatuses.Refunded;
            refund.RecordedByUserId = adminUserId;
            refund.RefundedAt = refundedAt.ToUniversalTime();
            refund.RefundReference = Text.Trim(reference, 100);
            AfterSalesHistory.Record(data, EventSubjects.Refund, refund.Id, "REFUND_RECORDED", adminUserId, $"{refund.ApprovedAmount:0} VND on {refund.RefundedAt:O}; {refund.RefundReference}", now);
            AfterSalesHistory.Notify(data, refund.CustomerUserId, "REFUND_RECORDED", $"A refund of {refund.ApprovedAmount:N0} VND was sent to you.", now, refund.RequestId, refund.SubscriptionId);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    private async Task<OperationResult<RefundView>> MutateAsync(Guid refundId, Func<RefundRecord, DateTimeOffset, OperationResult<bool>> change, CancellationToken cancellationToken)
    {
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<RefundRecord>>(async token =>
            {
                var refund = await data.FindRefundAsync(refundId, true, token);
                if (refund is null) return (OperationResult<RefundRecord>.Fail(AfterSalesErrorCodes.RefundNotFound, "Refund was not found."), false);
                var now = time.GetUtcNow();
                var outcome = change(refund, now);
                if (!outcome.Succeeded) return (outcome.As<RefundRecord>(), false);
                refund.UpdatedAt = now;
                refund.Revision++;
                await data.SaveChangesAsync(token);
                return (OperationResult<RefundRecord>.Ok(refund), true);
            }, cancellationToken);
            return result.Succeeded ? OperationResult<RefundView>.Ok(views.Refund(result.Value!)) : result.As<RefundView>();
        }
        catch (PersistenceConflictException)
        {
            return OperationResult<RefundView>.Fail(AfterSalesErrorCodes.StaleRevision, "The refund changed meanwhile.");
        }
    }
}
