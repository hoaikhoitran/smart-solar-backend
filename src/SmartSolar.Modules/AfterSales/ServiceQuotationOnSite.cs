using SmartSolar.Modules.Common.Persistence;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.AfterSales;

/// <summary>What the customer decided on site about a technician quotation (warranty / repair / one-time maintenance).</summary>
public static class ServiceOnSiteDecisions
{
    public const string Agreed = "AGREED";
    public const string Declined = "DECLINED";
    public static readonly IReadOnlyList<string> All = [Agreed, Declined];
}

/// <summary>Payment of an after-sales quotation as the technician sees it on site.</summary>
/// <param name="Current">The live or verified attempt (null = nothing opened; also when nothing is due).</param>
/// <param name="NothingToPay">Fully covered (customer amount 0): the work is authorized without any checkout.</param>
public sealed record ServiceQuotationPaymentView(Guid QuotationId, int Version, string QuotationStatus, decimal CustomerAmount, bool NothingToPay,
    PaymentAttemptView? Current);

/// <summary>
/// Technician-led after-sales quotations (2026-11-18). The technician who inspected the equipment prices the chargeable items
/// (<see cref="IssueByTechnicianAsync"/>; coverage stays the Manager's decision), shows the quotation on their device, asks the customer in person and
/// records the answer with their own account — the customer does not open the app to approve. AGREED: nothing due (fully covered) → the work is
/// authorized at once, never a 0 VND checkout; otherwise the technician opens the payment (PayOS QR on the device, or cash confirmed by a
/// Manager/Admin) and paid work is authorized only after the payment is verified. DECLINED: the quotation's items are declined, nothing is charged,
/// free items continue. Only a technician with a live (not declined / cancelled) visit on the request may act; anyone else gets 404.
/// </summary>
public sealed partial class ServiceQuotationHandlers
{
    public async Task<OperationResult<ServiceRequestView>> RecordOnSiteDecisionAsync(Guid technicianUserId, Guid quotationId, string? decision, string? note,
        CancellationToken cancellationToken)
    {
        var chosen = decision?.Trim().ToUpperInvariant();
        var why = Text.Trim(note, 1000);
        if (chosen is null || !ServiceOnSiteDecisions.All.Contains(chosen)) return Invalid("decision", "Must be AGREED or DECLINED.");
        if (chosen == ServiceOnSiteDecisions.Declined && why is null) return Invalid("note", "Record what the customer said (at most 1000 characters).");
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        if (quotation is null || !await AssignedAsync(technicianUserId, quotation.RequestId, cancellationToken))
            return Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        if (chosen == ServiceOnSiteDecisions.Declined
            && await CancelLinkBeforeRejectAsync(quotation, "Quotation declined on site (recorded by the technician)", cancellationToken) is { } paid)
            return paid;

        return await TechnicianMutateAsync(technicianUserId, quotationId, (q, request, now, token) => chosen == ServiceOnSiteDecisions.Agreed
            ? AcceptChangeAsync(q, request, technicianUserId, "QUOTATION_AGREED_ON_SITE",
                $"v{q.Version}: customer agreed on site, recorded by the technician; {q.CustomerAmount:0} VND" + (why is null ? "" : $"; note: {why}"), now, token)
            : RecordDeclineAsync(q, request, technicianUserId, why!, now, token), cancellationToken);
    }

    private async Task<OperationResult<bool>> RecordDeclineAsync(ServiceQuotation q, ServiceRequest request, Guid technicianUserId, string why, DateTimeOffset now,
        CancellationToken token)
    {
        var result = await RejectChangeAsync(q, request, technicianUserId, why, "quotation declined on site", now, token);
        if (result.Succeeded)
            AfterSalesHistory.Record(data, EventSubjects.Request, request.Id, "QUOTATION_DECLINED_ON_SITE", technicianUserId, $"v{q.Version}: {why}", now);
        return result;
    }

