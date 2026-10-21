using SmartSolar.Modules.Installations;

namespace SmartSolar.Modules.AfterSales;

/// <summary>Audit events and in-app notifications (always written in the caller's transaction).</summary>
public static class AfterSalesHistory
{
    public static void Record(IAfterSalesUnitOfWork data, string subjectType, Guid subjectId, string eventType, Guid? actorUserId, string? detail, DateTimeOffset now)
        => data.Add(new AfterSalesEvent
        {
            Id = Guid.NewGuid(), SubjectType = subjectType, SubjectId = subjectId, EventType = eventType, ActorUserId = actorUserId,
            Detail = Text.Trim(detail, 2000), CreatedAt = now,
        });

    public static void Notify(IAfterSalesUnitOfWork data, Guid recipientUserId, string type, string message, DateTimeOffset now,
        Guid? requestId = null, Guid? subscriptionId = null)
        => data.Add(new AfterSalesNotification
        {
            Id = Guid.NewGuid(), RecipientUserId = recipientUserId, Type = type, Message = Text.Trim(message, 500)!, RequestId = requestId,
            SubscriptionId = subscriptionId, CreatedAt = now,
        });

    public static void NotifyAll(IAfterSalesUnitOfWork data, IEnumerable<Guid> recipients, string type, string message, DateTimeOffset now,
        Guid? requestId = null, Guid? subscriptionId = null)
    {
        foreach (var recipient in recipients.Distinct()) Notify(data, recipient, type, message, now, requestId, subscriptionId);
    }
}

public static class Text
{
    /// <summary>Trimmed text cut to <paramref name="max"/> characters; null for blank input.</summary>
    public static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}

/// <summary>
/// Request progress and money bookkeeping shared by every Flow 3 handler, so each rule exists exactly once.
/// </summary>
public static class AfterSalesWorkflow
{
    /// <summary>
    /// Where a FAULT (or one-time maintenance) request goes once nobody is working on site, derived from its items:
    /// a coverage decision is missing → AWAITING_DECISION; chargeable items without an open quotation → AWAITING_QUOTATION;
    /// a quotation waits for the customer → AWAITING_CUSTOMER; authorized work is left → READY_FOR_WORK;
    /// every item was declined (the customer rejected all paid work) → CANCELLED; otherwise the work is done →
    /// PENDING_CUSTOMER_ACCEPTANCE (one final acceptance for the whole request).
    /// </summary>
    public static string NextStatus(IReadOnlyList<ServiceItem> items)
    {
        if (items.Any(i => i.Coverage == ItemCoverages.Pending && i.Status == ItemStatuses.Open)) return ServiceRequestStatuses.AwaitingDecision;
        if (items.Any(i => ItemCoverages.NeedsQuotation(i.Coverage) && i.Status == ItemStatuses.Open)) return ServiceRequestStatuses.AwaitingQuotation;
        if (items.Any(i => i.Status == ItemStatuses.Quoted)) return ServiceRequestStatuses.AwaitingCustomer;
        if (items.Any(i => i.Status == ItemStatuses.Authorized)) return ServiceRequestStatuses.ReadyForWork;
        if (items.Count > 0 && items.All(i => i.Status == ItemStatuses.Declined)) return ServiceRequestStatuses.Cancelled;
        return ServiceRequestStatuses.PendingCustomerAcceptance;
    }

