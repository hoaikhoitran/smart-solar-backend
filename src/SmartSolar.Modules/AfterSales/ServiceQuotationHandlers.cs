using System.Text.Json;
using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.AfterSales;

/// <summary>One quotation line. FAULT quotations reference a diagnosed item; one-time maintenance lines describe the job.</summary>
public sealed record QuotationLineInput(Guid? ItemId, string? Description, decimal PartsAmount, decimal LaborAmount);

public sealed record IssueQuotationInput(IReadOnlyList<QuotationLineInput> Lines, int? RepairWarrantyMonths, string? Note);

/// <summary>
/// Paid repair / one-time maintenance quotations. Sales (owner of the claimed queue task) issues an immutable quotation covering every
/// open chargeable item; a change is a new version that supersedes the unpaid previous one. Disaster-protected items get their support
/// computed and reserved at issuance (shared per-incident cap across all quotations of the incident). The customer accepts or rejects
/// the whole quotation and pays the customer share through PayOS before the work starts.
/// </summary>
public sealed class ServiceQuotationHandlers(
    IAfterSalesUnitOfWork data,
    PaymentService payments,
    ISchedulingPolicyProvider policies,
    AfterSalesViews views,
    TimeProvider time)
{
    // ================================================================== Sales

    public async Task<OperationResult<ServiceRequestView>> IssueAsync(Guid salesUserId, Guid requestId, IssueQuotationInput input, CancellationToken cancellationToken)
    {
        var request = await data.FindRequestAsync(requestId, false, cancellationToken);
        if (request is null) return Fail(AfterSalesErrorCodes.RequestNotFound, "Request was not found.");
        var taskType = request.Kind == ServiceRequestKinds.OneTimeMaintenance ? SalesTaskTypes.MaintenanceQuotation : SalesTaskTypes.RepairQuotation;
        var task = await data.FindOpenTaskAsync(requestId, null, taskType, false, cancellationToken);
        // Superseding an issued quotation is done by the Sales user who issued it (no new task is opened for a correction).
        var open = (await data.ListQuotationsAsync(requestId, false, cancellationToken)).FirstOrDefault(q => QuotationStatuses.IsOpen(q.Status));
        var mayIssue = task is not null && task.AssignedSalesUserId == salesUserId || open is not null && open.SalesUserId == salesUserId;
        if (!mayIssue) return Fail(Common.Results.CommonErrorCodes.Forbidden, "Claim the quotation task in the Sales queue first.");
        if (request.Status is not (ServiceRequestStatuses.AwaitingQuotation or ServiceRequestStatuses.AwaitingCustomer))
            return Fail(AfterSalesErrorCodes.InvalidState, $"A {request.Status} request does not need a quotation.");
        if (open?.CurrentPaymentAttemptId is { } liveAttempt && await data.FindPaymentAttemptAsync(liveAttempt, cancellationToken) is { } attempt
            && PaymentStatuses.Payable.Contains(attempt.Status) && attempt.ExpiresAt > time.GetUtcNow())
            return Fail(PaymentErrorCodes.CheckoutInProgress, "The customer is paying the current version; it cannot be replaced now.");

        // ---- validate the lines
        if (input.Lines.Count is 0 or > 50) return Invalid("lines", "Give between 1 and 50 lines.");
        foreach (var line in input.Lines)
        {
            if (line.PartsAmount < 0 || line.LaborAmount < 0 || line.PartsAmount + line.LaborAmount <= 0
                || decimal.Truncate(line.PartsAmount) != line.PartsAmount || decimal.Truncate(line.LaborAmount) != line.LaborAmount)
                return Invalid("lines", "Amounts are whole VND, not negative, and every line costs something.");
        }
        var items = await data.ListItemsAsync(requestId, false, cancellationToken);
        if (request.Kind == ServiceRequestKinds.Fault)
        {
            // The customer accepts the WHOLE chargeable part: every open chargeable / disaster item must be priced exactly once.
            var quotable = items.Where(i => ItemCoverages.NeedsQuotation(i.Coverage) && i.Status is ItemStatuses.Open or ItemStatuses.Quoted).Select(i => i.Id).ToHashSet();
            var priced = input.Lines.Select(l => l.ItemId).ToList();
            if (priced.Any(id => id is null) || priced.Distinct().Count() != priced.Count || !quotable.SetEquals(priced.Select(id => id!.Value)))
                return Invalid("lines", "Price every chargeable item of the diagnosis once (and nothing else).");
        }
        else if (input.Lines.Any(l => l.ItemId is not null || Text.Trim(l.Description, 300) is null))
        {
            return Invalid("lines", "Describe each maintenance line (no diagnosed items).");
        }

        int? warrantyMonths = null;
        if (request.Kind == ServiceRequestKinds.Fault)
        {
            var options = (await data.ListWarrantyOptionsAsync(WarrantyOptionKinds.Repair, false, cancellationToken)).Where(o => o.IsActive).ToList();
            warrantyMonths = input.RepairWarrantyMonths ?? options.FirstOrDefault(o => o.IsDefault)?.Months;
            if (warrantyMonths is { } m && options.All(o => o.Months != m)) return Invalid("repairWarrantyMonths", "Choose one of the active repair-warranty durations.");
        }

        var policy = await policies.GetAsync(cancellationToken);
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var tracked = (await data.FindRequestAsync(requestId, true, token))!;
                var now = time.GetUtcNow();
                var quotations = await data.ListQuotationsAsync(requestId, true, token);
                foreach (var previous in quotations.Where(q => QuotationStatuses.IsOpen(q.Status)))
                    await AfterSalesWorkflow.CloseQuotationAsync(data, previous, QuotationStatuses.Superseded, ItemStatuses.Open, salesUserId, now, token);
                // Free the "one open quotation" slot (and the released support) before the new version is inserted.
                await data.SaveChangesAsync(token);
                var trackedItems = await data.ListItemsAsync(requestId, true, token);
                var lines = input.Lines.Select(l => new
                {
                    itemId = l.ItemId,
                    description = Text.Trim(l.Description, 300) ?? trackedItems.First(i => i.Id == l.ItemId).Description,
                    partsAmount = l.PartsAmount,
                    laborAmount = l.LaborAmount,
                }).ToList();

                var quotation = new ServiceQuotation
                {
                    Id = Guid.NewGuid(), RequestId = requestId, Version = quotations.Count == 0 ? 1 : quotations.Max(q => q.Version) + 1,
                    IsSupplementary = quotations.Any(q => q.Status == QuotationStatuses.Paid),
                    SupersedesQuotationId = quotations.Where(q => q.Status == QuotationStatuses.Superseded).OrderByDescending(q => q.Version).FirstOrDefault()?.Id,
                    Status = QuotationStatuses.Issued, SalesUserId = salesUserId, LinesJson = JsonSerializer.Serialize(lines, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    PartsAmount = input.Lines.Sum(l => l.PartsAmount), LaborAmount = input.Lines.Sum(l => l.LaborAmount), RepairWarrantyMonths = warrantyMonths,
                    Note = Text.Trim(input.Note, 1000), IssuedAt = now, ExpiresAt = now.AddDays(policy.Get(AfterSalesSettingKeys.QuotationValidityDays)), UpdatedAt = now,
                };
                quotation.TotalAmount = quotation.PartsAmount + quotation.LaborAmount;

                // Disaster support for the protected items, capped by the incident's and the term's remaining limits; reserved now.
                var disasterAmount = input.Lines.Where(l => trackedItems.FirstOrDefault(i => i.Id == l.ItemId)?.Coverage == ItemCoverages.DisasterProtection)
                    .Sum(l => l.PartsAmount + l.LaborAmount);
                if (disasterAmount > 0)
                {
                    var claim = await data.FindClaimForRequestAsync(requestId, true, token);
                    var term = claim?.SubscriptionId is { } termId ? await data.FindSubscriptionAsync(termId, true, token) : null;
                    if (claim?.Status != ClaimStatuses.Approved || term is null)
                        return (OperationResult<bool>.Fail(AfterSalesErrorCodes.ClaimNotApproved, "The disaster-protection claim is not approved."), false);
                    var support = AfterSalesRules.DisasterSupport(disasterAmount, claim.CoveragePercent,
                        claim.PerIncidentLimit - claim.SupportReserved - claim.SupportConsumed, term.SupportAvailable);
                    AfterSalesWorkflow.Reserve(claim, term, support, now);
                    quotation.DisasterSupportAmount = support;
                    quotation.DisasterClaimId = claim.Id;
                }
                quotation.CustomerAmount = quotation.TotalAmount - quotation.DisasterSupportAmount;
                data.Add(quotation);
                foreach (var item in trackedItems.Where(i => input.Lines.Any(l => l.ItemId == i.Id)))
                {
                    item.Status = ItemStatuses.Quoted;
                    item.QuotationId = quotation.Id;
                    item.UpdatedAt = now;
                }
                if (await data.FindOpenTaskAsync(requestId, null, taskType, true, token) is { } openTask)
                {
                    openTask.Status = SalesTaskStatuses.Done;
                    openTask.CompletedAt = now;
                    openTask.Outcome = "QUOTATION_ISSUED";
                    openTask.UpdatedAt = now;
                    openTask.Revision++;
                }
                AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "QUOTATION_ISSUED", salesUserId,
                    $"v{quotation.Version}: total {quotation.TotalAmount:0}, disaster support {quotation.DisasterSupportAmount:0}, customer {quotation.CustomerAmount:0}, expires {quotation.ExpiresAt:O}", now);
                AfterSalesWorkflow.Move(data, tracked, ServiceRequestStatuses.AwaitingCustomer, salesUserId, null, now);
                AfterSalesHistory.Notify(data, tracked.CustomerUserId, "QUOTATION_ISSUED", $"A quotation for {tracked.RequestNumber} is ready ({quotation.CustomerAmount:N0} VND).", now, requestId);
                await data.SaveChangesAsync(token);
                return (OperationResult<bool>.Ok(true), true);
            }, cancellationToken);
            return result.Succeeded ? await ViewAsync(requestId, forStaff: true, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException)
        {
            // Another quotation reserved support from the same balance at the same time: re-read and issue again.
            return Fail(AfterSalesErrorCodes.StaleRevision, "The request or its protection balance changed meanwhile. Reload and try again.");
        }
    }

    // ================================================================== Customer

    /// <summary>Accepts the whole quotation (before it expires). Acceptance does not extend the payment deadline. Nothing to pay → authorized at once.</summary>
    public async Task<OperationResult<ServiceRequestView>> AcceptAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
        => await CustomerMutateAsync(customerUserId, quotationId, async (quotation, request, now, token) =>
        {
            if (quotation.Status != QuotationStatuses.Issued) return Bad($"This quotation is {quotation.Status}.");
            if (quotation.ExpiresAt <= now) return OperationResult<bool>.Fail(AfterSalesErrorCodes.QuotationExpired, "This quotation has expired.");
            quotation.Status = QuotationStatuses.Accepted;
            quotation.AcceptedAt = now;
            quotation.UpdatedAt = now;
            quotation.Revision++;
            AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "QUOTATION_ACCEPTED", customerUserId, $"v{quotation.Version}", now);
            if (quotation.CustomerAmount == 0) await AfterSalesWorkflow.MarkQuotationPaidAsync(data, quotation, request, customerUserId, now, now, token);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);

    /// <summary>
    /// Rejects the whole quotation: its items are declined (that work is not done), its disaster reservation is released, and free warranty
    /// items continue. A running checkout link is cancelled first; if the provider reports it paid, the rejection is refused.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> RejectAsync(Guid customerUserId, Guid quotationId, string? reason, CancellationToken cancellationToken)
    {
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        if (quotation?.CurrentPaymentAttemptId is { } attemptId)
        {
            var attempt = await payments.CancelAttemptAsync(attemptId, "Quotation rejected by customer", cancellationToken);
            if (attempt?.Status == PaymentStatuses.Paid) return Fail(AfterSalesErrorCodes.InvalidState, "This quotation has already been paid.");
        }
        return await CustomerMutateAsync(customerUserId, quotationId, async (q, request, now, token) =>
        {
            if (!QuotationStatuses.IsOpen(q.Status)) return Bad($"This quotation is {q.Status}.");
            await AfterSalesWorkflow.CloseQuotationAsync(data, q, QuotationStatuses.Rejected, ItemStatuses.Declined, customerUserId, now, token);
            request.CustomerDecisionReason = Text.Trim(reason, 1000);
            var items = await data.ListItemsAsync(request.Id, true, token);
            var next = request.Kind == ServiceRequestKinds.OneTimeMaintenance ? ServiceRequestStatuses.Cancelled : AfterSalesWorkflow.NextStatus(items);
            // A supplementary quotation rejected while the technician is on site leaves the visit running.
            if (request.Status is ServiceRequestStatuses.InProgress or ServiceRequestStatuses.Scheduled) return OperationResult<bool>.Ok(true);
            if (next == ServiceRequestStatuses.Cancelled)
            {
                request.CancelledAt = now;
                request.CancellationReason = request.CustomerDecisionReason ?? "Quotation rejected";
            }
            AfterSalesWorkflow.Move(data, request, next, customerUserId, "quotation rejected", now);
            return OperationResult<bool>.Ok(true);
        }, cancellationToken);
    }

    /// <summary>
    /// The PayOS checkout of an accepted quotation (one live link at a time; a repeated call returns it). The link lives at most the
    /// configured checkout time and never past the quotation's expiry, so a quotation never expires while a payment can still arrive.
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> CheckoutAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        var request = quotation is null ? null : await data.FindRequestAsync(quotation.RequestId, false, cancellationToken);
        if (quotation is null || request?.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        var now = time.GetUtcNow();
        if (quotation.Status != QuotationStatuses.Accepted)
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.InvalidState, "Accept the quotation before paying.");
        if (quotation.ExpiresAt <= now) return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationExpired, "This quotation has expired.");
        if (quotation.CurrentPaymentAttemptId is { } current && await payments.GetAttemptForCustomerAsync(customerUserId, current, reconcile: true, cancellationToken) is { } live
            && PaymentStatuses.Payable.Contains(live.Status) && live.ExpiresAt > now)
            return OperationResult<PaymentAttemptView>.Ok(live);

        var policy = await policies.GetAsync(cancellationToken);
        var expiresAt = now.AddMinutes(policy.Get(AfterSalesSettingKeys.CheckoutExpiryMinutes));
        if (expiresAt > quotation.ExpiresAt) expiresAt = quotation.ExpiresAt;
        var itemType = request.Kind == ServiceRequestKinds.OneTimeMaintenance ? PaymentItemTypes.MaintenanceQuotation : PaymentItemTypes.RepairQuotation;
        return await payments.CreateCheckoutAsync(customerUserId, null, [new CheckoutItem(itemType, null, quotation.Id, quotation.CustomerAmount)], expiresAt,
            async (attemptId, token) =>
            {
                // Same transaction as the new attempt: the quotation points to exactly one live checkout.
                var tracked = await data.FindQuotationAsync(quotationId, true, token);
                if (tracked is null || tracked.Status != QuotationStatuses.Accepted || tracked.Revision != quotation.Revision)
                    return "The quotation changed meanwhile. Reload and try again.";
                tracked.CurrentPaymentAttemptId = attemptId;
                tracked.UpdatedAt = time.GetUtcNow();
                tracked.Revision++;
                return null;
            }, cancellationToken);
    }

    // ================================================================== helpers

    private async Task<OperationResult<ServiceRequestView>> CustomerMutateAsync(Guid customerUserId, Guid quotationId,
        Func<ServiceQuotation, ServiceRequest, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        Guid? requestId = null;
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var quotation = await data.FindQuotationAsync(quotationId, true, token);
                var request = quotation is null ? null : await data.FindRequestAsync(quotation.RequestId, true, token);
                if (quotation is null || request is null || request.CustomerUserId != customerUserId)
                    return (OperationResult<bool>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found."), false);
                requestId = request.Id;
                var outcome = await change(quotation, request, time.GetUtcNow(), token);
                if (!outcome.Succeeded) return (outcome, false);
                await data.SaveChangesAsync(token);
                return (outcome, true);
            }, cancellationToken);
            return result.Succeeded ? await ViewAsync(requestId!.Value, forStaff: false, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The quotation changed meanwhile. Reload and try again.");
        }
    }

    private async Task<OperationResult<ServiceRequestView>> ViewAsync(Guid requestId, bool forStaff, CancellationToken token)
        => OperationResult<ServiceRequestView>.Ok(await views.RequestAsync((await data.FindRequestAsync(requestId, false, token))!, forStaff, token));

    private static OperationResult<bool> Bad(string message) => OperationResult<bool>.Fail(AfterSalesErrorCodes.InvalidState, message);
    private static OperationResult<ServiceRequestView> Fail(string code, string message) => OperationResult<ServiceRequestView>.Fail(code, message);
    private static OperationResult<ServiceRequestView> Invalid(string field, string message)
        => OperationResult<ServiceRequestView>.Fail(AfterSalesErrorCodes.ValidationFailed, "One or more fields are invalid.", new Dictionary<string, string[]> { [field] = [message] });
}