    /// <summary>
    /// Opens the payment of an AGREED quotation on site: ONLINE (PayOS QR / link to show on the device; repeated → the same open link) or CASH (a
    /// Manager/Admin records the receipt). Same rules as the customer's checkout (one live payment per quotation, never past the quotation's expiry).
    /// </summary>
    public async Task<OperationResult<PaymentAttemptView>> StartPaymentOnSiteAsync(Guid technicianUserId, Guid quotationId, string? method,
        CancellationToken cancellationToken)
    {
        var chosen = method?.Trim().ToUpperInvariant();
        if (chosen is not (PaymentMethods.Online or PaymentMethods.Cash))
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["method"] = ["Must be ONLINE or CASH."] });
        var (quotation, request) = await OwnedForTechnicianAsync(technicianUserId, quotationId, cancellationToken);
        if (quotation is null || request is null) return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        if (quotation.Status == QuotationStatuses.Paid) return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "This quotation is already paid.");
        if (quotation.Status == QuotationStatuses.Accepted && quotation.CustomerAmount == 0)
            return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.InvalidState, "Nothing is due: the work is already authorized.");
        return chosen == PaymentMethods.Cash
            ? await SelectCashAsync(request.CustomerUserId, quotationId, cancellationToken, PaymentSources.Technician, technicianUserId)
            : await CheckoutAsync(request.CustomerUserId, quotationId, cancellationToken, PaymentSources.Technician);
    }

    /// <summary>The quotation's payment on site (an open online link is checked server-to-server, throttled; a browser return never marks it paid).</summary>
    public async Task<OperationResult<ServiceQuotationPaymentView>> GetPaymentOnSiteAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var (quotation, request) = await OwnedForTechnicianAsync(technicianUserId, quotationId, cancellationToken);
        if (quotation is null || request is null)
            return OperationResult<ServiceQuotationPaymentView>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        var current = quotation.CurrentPaymentAttemptId is { } attemptId
            ? await payments.GetAttemptForCustomerAsync(request.CustomerUserId, attemptId, reconcile: true, cancellationToken)
            : null;
        quotation = (await data.FindQuotationAsync(quotationId, false, cancellationToken))!;
        return OperationResult<ServiceQuotationPaymentView>.Ok(new ServiceQuotationPaymentView(quotation.Id, quotation.Version, quotation.Status, quotation.CustomerAmount,
            quotation.CustomerAmount == 0, current));
    }

    /// <summary>Cancels the open payment of the quotation on site (switch method, or the customer stopped). One the provider reports paid stays paid.</summary>
    public async Task<OperationResult<PaymentAttemptView>> CancelPaymentOnSiteAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var (quotation, request) = await OwnedForTechnicianAsync(technicianUserId, quotationId, cancellationToken);
        if (quotation is null || request is null) return OperationResult<PaymentAttemptView>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found.");
        if (quotation.CurrentPaymentAttemptId is not { } current
            || await payments.GetAttemptForCustomerAsync(request.CustomerUserId, current, reconcile: false, cancellationToken) is not { } live
            || !PaymentStatuses.Payable.Contains(live.Status))
            return OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AttemptNotFound, "There is no open payment for this quotation.");
        var ended = await payments.CancelAttemptAsync(current, "cancelled on site by the technician", cancellationToken);
        return ended?.Status == PaymentStatuses.Paid
            ? OperationResult<PaymentAttemptView>.Fail(PaymentErrorCodes.AlreadyPaid, "The payment was completed meanwhile; it cannot be cancelled.")
            : OperationResult<PaymentAttemptView>.Ok((await payments.GetAttemptForCustomerAsync(request.CustomerUserId, current, reconcile: false, cancellationToken))!);
    }

    private async Task<bool> AssignedAsync(Guid technicianUserId, Guid requestId, CancellationToken cancellationToken)
        => (await data.ListVisitsAsync(requestId, false, cancellationToken))
            .Any(v => v.TechnicianUserId == technicianUserId && v.Status is not (VisitStatuses.Declined or VisitStatuses.Cancelled));

    private async Task<(ServiceQuotation? Quotation, ServiceRequest? Request)> OwnedForTechnicianAsync(Guid technicianUserId, Guid quotationId,
        CancellationToken cancellationToken)
    {
        var quotation = await data.FindQuotationAsync(quotationId, false, cancellationToken);
        if (quotation is null || !await AssignedAsync(technicianUserId, quotation.RequestId, cancellationToken)) return (null, null);
        return (quotation, await data.FindRequestAsync(quotation.RequestId, false, cancellationToken));
    }

    private async Task<OperationResult<ServiceRequestView>> TechnicianMutateAsync(Guid technicianUserId, Guid quotationId,
        Func<ServiceQuotation, ServiceRequest, DateTimeOffset, CancellationToken, Task<OperationResult<bool>>> change, CancellationToken cancellationToken)
    {
        Guid? requestId = null;
        try
        {
            var result = await data.ExecuteInTransactionAsync<OperationResult<bool>>(async token =>
            {
                var quotation = await data.FindQuotationAsync(quotationId, true, token);
                var request = quotation is null ? null : await data.FindRequestAsync(quotation.RequestId, true, token);
                if (quotation is null || request is null || !await AssignedAsync(technicianUserId, request.Id, token))
                    return (OperationResult<bool>.Fail(AfterSalesErrorCodes.QuotationNotFound, "Quotation was not found."), false);
                requestId = request.Id;
                var outcome = await change(quotation, request, time.GetUtcNow(), token);
                if (!outcome.Succeeded) return (outcome, false);
                await data.SaveChangesAsync(token);
                return (outcome, true);
            }, cancellationToken);
            return result.Succeeded ? await ViewAsync(requestId!.Value, forStaff: true, cancellationToken) : result.As<ServiceRequestView>();
        }
        catch (PersistenceConflictException)
        {
            return Fail(AfterSalesErrorCodes.StaleRevision, "The quotation changed meanwhile. Reload and try again.");
        }
    }
}