    /// <summary>Moves a request to <paramref name="status"/> with an audit event (no-op when unchanged).</summary>
    public static void Move(IAfterSalesUnitOfWork data, ServiceRequest request, string status, Guid? actorUserId, string? detail, DateTimeOffset now)
    {
        if (request.Status == status) return;
        AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "STATUS_CHANGED", actorUserId, $"{request.Status} → {status}{(detail is null ? "" : $": {detail}")}", now);
        request.Status = status;
        if (status == ServiceRequestStatuses.PendingCustomerAcceptance)
        {
            request.AcceptanceRequestedAt = now;
            request.LastAcceptanceReminderAt = null;
        }
        request.UpdatedAt = now;
        request.Revision++;
    }

    // ------------------------------------------------------------------ disaster support balance

    /// <summary>Reserves support on the claim and on the term that covered the incident (check constraints forbid overspending).</summary>
    public static void Reserve(DisasterClaim claim, ServiceSubscription term, decimal amount, DateTimeOffset now)
    {
        if (amount <= 0) return;
        claim.SupportReserved += amount;
        term.SupportReserved += amount;
        Touch(claim, term, now);
    }

    /// <summary>Gives back support reserved by a quotation that will not be used (declined, expired, superseded, cancelled).</summary>
    public static void Release(DisasterClaim claim, ServiceSubscription term, decimal amount, DateTimeOffset now)
    {
        if (amount <= 0) return;
        claim.SupportReserved = Math.Max(0, claim.SupportReserved - amount);
        term.SupportReserved = Math.Max(0, term.SupportReserved - amount);
        Touch(claim, term, now);
    }

    /// <summary>Turns reserved support into consumed support (at the customer's final acceptance).</summary>
    public static void Consume(DisasterClaim claim, ServiceSubscription term, decimal amount, DateTimeOffset now)
    {
        if (amount <= 0) return;
        Release(claim, term, amount, now);
        claim.SupportConsumed += amount;
        term.SupportConsumed += amount;
    }

    private static void Touch(DisasterClaim claim, ServiceSubscription term, DateTimeOffset now)
    {
        claim.UpdatedAt = now;
        claim.Revision++;
        term.UpdatedAt = now;
        term.Revision++;
    }

    /// <summary>
    /// Cancels a quotation that will not be paid and releases its disaster support. Items go back to OPEN (a new quotation can be
    /// issued) unless <paramref name="itemStatus"/> says otherwise (DECLINED when the customer rejected it).
    /// </summary>
    public static async Task CloseQuotationAsync(IAfterSalesUnitOfWork data, ServiceQuotation quotation, string status, string itemStatus, Guid? actorUserId,
        DateTimeOffset now, CancellationToken token)
    {
        if (!QuotationStatuses.IsOpen(quotation.Status)) return;
        if (quotation.DisasterSupportAmount > 0 && quotation.DisasterClaimId is not null)
        {
            var claim = await data.FindClaimForRequestAsync(quotation.RequestId, true, token);
            var term = claim?.SubscriptionId is { } termId ? await data.FindSubscriptionAsync(termId, true, token) : null;
            if (claim is not null && term is not null) Release(claim, term, quotation.DisasterSupportAmount, now);
        }
        quotation.Status = status;
        if (status == QuotationStatuses.Rejected) quotation.RejectedAt = now;
        quotation.UpdatedAt = now;
        quotation.Revision++;
        foreach (var item in (await data.ListItemsAsync(quotation.RequestId, true, token)).Where(i => i.QuotationId == quotation.Id && i.Status == ItemStatuses.Quoted))
        {
            item.Status = itemStatus;
            if (itemStatus == ItemStatuses.Open) item.QuotationId = null;
            item.UpdatedAt = now;
        }
        AfterSalesHistory.Record(data, EventSubjects.Request, quotation.RequestId, "QUOTATION_" + status, actorUserId, $"quotation v{quotation.Version}", now);
    }

    // ------------------------------------------------------------------ open work

    /// <summary>Cancels every open visit of a request and frees its technician interval.</summary>
    public static async Task CancelOpenVisitsAsync(IAfterSalesUnitOfWork data, Guid requestId, string reason, DateTimeOffset now, CancellationToken token)
    {
        foreach (var visit in (await data.ListVisitsAsync(requestId, true, token)).Where(v => VisitStatuses.IsOpen(v.Status)))
        {
            visit.Status = VisitStatuses.Cancelled;
            visit.UpdatedAt = now;
            if (await data.FindCurrentAssignmentAsync(visit.Id, true, token) is { } assignment)
            {
                assignment.Status = AssignmentStatuses.Cancelled;
                assignment.ClosedAt = now;
                assignment.ResponseReason ??= Text.Trim(reason, 500);
            }
        }
    }

    /// <summary>Closes the open Sales tasks of a request (the work they were for is gone).</summary>
    public static async Task CloseTasksAsync(IAfterSalesUnitOfWork data, Guid requestId, DateTimeOffset now, CancellationToken token)
    {
        foreach (var type in SalesTaskTypes.All)
        {
            if (await data.FindOpenTaskAsync(requestId, null, type, true, token) is not { } task) continue;
            task.Status = SalesTaskStatuses.Cancelled;
            task.CompletedAt = now;
            task.UpdatedAt = now;
            task.Revision++;
        }
    }

    /// <summary>Creates a Sales queue task once (an open task of the same type for the same work is reused).</summary>
    public static async Task<SalesTask> OpenTaskAsync(IAfterSalesUnitOfWork data, string taskType, Guid customerUserId, Guid? requestId, Guid? entitlementId,
        string? note, DateTimeOffset now, CancellationToken token)
    {
        if (await data.FindOpenTaskAsync(requestId, entitlementId, taskType, false, token) is { } existing) return existing;
        var task = new SalesTask
        {
            Id = Guid.NewGuid(), TaskType = taskType, Status = SalesTaskStatuses.Open, RequestId = requestId, EntitlementId = entitlementId,
            CustomerUserId = customerUserId, Note = Text.Trim(note, 2000), CreatedAt = now, UpdatedAt = now,
        };
        data.Add(task);
        AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(Identity.Constants.RoleCodes.Sales, token), "SALES_TASK_CREATED",
            $"New after-sales task: {taskType}.", now, requestId);
        return task;
    }

    /// <summary>
    /// A quotation was paid (verified PayOS payment, or nothing to pay): its items are authorized and the request moves on (normally
    /// READY_FOR_WORK). A supplementary quotation paid while the technician is on site keeps the request IN_PROGRESS.
    /// </summary>
    public static async Task MarkQuotationPaidAsync(IAfterSalesUnitOfWork data, ServiceQuotation quotation, ServiceRequest request, Guid? actorUserId,
        DateTimeOffset paidAt, DateTimeOffset now, CancellationToken token)
    {
        quotation.Status = QuotationStatuses.Paid;
        quotation.PaidAt = paidAt;
        quotation.UpdatedAt = now;
        quotation.Revision++;
        var items = await data.ListItemsAsync(request.Id, true, token);
        foreach (var item in items.Where(i => i.QuotationId == quotation.Id && i.Status == ItemStatuses.Quoted))
        {
            item.Status = ItemStatuses.Authorized;
            item.UpdatedAt = now;
        }
        AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "QUOTATION_PAID", actorUserId, $"v{quotation.Version}: {quotation.CustomerAmount:0} VND", now);
        if (request.Status is not (ServiceRequestStatuses.InProgress or ServiceRequestStatuses.Scheduled))
        {
            var next = request.Kind == ServiceRequestKinds.OneTimeMaintenance ? ServiceRequestStatuses.ReadyForWork : NextStatus(items);
            Move(data, request, next, actorUserId, "quotation paid", now);
        }
        AfterSalesHistory.NotifyAll(data, await data.ListActiveUserIdsWithRoleAsync(Identity.Constants.RoleCodes.Manager, token), "WORK_AUTHORIZED",
            $"{request.RequestNumber}: paid work can be scheduled.", now, request.Id);
    }

    public static string NewRequestNumber(DateTimeOffset now)
        => $"AS-{now:yyyyMMdd}-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3))}";
}
