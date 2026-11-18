using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.Quotations;

/// <summary>Payment state of an agreed installation quotation (derived; the attempts are the source).</summary>
public static class QuotationPaymentStates
{
    /// <summary>The quotation is not agreed (or expired / closed): nothing can be paid.</summary>
    public const string NotPayable = "NOT_PAYABLE";
    /// <summary>Agreed on site, no payment open: the technician opens ONLINE (QR) or CASH.</summary>
    public const string AwaitingPayment = "AWAITING_PAYMENT";
    /// <summary>A PayOS link / QR is open on the technician's device.</summary>
    public const string OnlineInProgress = "ONLINE_IN_PROGRESS";
    /// <summary>Cash was chosen; a Manager/Admin must record the receipt. Not paid yet.</summary>
    public const string AwaitingCashConfirmation = "AWAITING_CASH_CONFIRMATION";
    /// <summary>Payment verified and the contract created from the paid quotation.</summary>
    public const string Paid = "PAID";
    /// <summary>Money arrived but could not be applied (wrong amount, quotation closed meanwhile …): manual handling, no contract.</summary>
    public const string ReviewRequired = "REVIEW_REQUIRED";
}

/// <param name="ContractId">The contract created from the paid quotation (null until the payment is verified).</param>
public sealed record QuotationPaymentView(Guid QuotationId, string QuoteNumber, int QuotationVersion, string QuotationStatus, decimal Amount, string Currency,
    string PaymentState, PaymentAttemptView? Current, IReadOnlyList<PaymentAttemptView> Attempts, Guid? ContractId);

