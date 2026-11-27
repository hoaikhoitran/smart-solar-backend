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
public sealed partial class ServiceQuotationHandlers(
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
        // An inspected one-time maintenance uses the repair queue task opened by the coverage decision; a legacy one uses its maintenance task.
        var inspected = (await data.ListItemsAsync(requestId, false, cancellationToken)).Count > 0;
        var taskType = request.Kind == ServiceRequestKinds.OneTimeMaintenance && !inspected ? SalesTaskTypes.MaintenanceQuotation : SalesTaskTypes.RepairQuotation;
        var task = await data.FindOpenTaskAsync(requestId, null, taskType, false, cancellationToken);
        // Superseding an issued quotation is done by the Sales user who issued it (no new task is opened for a correction).
        var open = (await data.ListQuotationsAsync(requestId, false, cancellationToken)).FirstOrDefault(q => QuotationStatuses.IsOpen(q.Status));
        var mayIssue = task is not null && task.AssignedSalesUserId == salesUserId || open is not null && open.SalesUserId == salesUserId;
        if (!mayIssue) return Fail(Common.Results.CommonErrorCodes.Forbidden, "Claim the quotation task in the Sales queue first.");
        return await IssueCoreAsync(salesUserId, QuotationIssuers.Sales, request, open, taskType, input, null, cancellationToken);
    }

    /// <summary>
    /// The shared issue step (Sales or technician): state and live-checkout checks, line validation (every open chargeable / disaster item priced
    /// exactly once), repair warranty, supersede of the open version, disaster support reservation, items QUOTED, queue task closed, request
    /// AWAITING_CUSTOMER, customer notified — one transaction. <paramref name="componentsByItem"/> carries the catalog snapshot of a technician
    /// quotation (null for Sales: the stored line shape is unchanged).
    /// </summary>
    private async Task<OperationResult<ServiceRequestView>> IssueCoreAsync(Guid salesUserId, string issuerRole, ServiceRequest request, ServiceQuotation? open,
        string taskType, IssueQuotationInput input, IReadOnlyDictionary<Guid, IReadOnlyList<QuotationComponentSnapshot>>? componentsByItem,
        CancellationToken cancellationToken)
    {
        var requestId = request.Id;
        if (request.Status is not (ServiceRequestStatuses.AwaitingQuotation or ServiceRequestStatuses.AwaitingCustomer))
            return Fail(AfterSalesErrorCodes.InvalidState, $"A {request.Status} request does not need a quotation.");
        if (open?.CurrentPaymentAttemptId is { } liveAttempt && await data.FindPaymentAttemptAsync(liveAttempt, cancellationToken) is { } attempt
            && PaymentStatuses.Payable.Contains(attempt.Status) && attempt.ExpiresAt > time.GetUtcNow())
            return Fail(PaymentErrorCodes.CheckoutInProgress, "The customer is paying the current version; it cannot be replaced now.");
        // Cash a technician reported as taken for the current version is reviewed first: replacing it would detach the receipt from its quotation.
        if (await data.HasCashReceiptUnderReviewAsync(requestId, cancellationToken)) return UnderReview<ServiceRequestView>();

        // ---- validate the lines
        if (input.Lines.Count is 0 or > 50) return Invalid("lines", "Give between 1 and 50 lines.");
        foreach (var line in input.Lines)
        {
            if (line.PartsAmount < 0 || line.LaborAmount < 0 || line.PartsAmount + line.LaborAmount <= 0
                || decimal.Truncate(line.PartsAmount) != line.PartsAmount || decimal.Truncate(line.LaborAmount) != line.LaborAmount)
                return Invalid("lines", "Amounts are whole VND, not negative, and every line costs something.");
        }
        var items = await data.ListItemsAsync(requestId, false, cancellationToken);
        // FAULT requests and inspected one-time maintenance price their diagnosed items; a legacy one-time maintenance (no inspection) has free lines.
        if (request.Kind == ServiceRequestKinds.Fault || items.Count > 0)
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
                if (await data.HasCashReceiptUnderReviewAsync(requestId, token)) return (UnderReview<bool>(), false);
                var now = time.GetUtcNow();
                var quotations = await data.ListQuotationsAsync(requestId, true, token);
                foreach (var previous in quotations.Where(q => QuotationStatuses.IsOpen(q.Status)))
                    await AfterSalesWorkflow.CloseQuotationAsync(data, previous, QuotationStatuses.Superseded, ItemStatuses.Open, salesUserId, now, token);
                // Free the "one open quotation" slot (and the released support) before the new version is inserted.
                await data.SaveChangesAsync(token);
                var trackedItems = await data.ListItemsAsync(requestId, true, token);
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var linesJson = componentsByItem is null
                    ? JsonSerializer.Serialize(input.Lines.Select(l => new
                    {
                        itemId = l.ItemId,
                        description = Text.Trim(l.Description, 300) ?? trackedItems.First(i => i.Id == l.ItemId).Description,
                        partsAmount = l.PartsAmount,
                        laborAmount = l.LaborAmount,
                    }).ToList(), json)
                    : JsonSerializer.Serialize(input.Lines.Select(l => new
                    {
                        itemId = l.ItemId,
                        description = Text.Trim(l.Description, 300) ?? trackedItems.First(i => i.Id == l.ItemId).Description,
                        partsAmount = l.PartsAmount,
                        laborAmount = l.LaborAmount,
                        components = componentsByItem[l.ItemId!.Value],
                    }).ToList(), json);

                var quotation = new ServiceQuotation
                {
                    Id = Guid.NewGuid(), RequestId = requestId, Version = quotations.Count == 0 ? 1 : quotations.Max(q => q.Version) + 1,
                    IsSupplementary = quotations.Any(q => q.Status == QuotationStatuses.Paid),
                    SupersedesQuotationId = quotations.Where(q => q.Status == QuotationStatuses.Superseded).OrderByDescending(q => q.Version).FirstOrDefault()?.Id,
                    Status = QuotationStatuses.Issued, SalesUserId = salesUserId, IssuedByRole = issuerRole, LinesJson = linesJson,
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
                    // A task nobody claimed (the technician priced the request) is no longer needed: CANCELLED, never DONE without an owner.
                    openTask.Status = openTask.AssignedSalesUserId is null ? SalesTaskStatuses.Cancelled : SalesTaskStatuses.Done;
                    openTask.CompletedAt = now;
                    openTask.Outcome = issuerRole == QuotationIssuers.Technician ? "QUOTATION_ISSUED_BY_TECHNICIAN" : "QUOTATION_ISSUED";
                    openTask.UpdatedAt = now;
                    openTask.Revision++;
                }
                AfterSalesHistory.Record(data, EventSubjects.Request, requestId, "QUOTATION_ISSUED", salesUserId,
                    $"v{quotation.Version} by {issuerRole}: total {quotation.TotalAmount:0}, disaster support {quotation.DisasterSupportAmount:0}, customer {quotation.CustomerAmount:0}, expires {quotation.ExpiresAt:O}", now);
                AfterSalesWorkflow.Move(data, tracked, ServiceRequestStatuses.AwaitingCustomer, salesUserId, null, now);
                // A technician quotation is presented and decided on site (2026-11-18): nothing is sent to the customer's app for approval.
                if (issuerRole != QuotationIssuers.Technician)
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

    /// <summary>
    /// Accepts the whole Sales-issued quotation (before it expires). Acceptance does not extend the payment deadline. Nothing to pay → authorized at
    /// once. A technician quotation is decided on site and recorded by the technician (409 SERVICE_QUOTATION_DECIDED_ON_SITE here).
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> AcceptAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
        => await CustomerMutateAsync(customerUserId, quotationId, async (quotation, request, now, token) =>
            quotation.IssuedByRole == QuotationIssuers.Technician ? DecidedOnSite()
                : await AcceptChangeAsync(quotation, request, customerUserId, "QUOTATION_ACCEPTED", $"v{quotation.Version}", now, token), cancellationToken);

    private async Task<OperationResult<bool>> AcceptChangeAsync(ServiceQuotation quotation, ServiceRequest request, Guid actorUserId, string eventType, string detail,
        DateTimeOffset now, CancellationToken token)
    {
        if (quotation.Status != QuotationStatuses.Issued) return Bad($"This quotation is {quotation.Status}.");
        if (quotation.ExpiresAt <= now) return OperationResult<bool>.Fail(AfterSalesErrorCodes.QuotationExpired, "This quotation has expired.");
        quotation.Status = QuotationStatuses.Accepted;
        quotation.AcceptedAt = now;
        quotation.UpdatedAt = now;
        quotation.Revision++;
        AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, eventType, actorUserId, detail, now);
        // Nothing to pay (fully covered by disaster support): authorized at once, never a 0 VND checkout.
        if (quotation.CustomerAmount == 0) await AfterSalesWorkflow.MarkQuotationPaidAsync(data, quotation, request, actorUserId, now, now, token);
        return OperationResult<bool>.Ok(true);
    }

    private static OperationResult<bool> DecidedOnSite()
        => OperationResult<bool>.Fail(AfterSalesErrorCodes.DecidedOnSite,
            "This quotation was made by the technician on site; the technician records the decision you take with them. Nothing is needed in the app.");

    /// <summary>
    /// Rejects the whole quotation: its items are declined (that work is not done), its disaster reservation is released, and free warranty
    /// items continue. A running checkout link is cancelled first; if the provider reports it paid, the rejection is refused.
    /// </summary>
    public async Task<OperationResult<ServiceRequestView>> RejectAsync(Guid customerUserId, Guid quotationId, string? reason, CancellationToken cancellationToken)
    {
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        if (quotation?.IssuedByRole == QuotationIssuers.Technician) return DecidedOnSite().As<ServiceRequestView>();
        if (await CancelLinkBeforeRejectAsync(quotation, "Quotation rejected by customer", cancellationToken) is { } paid) return paid;
        return await CustomerMutateAsync(customerUserId, quotationId,
            (q, request, now, token) => RejectChangeAsync(q, request, customerUserId, Text.Trim(reason, 1000), "quotation rejected", now, token), cancellationToken);
    }

    /// <summary>A running checkout link is cancelled first; if the provider reports it paid, the rejection is refused (returned).</summary>
    private async Task<OperationResult<ServiceRequestView>?> CancelLinkBeforeRejectAsync(ServiceQuotation? quotation, string why, CancellationToken cancellationToken)
    {
        if (quotation?.CurrentPaymentAttemptId is not { } attemptId) return null;
        if (await payments.HasCashReceiptUnderReviewAsync(attemptId, cancellationToken)) return UnderReview<ServiceRequestView>();
        var attempt = await payments.CancelAttemptAsync(attemptId, why, cancellationToken);
        if (attempt is not null && attempt.Provider == PaymentProviders.Cash && PaymentStatuses.Payable.Contains(attempt.Status)) return UnderReview<ServiceRequestView>();
        return attempt?.Status == PaymentStatuses.Paid ? Fail(AfterSalesErrorCodes.InvalidState, "This quotation has already been paid.") : null;
    }

    private async Task<OperationResult<bool>> RejectChangeAsync(ServiceQuotation q, ServiceRequest request, Guid actorUserId, string? reason, string moveNote,
        DateTimeOffset now, CancellationToken token)
    {
        if (!QuotationStatuses.IsOpen(q.Status)) return Bad($"This quotation is {q.Status}.");
        await AfterSalesWorkflow.CloseQuotationAsync(data, q, QuotationStatuses.Rejected, ItemStatuses.Declined, actorUserId, now, token);
        request.CustomerDecisionReason = reason;
        var items = await data.ListItemsAsync(request.Id, true, token);
        var next = request.Kind == ServiceRequestKinds.OneTimeMaintenance ? ServiceRequestStatuses.Cancelled : AfterSalesWorkflow.NextStatus(items);
        // A supplementary quotation rejected while the technician is on site leaves the visit running.
        if (request.Status is ServiceRequestStatuses.InProgress or ServiceRequestStatuses.Scheduled) return OperationResult<bool>.Ok(true);
        if (next == ServiceRequestStatuses.Cancelled)
        {
            request.CancelledAt = now;
            request.CancellationReason = request.CustomerDecisionReason ?? "Quotation rejected";
        }
        AfterSalesWorkflow.Move(data, request, next, actorUserId, moveNote, now);
        return OperationResult<bool>.Ok(true);
    }

    /// <summary>
    /// The PayOS checkout of an accepted quotation (one live link at a time; a repeated call returns it). The link lives at most the
    /// configured checkout time and never past the quotation's expiry, so a quotation never expires while a payment can still arrive.
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> CheckoutAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken,
        string source = PaymentSources.Customer)
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
            && PaymentStatuses.Payable.Contains(live.Status) && (live.ExpiresAt > now || live.Method == PaymentMethods.Cash))
            return live.Method == PaymentMethods.Cash
                ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashSelected, "Cash payment was chosen for this quotation. Cancel the cash choice first to pay online.")
                : OperationResult<PaymentAttemptView>.Ok(live);

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
            }, cancellationToken, source);
    }

    /// <summary>
    /// The customer chooses to pay an ACCEPTED quotation in cash (2026-11-18): a pending CASH attempt for exactly the customer amount, not a payment.
    /// A Manager/Admin confirms the receipt (<see cref="PaymentService.ConfirmCashAttemptAsync"/>) before the quotation expires; the choice never
    /// extends the expiry (the sweep withdraws it when the quotation expires). An open online link is checked with PayOS first (paid → refused;
    /// still open → cancel it first). Repeating the choice returns the same pending attempt.
    /// </summary>
    /// <param name="initiatedBy">The technician who recorded the cash choice on site (audit actor); null when the customer chose it in the app.</param>
    public async Task<OperationResult<PaymentAttemptView>> SelectCashAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken,
        string source = PaymentSources.Customer, Guid? initiatedBy = null)
    {
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        var request = quotation is null ? null : await data.FindRequestAsync(quotation.RequestId, false, cancellationToken);
        if (quotation is null || request?.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        var now = time.GetUtcNow();
        if (quotation.Status == QuotationStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This quotation is already paid.");
        if (quotation.Status != QuotationStatuses.Accepted)
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.InvalidState, "Accept the quotation before paying.");
        if (quotation.ExpiresAt <= now) return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationExpired, "This quotation has expired.");
        if (quotation.CurrentPaymentAttemptId is { } current && await payments.GetAttemptForCustomerAsync(customerUserId, current, reconcile: true, cancellationToken) is { } live)
        {
            if (live.Status == PaymentStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This quotation is already paid.");
            if (PaymentStatuses.Payable.Contains(live.Status))
                return live.Method == PaymentMethods.Cash
                    ? OperationResult<PaymentAttemptView>.Ok(live)
                    : OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CheckoutInProgress,
                        "An online payment is in progress. Cancel it first (its status is checked with the payment provider) to pay in cash.");
        }
        var itemType = request.Kind == ServiceRequestKinds.OneTimeMaintenance ? PaymentItemTypes.MaintenanceQuotation : PaymentItemTypes.RepairQuotation;
        var created = await payments.CreateCashCheckoutAsync(customerUserId, null, [new CheckoutItem(itemType, null, quotation.Id, quotation.CustomerAmount)],
            async (attemptId, token) =>
            {
                var tracked = await data.FindQuotationAsync(quotationId, true, token);
                if (tracked is null || tracked.Status != QuotationStatuses.Accepted || tracked.Revision != quotation.Revision)
                    return "The quotation changed meanwhile. Reload and try again.";
                tracked.CurrentPaymentAttemptId = attemptId;
                tracked.UpdatedAt = time.GetUtcNow();
                tracked.Revision++;
                AfterSalesHistory.Record(data, EventSubjects.Request, tracked.RequestId, "CASH_SELECTED", initiatedBy ?? customerUserId,
                    source == PaymentSources.Technician ? $"v{tracked.Version}: {tracked.CustomerAmount:0} VND (chosen on site, recorded by the technician)" : $"v{tracked.Version}: {tracked.CustomerAmount:0} VND",
                    tracked.UpdatedAt);
                return null;
            }, cancellationToken, source);
        if (created.ErrorCode == PaymentErrorCodes.CheckoutInProgress
            && (await data.FindQuotationAsync(quotationId, false, cancellationToken))?.CurrentPaymentAttemptId is { } winner
            && await payments.GetAttemptForCustomerAsync(customerUserId, winner, reconcile: false, cancellationToken) is { Method: PaymentMethods.Cash } same
            && PaymentStatuses.Payable.Contains(same.Status))
            return OperationResult<PaymentAttemptView>.Ok(same);   // a concurrent identical choice won: idempotent
        return created;
    }

    /// <summary>The customer withdraws the pending cash choice of a quotation (to pay online instead). A confirmed one stays PAID.</summary>
    public async Task<OperationResult<PaymentAttemptView>> CancelCashAsync(Guid customerUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        var request = quotation is null ? null : await data.FindRequestAsync(quotation.RequestId, false, cancellationToken);
        if (quotation is null || request?.CustomerUserId != customerUserId)
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        if (quotation.CurrentPaymentAttemptId is not { } current
            || await payments.GetAttemptForCustomerAsync(customerUserId, current, reconcile: false, cancellationToken) is not { Method: PaymentMethods.Cash } cash
            || !PaymentStatuses.Payable.Contains(cash.Status))
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.CashNotSelected, "There is no pending cash choice for this quotation.");
        if (await payments.HasCashReceiptUnderReviewAsync(current, cancellationToken)) return UnderReview<PaymentAttemptView>();
        var ended = await payments.CancelAttemptAsync(current, "cash choice cancelled by customer", cancellationToken);
        if (ended is not null && PaymentStatuses.Payable.Contains(ended.Status)) return UnderReview<PaymentAttemptView>();
        return OperationResult<PaymentAttemptView>.Ok((await payments.GetAttemptForCustomerAsync(customerUserId, current, reconcile: false, cancellationToken))!);
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
