using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.AfterSales;

public sealed record AfterSalesSweepResult(int ExpiredCheckouts, int ExpiredQuotations, int StartedOrActivated, int CancelledWithInstallation, int ExpiredTerms,
    int ExpiredVisits, int MaintenanceNotices, int FollowUpTasks, int ClosedFollowUps, int AcceptanceReminders, int EndedOnSiteWaits)
{
    public int Total => ExpiredCheckouts + ExpiredQuotations + StartedOrActivated + CancelledWithInstallation + ExpiredTerms + ExpiredVisits + MaintenanceNotices
        + FollowUpTasks + ClosedFollowUps + AcceptanceReminders + EndedOnSiteWaits;
}

/// <summary>
/// Periodic Flow 3 housekeeping, run by the background worker (and directly by tests). Each item is handled in its own short transaction;
/// a failure is logged and retried on the next run. Nothing here charges a customer or expires a request automatically:
/// <list type="bullet">
/// <item>Unpaid checkouts and quotations expire only after the provider confirmed (server-to-server) that nothing was paid; an expired
/// quotation releases its reserved disaster support and goes back to Sales.</item>
/// <item>Paid packages start when the installation is handed over and become active on their start date; packages of an installation
/// cancelled before handover are cancelled (refund request for a Manager) and never activated.</item>
/// <item>Terms expire at their end; unused visits expire with them (a SmartSolar-delayed booking keeps its extension window).</item>
/// <item>Maintenance notices, Sales follow-ups after the customer's silence, and acceptance reminders (silence is never acceptance).</item>
/// <item>A technician still waiting on site after the visit's reserved interval is checked out (the repair needs its own visit).</item>
/// </list>
/// </summary>
public sealed class AfterSalesSweepService(
    IAfterSalesUnitOfWork data,
    PaymentService payments,
    SubscriptionTerms terms,
    ServicePackageHandlers packages,
    ISchedulingPolicyProvider policies,
    TimeProvider time,
    ILogger<AfterSalesSweepService> logger)
{
    public async Task<AfterSalesSweepResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var policy = await policies.GetAsync(cancellationToken);
        return new AfterSalesSweepResult(
            await ExpireCheckoutsAsync(cancellationToken),
            await ExpireQuotationsAsync(cancellationToken),
            await StartAndActivateAsync(cancellationToken),
            await CancelForCancelledInstallationsAsync(cancellationToken),
            await ExpireTermsAsync(policy, cancellationToken),
            await ExpireVisitsAsync(cancellationToken),
            await SendMaintenanceNoticesAsync(policy, cancellationToken),
            await OpenFollowUpsAsync(policy, cancellationToken),
            await CloseStaleFollowUpsAsync(policy, cancellationToken),
            await RemindAcceptanceAsync(policy, cancellationToken),
            await EndOnSiteWaitsAsync(cancellationToken));
    }

    // ------------------------------------------------------------------ same-visit repair

    /// <summary>A technician waiting on site whose reserved interval ended without a repair: the visit closes, a Manager plans the repair.</summary>
    private async Task<int> EndOnSiteWaitsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var visit in await data.ListOnSiteWaitsEndedAsync(time.GetUtcNow(), cancellationToken))
        {
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindVisitAsync(visit.Id, true, token);
                if (tracked is null || !VisitStatuses.IsWaitingOnSite(tracked)) return false;
                var request = (await data.FindRequestAsync(tracked.RequestId, true, token))!;
                var assignment = await data.FindCurrentAssignmentAsync(tracked.Id, true, token);
                TechnicianServiceHandlers.CloseOnSiteWait(data, tracked, assignment, request, null, "the reserved visit time ended", time.GetUtcNow());
                return true;
            }, "ending an on-site wait", cancellationToken);
        }
        return count;
    }

    // ------------------------------------------------------------------ payments

    private async Task<int> ExpireCheckoutsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        foreach (var subscription in (await data.ListSubscriptionsByStatusAsync([SubscriptionStatuses.AwaitingPayment], cancellationToken))
                     .Where(s => s.CheckoutExpiresAt <= now))
        {
            // Ask PayOS first: a payment that arrived (late webhook) is applied by the reconciliation, never expired away.
            if (subscription.CurrentPaymentAttemptId is { } attemptId
                && await payments.ReconcileAsync(attemptId, force: true, cancellationToken) is { } attempt
                && (attempt.Status == PaymentStatuses.Paid || PaymentStatuses.Payable.Contains(attempt.Status)))
                continue;
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindSubscriptionAsync(subscription.Id, true, token);
                if (tracked?.Status != SubscriptionStatuses.AwaitingPayment) return false;
                tracked.Status = SubscriptionStatuses.CheckoutExpired;
                tracked.UpdatedAt = time.GetUtcNow();
                tracked.Revision++;
                AfterSalesHistory.Record(data, EventSubjects.Subscription, tracked.Id, "CHECKOUT_EXPIRED", null, null, tracked.UpdatedAt);
                return true;
            }, "expiring a package checkout", cancellationToken);
        }
        return count;
    }

    private async Task<int> ExpireQuotationsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var quotation in await data.ListExpiredOpenQuotationsAsync(time.GetUtcNow(), cancellationToken))
        {
            // Release reserved support only after PayOS confirmed that this quotation's checkout was not paid.
            if (quotation.CurrentPaymentAttemptId is { } attemptId
                && await payments.ReconcileAsync(attemptId, force: true, cancellationToken) is { } attempt
                && (attempt.Status == PaymentStatuses.Paid || PaymentStatuses.Payable.Contains(attempt.Status)))
                continue;
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindQuotationAsync(quotation.Id, true, token);
                if (tracked is null || !QuotationStatuses.IsOpen(tracked.Status)) return false;
                var now = time.GetUtcNow();
                var request = (await data.FindRequestAsync(tracked.RequestId, true, token))!;
                await AfterSalesWorkflow.CloseQuotationAsync(data, tracked, QuotationStatuses.Expired, ItemStatuses.Open, null, now, token);
                if (request.Kind == ServiceRequestKinds.OneTimeMaintenance)
                {
                    AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.AwaitingQuotation, null, "quotation expired", now);
                    await AfterSalesWorkflow.OpenTaskAsync(data, SalesTaskTypes.MaintenanceQuotation, request.CustomerUserId, request.Id, null, "The quotation expired.", now, token);
                }
                else
                {
                    var items = await data.ListItemsAsync(request.Id, true, token);
                    if (request.Status is not (ServiceRequestStatuses.InProgress or ServiceRequestStatuses.Scheduled))
                        AfterSalesWorkflow.Move(data, request, AfterSalesWorkflow.NextStatus(items), null, "quotation expired", now);
                    await AfterSalesWorkflow.OpenTaskAsync(data, SalesTaskTypes.RepairQuotation, request.CustomerUserId, request.Id, null, "The quotation expired.", now, token);
                }
                AfterSalesHistory.Notify(data, request.CustomerUserId, "QUOTATION_EXPIRED", $"The quotation for {request.RequestNumber} expired.", now, request.Id);
                return true;
            }, "expiring a quotation", cancellationToken);
        }
        return count;
    }

    // ------------------------------------------------------------------ terms

    private async Task<int> StartAndActivateAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var subscription in await data.ListSubscriptionsByStatusAsync([SubscriptionStatuses.Paid], cancellationToken))
        {
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindSubscriptionAsync(subscription.Id, true, token);
                if (tracked?.Status != SubscriptionStatuses.Paid) return false;
                var now = time.GetUtcNow();
                var before = (tracked.StartsAt, tracked.Status);
                await terms.StartTermIfPossibleAsync(tracked, now, token);
                terms.ActivateIfDue(tracked, now);
                return before != (tracked.StartsAt, tracked.Status);
            }, "starting a package term", cancellationToken);
        }
        return count;
    }

    private async Task<int> CancelForCancelledInstallationsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        var pending = await data.ListSubscriptionsByStatusAsync(
            [SubscriptionStatuses.AwaitingPayment, SubscriptionStatuses.Paid, SubscriptionStatuses.CancellationRequested], cancellationToken);
        foreach (var subscription in pending.Where(s => s.StartsAt is null))
        {
            var contract = await data.GetContractAsync(subscription.ContractId, cancellationToken);
            if (contract?.ProjectStatus != InstallationStatuses.Cancelled) continue;
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindSubscriptionAsync(subscription.Id, true, token);
                if (tracked is null || tracked.StartsAt is not null) return false;
                var now = time.GetUtcNow();
                if (tracked.Status == SubscriptionStatuses.AwaitingPayment)
                {
                    tracked.Status = SubscriptionStatuses.Cancelled;
                    tracked.CancelledAt = now;
                    AfterSalesHistory.Record(data, EventSubjects.Subscription, tracked.Id, "CANCELLED_WITH_INSTALLATION", null, null, now);
                }
                else
                {
                    // Paid but never activated: a Manager reviews the refund arrangement; an Admin records it.
                    await packages.CancelPaidAsync(tracked, null, tracked.Price, "Installation cancelled before handover", now, token);
                }
                tracked.UpdatedAt = now;
                tracked.Revision++;
                return true;
            }, "cancelling packages of a cancelled installation", cancellationToken);
        }
        return count;
    }

    private async Task<int> ExpireTermsAsync(SchedulingPolicySnapshot policy, CancellationToken cancellationToken)
    {
        var count = 0;
        var noticeDays = policy.Get(AfterSalesSettingKeys.MaintenanceNoticeLeadDays);
        foreach (var subscription in await data.ListSubscriptionsByStatusAsync([SubscriptionStatuses.Active], cancellationToken))
        {
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindSubscriptionAsync(subscription.Id, true, token);
                if (tracked?.Status != SubscriptionStatuses.Active) return false;
                var now = time.GetUtcNow();
                if (tracked.EndsAt <= now)
                {
                    tracked.Status = SubscriptionStatuses.Expired;
                    AfterSalesHistory.Record(data, EventSubjects.Subscription, tracked.Id, "EXPIRED", null, null, now);
                    // Unused visits end with the term (they never roll over). A booked visit is resolved by its request.
                    foreach (var entitlement in (await data.ListEntitlementsAsync(tracked.Id, true, token))
                                 .Where(e => e.Status is EntitlementStatuses.Available or EntitlementStatuses.NotCompleted))
                    {
                        entitlement.Status = EntitlementStatuses.Expired;
                        entitlement.UpdatedAt = now;
                        entitlement.Revision++;
                    }
                }
                else if (tracked.ExpiryReminderSentAt is null && tracked.EndsAt <= now.AddDays(noticeDays))
                {
                    tracked.ExpiryReminderSentAt = now;
                    AfterSalesHistory.Notify(data, tracked.CustomerUserId, "PACKAGE_EXPIRING", $"{tracked.PackageName} ends on {tracked.EndsAt:O}. Renew it to stay covered.",
                        now, subscriptionId: tracked.Id);
                }
                else
                {
                    return false;
                }
                tracked.UpdatedAt = now;
                tracked.Revision++;
                return true;
            }, "expiring a package term", cancellationToken);
        }
        return count;
    }

    /// <summary>Visits freed after their term ended expire; a SmartSolar-delayed booking not done within its extension is EXPIRED_UNFULFILLED.</summary>
    private async Task<int> ExpireVisitsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        foreach (var entitlement in await data.ListEntitlementsByStatusAsync([EntitlementStatuses.Available, EntitlementStatuses.NotCompleted, EntitlementStatuses.Booked], cancellationToken))
        {
            var term = await data.FindSubscriptionAsync(entitlement.SubscriptionId, false, cancellationToken);
            if (term?.EndsAt is not { } end) continue;
            var limit = entitlement.ExtendedUntil ?? end;
            if (limit > now) continue;
            if (entitlement.Status != EntitlementStatuses.Booked && term.Status == SubscriptionStatuses.Active) continue;   // handled with the term
            count += await InTransactionAsync(async token =>
            {
                var tracked = (await data.FindEntitlementAsync(entitlement.Id, true, token))!;
                if (tracked.Status is EntitlementStatuses.Consumed or EntitlementStatuses.Expired or EntitlementStatuses.ExpiredUnfulfilled) return false;
                var at = time.GetUtcNow();
                var delayed = tracked.Status == EntitlementStatuses.Booked && tracked.ExtendedUntil is not null;
                if (tracked.CurrentRequestId is { } requestId && await data.FindRequestAsync(requestId, true, token) is { } request
                    && !ServiceRequestStatuses.IsFinal(request.Status))
                {
                    if (request.Status is ServiceRequestStatuses.InProgress or ServiceRequestStatuses.PendingCustomerAcceptance or ServiceRequestStatuses.Disputed
                        or ServiceRequestStatuses.ReworkRequired)
                        return false;   // the visit was done; its acceptance decides the visit, not the clock
                    await AfterSalesWorkflow.CancelOpenVisitsAsync(data, requestId, "maintenance window ended", at, token);
                    AfterSalesWorkflow.Move(data, request, ServiceRequestStatuses.NotCompleted, null, "the maintenance window ended", at);
                }
                tracked.Status = delayed ? EntitlementStatuses.ExpiredUnfulfilled : EntitlementStatuses.Expired;
                tracked.CurrentRequestId = null;
                tracked.UpdatedAt = at;
                tracked.Revision++;
                AfterSalesHistory.Record(data, EventSubjects.Subscription, tracked.SubscriptionId, delayed ? "VISIT_EXPIRED_UNFULFILLED" : "VISIT_EXPIRED", null,
                    $"plan month {tracked.PlanMonth}", at);
                return true;
            }, "expiring a maintenance visit", cancellationToken);
        }
        return count;
    }

    // ------------------------------------------------------------------ customer reminders and Sales follow-ups

    private async Task<int> SendMaintenanceNoticesAsync(SchedulingPolicySnapshot policy, CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        var lead = TimeSpan.FromDays(policy.Get(AfterSalesSettingKeys.MaintenanceNoticeLeadDays));
        foreach (var entitlement in (await data.ListEntitlementsByStatusAsync([EntitlementStatuses.Available], cancellationToken))
                     .Where(e => e.NotifiedAt is null && e.PeriodStartsAt - lead <= now))
        {
            var term = await data.FindSubscriptionAsync(entitlement.SubscriptionId, false, cancellationToken);
            if (term?.Status != SubscriptionStatuses.Active) continue;
            count += await InTransactionAsync(async token =>
            {
                var tracked = (await data.FindEntitlementAsync(entitlement.Id, true, token))!;
                if (tracked.NotifiedAt is not null) return false;
                tracked.NotifiedAt = time.GetUtcNow();
                tracked.UpdatedAt = tracked.NotifiedAt.Value;
                tracked.Revision++;
                AfterSalesHistory.Notify(data, term.CustomerUserId, "MAINTENANCE_DUE",
                    $"Your maintenance visit (plan month {tracked.PlanMonth}) can be booked from {tracked.PeriodStartsAt:O}.", tracked.NotifiedAt.Value, subscriptionId: term.Id);
                return true;
            }, "sending a maintenance notice", cancellationToken);
        }
        return count;
    }

    private async Task<int> OpenFollowUpsAsync(SchedulingPolicySnapshot policy, CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        var wait = TimeSpan.FromHours(policy.Get(AfterSalesSettingKeys.CustomerResponseHours));
        foreach (var entitlement in (await data.ListEntitlementsByStatusAsync([EntitlementStatuses.Available], cancellationToken))
                     .Where(e => e.NotifiedAt is { } notified && notified + wait <= now && e.FollowUpCreatedAt is null))
        {
            var term = await data.FindSubscriptionAsync(entitlement.SubscriptionId, false, cancellationToken);
            if (term?.Status != SubscriptionStatuses.Active) continue;
            count += await InTransactionAsync(async token =>
            {
                var tracked = (await data.FindEntitlementAsync(entitlement.Id, true, token))!;
                if (tracked.FollowUpCreatedAt is not null || tracked.Status != EntitlementStatuses.Available) return false;
                var at = time.GetUtcNow();
                tracked.FollowUpCreatedAt = at;
                tracked.UpdatedAt = at;
                tracked.Revision++;
                await AfterSalesWorkflow.OpenTaskAsync(data, SalesTaskTypes.MaintenanceFollowUp, term.CustomerUserId, null, tracked.Id,
                    $"No booking yet for plan month {tracked.PlanMonth}; contact the customer.", at, token);
                return true;
            }, "opening a maintenance follow-up", cancellationToken);
        }
        return count;
    }

    /// <summary>A follow-up that reached nobody within the maximum duration closes; the planned visit is marked NOT_COMPLETED (not consumed).</summary>
    private async Task<int> CloseStaleFollowUpsAsync(SchedulingPolicySnapshot policy, CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        var maxDays = policy.Get(AfterSalesSettingKeys.FollowUpMaxDays);
        var stale = (await data.ListTasksAsync(null, null, cancellationToken))
            .Where(t => t.TaskType == SalesTaskTypes.MaintenanceFollowUp && SalesTaskStatuses.IsOpen(t.Status) && t.CreatedAt.AddDays(maxDays) <= now);
        foreach (var task in stale)
        {
            count += await InTransactionAsync(async token =>
            {
                var tracked = await data.FindTaskAsync(task.Id, true, token);
                if (tracked is null || !SalesTaskStatuses.IsOpen(tracked.Status)) return false;
                var at = time.GetUtcNow();
                tracked.Status = SalesTaskStatuses.Done;
                tracked.Outcome = FollowUpOutcomes.Unreachable;
                tracked.CompletedAt = at;
                tracked.UpdatedAt = at;
                tracked.Revision++;
                if (tracked.EntitlementId is { } entitlementId) await SalesTaskHandlers.MarkNotCompletedAsync(data, entitlementId, at, token);
                AfterSalesHistory.Record(data, EventSubjects.SalesTask, tracked.Id, "FOLLOW_UP_TIMED_OUT", null, $"after {maxDays} days", at);
                return true;
            }, "closing a follow-up", cancellationToken);
        }
        return count;
    }

    /// <summary>Completed work waits for the customer: remind them every response period and let Sales follow up. Never auto-accepted.</summary>
    private async Task<int> RemindAcceptanceAsync(SchedulingPolicySnapshot policy, CancellationToken cancellationToken)
    {
        var count = 0;
        var now = time.GetUtcNow();
        var wait = TimeSpan.FromHours(policy.Get(AfterSalesSettingKeys.CustomerResponseHours));
        foreach (var request in (await data.ListRequestsAsync(new RequestFilter(Status: ServiceRequestStatuses.PendingCustomerAcceptance), cancellationToken))
                     .Where(r => (r.LastAcceptanceReminderAt ?? r.AcceptanceRequestedAt) is { } last && last + wait <= now))
        {
            count += await InTransactionAsync(async token =>
            {
                var tracked = (await data.FindRequestAsync(request.Id, true, token))!;
                if (tracked.Status != ServiceRequestStatuses.PendingCustomerAcceptance) return false;
                var at = time.GetUtcNow();
                tracked.LastAcceptanceReminderAt = at;
                tracked.UpdatedAt = at;
                tracked.Revision++;
                AfterSalesHistory.Notify(data, tracked.CustomerUserId, "ACCEPTANCE_REMINDER", $"Please accept or reject the result of {tracked.RequestNumber}.", at, tracked.Id);
                await AfterSalesWorkflow.OpenTaskAsync(data, SalesTaskTypes.AcceptanceFollowUp, tracked.CustomerUserId, tracked.Id, null,
                    "The customer has not answered the completion yet.", at, token);
                return true;
            }, "sending an acceptance reminder", cancellationToken);
        }
        return count;
    }

    private async Task<int> InTransactionAsync(Func<CancellationToken, Task<bool>> work, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await data.ExecuteInTransactionAsync<bool>(async token =>
            {
                if (!await work(token)) return (false, false);
                await data.SaveChangesAsync(token);
                return (true, true);
            }, cancellationToken) ? 1 : 0;
        }
        catch (PersistenceConflictException)
        {
            return 0;   // a user changed the row at the same moment: the next run sees the new state
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "After-sales sweep failed while {What}.", what);
            return 0;
        }
    }
}