/// <summary>
/// Payment BEFORE contract (technician-led flow, 2026-11-18). After the technician recorded the customer's on-site agreement, the same technician
/// opens the payment of exactly the agreed quotation total:
/// <list type="bullet">
/// <item>ONLINE: a PayOS checkout whose QR / link is shown on the technician's device; the customer pays with their banking app without logging in
/// to SmartSolar. Only the verified webhook or a server-to-server status query marks it PAID (never a browser return URL).</item>
/// <item>CASH: a pending cash choice; the technician cannot mark it paid. A Manager/Admin records the receipt (amount, reference, evidence) with
/// <c>POST /api/manager/payments/{attemptId}/cash/confirm</c>.</item>
/// </list>
/// One payment at a time per quotation (quotation row lock + check in the creating transaction); a verified payment creates exactly one contract
/// from the paid snapshot (<see cref="CustomerContracts.InstallationQuotationPaymentSettlement"/>). A late, duplicate or wrong-amount payment is
/// never applied twice: it is flagged for manual handling.
/// </summary>
public sealed class OnSiteQuotationPaymentHandlers(IQuotationUnitOfWork quotations, PaymentService payments, PaymentOptions paymentOptions, TimeProvider time)
{
    public async Task<OperationResult<QuotationPaymentView>> StartAsync(Guid technicianUserId, Guid quotationId, string? method, CancellationToken cancellationToken)
    {
        var chosen = method?.Trim().ToUpperInvariant();
        if (chosen is not (PaymentMethods.Online or PaymentMethods.Cash))
            return OperationResult<QuotationPaymentView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["method"] = ["Must be ONLINE or CASH."] });
        var (quotation, error) = await OwnedAsync(technicianUserId, quotationId, cancellationToken);
        if (error is not null) return error;
        var q = quotation!;
        var now = time.GetUtcNow();
        if (q.Status != QuotationStatuses.Accepted)
            return Fail(QuotationErrorCodes.NotAgreed, $"Record the customer's agreement first; this quotation is {q.Status}.");
        if (q.ValidUntil is not { } validUntil || validUntil <= now)
            return Fail(QuotationErrorCodes.Expired, "This quotation has expired; create a new version with current catalog prices.");
        if (q.TotalAmount <= 0 || decimal.Truncate(q.TotalAmount) != q.TotalAmount)
            return Fail(PaymentErrorCodes.AmountNotSupported, "The quotation total must be a whole VND amount.");

        // A payment already open or verified: reconcile an online one first; the same method again returns it (idempotent).
        foreach (var attempt in await payments.ListInstallationQuotationAttemptsAsync(quotationId, cancellationToken))
        {
            var current = PaymentStatuses.Payable.Contains(attempt.Status) && attempt.Provider != PaymentProviders.Cash
                ? await payments.ReconcileAsync(attempt.Id, force: true, cancellationToken) ?? attempt
                : attempt;
            if (current.Status == PaymentStatuses.Paid) return Fail(PaymentErrorCodes.AlreadyPaid, "This quotation is already paid.");
            if (!PaymentStatuses.Payable.Contains(current.Status)) continue;
            var view = PaymentService.View(current);
            if (view.Method == chosen) return OperationResult<QuotationPaymentView>.Ok(await ViewAsync(q, cancellationToken));
            return view.Method == PaymentMethods.Cash
                ? Fail(PaymentErrorCodes.CashSelected, "Cash was chosen for this quotation. Cancel the cash choice first to show a QR code.")
                : Fail(PaymentErrorCodes.CheckoutInProgress, "An online payment is open for this quotation. Cancel it first to take cash.");
        }

        var item = new CheckoutItem(PaymentItemTypes.InstallationQuotation, null, null, q.TotalAmount, q.Id);
        async Task<string?> Reserve(Guid attemptId, CancellationToken token)
        {
            // Same transaction as the new attempt, in the global lock order survey_request → quotation: never two live payments for one quotation.
            await quotations.LockSurveyRequestAsync(q.SurveyRequestId, token);
            await quotations.LockAsync(q.Id, token);
            var fresh = await quotations.FindAsync(q.Id, false, token);
            if (fresh is null || fresh.Status != QuotationStatuses.Accepted || fresh.Revision != q.Revision) return "The quotation changed meanwhile. Reload and try again.";
            if (await quotations.HasPaymentInProgressOrPaidAsync(q.Id, token)) return "A payment of this quotation is already open or verified.";
            if (await quotations.HasLiveContractAsync(q.Id, token)) return "A contract already exists for this quotation.";
            return null;
        }

        var created = chosen == PaymentMethods.Cash
            ? await payments.CreateCashCheckoutAsync(q.CustomerUserId, null, [item], Reserve, cancellationToken, PaymentSources.Technician)
            : await payments.CreateCheckoutAsync(q.CustomerUserId, null, [item],
                Min(now.AddMinutes(Math.Clamp(paymentOptions.LinkExpiryMinutes, 5, 1440)), validUntil), Reserve, cancellationToken, PaymentSources.Technician);
        if (!created.Succeeded) return created.As<QuotationPaymentView>();
        return OperationResult<QuotationPaymentView>.Ok(await ViewAsync(q, cancellationToken));
    }

    /// <summary>The quotation's payment for its technician (an open online link is checked with the provider, throttled).</summary>
    public async Task<OperationResult<QuotationPaymentView>> GetAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var (quotation, error) = await OwnedAsync(technicianUserId, quotationId, cancellationToken);
        if (error is not null) return error;
        foreach (var live in (await payments.ListInstallationQuotationAttemptsAsync(quotationId, cancellationToken)).Where(a => PaymentStatuses.Payable.Contains(a.Status)))
            await payments.ReconcileAsync(live.Id, force: false, cancellationToken);
        return OperationResult<QuotationPaymentView>.Ok(await ViewAsync(quotation!, cancellationToken));
    }

    /// <summary>Cancels the open payment (to switch method, or the customer stopped). A payment the provider reports paid is confirmed instead.</summary>
    public async Task<OperationResult<QuotationPaymentView>> CancelAsync(Guid technicianUserId, Guid quotationId, CancellationToken cancellationToken)
    {
        var (quotation, error) = await OwnedAsync(technicianUserId, quotationId, cancellationToken);
        if (error is not null) return error;
        var live = (await payments.ListInstallationQuotationAttemptsAsync(quotationId, cancellationToken)).FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        if (live is null) return Fail(PaymentErrorCodes.AttemptNotFound, "There is no open payment for this quotation.");
        var ended = await payments.CancelAttemptAsync(live.Id, "cancelled on site by the technician", cancellationToken);
        if (ended?.Status == PaymentStatuses.Paid) return Fail(PaymentErrorCodes.AlreadyPaid, "The payment was completed meanwhile; it cannot be cancelled.");
        return OperationResult<QuotationPaymentView>.Ok(await ViewAsync(quotation!, cancellationToken));
    }

    private async Task<(Quotation? Quotation, OperationResult<QuotationPaymentView>? Error)> OwnedAsync(Guid technicianUserId, Guid quotationId,
        CancellationToken cancellationToken)
    {
        var quotation = await quotations.FindAsync(quotationId, false, cancellationToken);
        if (quotation is null || (await quotations.GetSurveyBasisAsync(quotation.SurveyRequestId, cancellationToken))?.TechnicianUserId != technicianUserId)
            return (null, Fail(QuotationErrorCodes.NotFound, "Quotation was not found."));
        if (QuotationHandlers.IsClosedRequest((await quotations.GetSourceAsync(quotation.SurveyRequestId, cancellationToken))?.SurveyRequestStatus)
            && quotation.Status != QuotationStatuses.Accepted)
            return (null, Fail(QuotationErrorCodes.RequestClosed, "The survey request was withdrawn or closed."));
        return (quotation, null);
    }

    private async Task<QuotationPaymentView> ViewAsync(Quotation q, CancellationToken cancellationToken)
    {
        var attempts = await payments.ListInstallationQuotationAttemptsAsync(q.Id, cancellationToken);
        var views = attempts.Select(a => PaymentService.View(a)).ToList();
        var paid = attempts.FirstOrDefault(a => a.Status == PaymentStatuses.Paid);
        var live = attempts.FirstOrDefault(a => PaymentStatuses.Payable.Contains(a.Status));
        var state = paid is { ContractId: not null } ? QuotationPaymentStates.Paid
            : live is not null ? (live.Provider == PaymentProviders.Cash ? QuotationPaymentStates.AwaitingCashConfirmation : QuotationPaymentStates.OnlineInProgress)
            : paid is not null || attempts.Any(a => a.Status is PaymentStatuses.ReviewRequired or PaymentStatuses.RefundRequired) ? QuotationPaymentStates.ReviewRequired
            : q.Status == QuotationStatuses.Accepted && q.ValidUntil > time.GetUtcNow() ? QuotationPaymentStates.AwaitingPayment
            : QuotationPaymentStates.NotPayable;
        var current = paid ?? live;
        return new QuotationPaymentView(q.Id, q.QuoteNumber, q.Version, q.Status, q.TotalAmount, q.Currency, state,
            current is null ? null : PaymentService.View(current), views, paid?.ContractId);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
    private static OperationResult<QuotationPaymentView> Fail(string code, string message) => OperationResult<QuotationPaymentView>.Fail(code, message);
}
