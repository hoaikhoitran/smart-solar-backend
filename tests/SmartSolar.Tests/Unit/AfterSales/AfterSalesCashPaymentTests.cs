using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// AFTER-PAYMENT cash (2026-11-18): an ACCEPTED quotation can be paid in cash. The choice is a pending CASH attempt (not a payment); a Manager/Admin
/// confirms the exact amount with a unique receipt and evidence, before the quotation expires; the confirmation settles the quotation once and the
/// request moves on exactly as after a verified PayOS payment. A cash choice never extends the quotation: at expiry it is withdrawn and the quotation
/// expires as before (support released). PayOS stays unchanged; the two never run together.
/// </summary>
public sealed class AfterSalesCashPaymentTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(HandedOver H, Guid RequestId, ServiceQuotationView Quotation)> AcceptedAsync(AfterSalesTestContext ctx)
    {
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var issued = await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId, new TechnicianQuotationInput([Line(items[0], (breaker.Id, 2))], null, null), None);
        var q = Assert.Single(issued.Value!.Quotations);
        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, q.QuotationId, None)).Succeeded);
        return (h, requestId, q);
    }

    private static async Task<CashConfirmation> ReceiptAsync(AfterSalesTestContext ctx, Guid staffId, decimal amount, string reference = "PT-AS-1")
        => new(amount, reference, await ctx.Installation.VerifiedFileAsync(staffId, FilePurposes.PaymentEvidence), "Thu tại nhà khách");

    [Fact]
    public async Task AFTER_CASH_001_A_cash_choice_is_pending_until_staff_confirm_the_exact_amount_then_the_request_is_ready_for_work()
    {
        await using var ctx = Create();
        var (h, requestId, q) = await AcceptedAsync(ctx);

        var pending = await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None);
        Assert.True(pending.Succeeded, pending.Message);
        Assert.Equal((PaymentMethods.Cash, PaymentStatuses.Pending, 300_000m, (Guid?)null), (pending.Value!.Method, pending.Value.Status, pending.Value.Amount, pending.Value.ContractId));
        Assert.Equal(pending.Value.PaymentAttemptId, (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Value!.PaymentAttemptId);   // idempotent
        // Pending is not paid: the request still waits, no work can be scheduled.
        Assert.Equal(ServiceRequestStatuses.AwaitingCustomer, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);

        var paid = await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, pending.Value.PaymentAttemptId, await ReceiptAsync(ctx, h.ManagerId, 300_000m), None);
        Assert.True(paid.Succeeded, paid.Message);
        Assert.Equal((PaymentStatuses.Paid, h.ManagerId, 300_000m), (paid.Value!.Status, paid.Value.ConfirmedByUserId!.Value, paid.Value.AmountPaid!.Value));
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
        Assert.Equal(PaymentItemStatuses.Settled, (await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.QuotationId != null)).Status);
        // Audit: who, when, how much, which receipt.
        var stored = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.ContractId == null);
        Assert.Equal(("PT-AS-1", PaymentProviders.Cash), (stored.ProviderReference, stored.Provider));
        Assert.Contains(await ctx.Db.PaymentEvents.AsNoTracking().Where(e => e.PaymentAttemptId == stored.Id).Select(e => e.EventType).ToListAsync(), t => t == "CASH_CONFIRMED");
        Assert.Contains(await ctx.Db.AfterSalesEvents.AsNoTracking().Select(e => e.EventType).ToListAsync(), t => t == "CASH_SELECTED");
    }

    [Fact]
    public async Task AFTER_CASH_002_Wrong_amount_duplicate_confirmation_and_reused_receipt_are_refused()
    {
        await using var ctx = Create();
        var (h, _, q) = await AcceptedAsync(ctx);
        var attempt = (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Value!.PaymentAttemptId;
        var payments = ctx.Payments();

        Assert.Equal(PaymentErrorCodes.CashAmountMismatch, (await payments.ConfirmCashAttemptAsync(h.ManagerId, attempt, await ReceiptAsync(ctx, h.ManagerId, 299_000m), None)).ErrorCode);
        Assert.Equal("VALIDATION_FAILED", (await payments.ConfirmCashAttemptAsync(h.ManagerId, attempt, new CashConfirmation(300_000m, "", Guid.Empty, null), None)).ErrorCode);
        // Evidence uploaded by someone else is refused.
        var foreign = await ctx.Installation.VerifiedFileAsync(h.CustomerId, FilePurposes.PaymentEvidence);
        Assert.False((await payments.ConfirmCashAttemptAsync(h.ManagerId, attempt, new CashConfirmation(300_000m, "PT-X", foreign, null), None)).Succeeded);
        Assert.True((await payments.ConfirmCashAttemptAsync(h.ManagerId, attempt, await ReceiptAsync(ctx, h.ManagerId, 300_000m), None)).Succeeded);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.ConfirmCashAttemptAsync(h.ManagerId, attempt, await ReceiptAsync(ctx, h.ManagerId, 300_000m, "PT-AS-2"), None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.AttemptNotFound, (await payments.ConfirmCashAttemptAsync(h.ManagerId, Guid.NewGuid(), await ReceiptAsync(ctx, h.ManagerId, 1m), None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid));
    }

    [Fact]
    public async Task AFTER_CASH_003_A_cash_choice_does_not_extend_the_quotation_late_confirmation_is_refused_and_the_sweep_expires_it()
    {
        await using var ctx = Create();
        var (h, requestId, q) = await AcceptedAsync(ctx);
        var attempt = (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Value!.PaymentAttemptId;
        var receipt = await ReceiptAsync(ctx, h.ManagerId, 300_000m);

        ctx.Clock.Now = q.ExpiresAt.AddMinutes(1).ToUniversalTime();
        Assert.Equal(PaymentErrorCodes.CashItemNotPayable, (await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, attempt, receipt, None)).ErrorCode);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.ContractId == null)).Status);   // nothing recorded

        Assert.Equal(1, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);
        Assert.Equal(PaymentStatuses.Cancelled, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.ContractId == null)).Status);
        Assert.Equal(QuotationStatuses.Expired, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, attempt, receipt, None)).ErrorCode);
        Assert.NotEqual(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
    }

    [Fact]
    public async Task AFTER_CASH_004_Cash_and_PayOS_never_both_collect_and_PayOS_still_works()
    {
        await using var ctx = Create();
        var (h, _, q) = await AcceptedAsync(ctx);
        var quotations = ctx.Quotations();

        // Online link first: cash is refused while it is open; paid online → cash refused as already paid.
        var link = (await quotations.CheckoutAsync(h.CustomerId, q.QuotationId, None)).Value!;
        Assert.Equal(PaymentErrorCodes.CheckoutInProgress, (await quotations.SelectCashAsync(h.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(PaymentStatuses.Paid, (await ctx.PayAsync(link))!.Status);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await quotations.SelectCashAsync(h.CustomerId, q.QuotationId, None)).ErrorCode);

        // Cash first: the online checkout is refused until the cash choice is withdrawn.
        await using var other = Create();
        var (h2, _, q2) = await AcceptedAsync(other);
        Assert.True((await other.Quotations().SelectCashAsync(h2.CustomerId, q2.QuotationId, None)).Succeeded);
        Assert.Equal(PaymentErrorCodes.CashSelected, (await other.Quotations().CheckoutAsync(h2.CustomerId, q2.QuotationId, None)).ErrorCode);
        Assert.Equal(PaymentStatuses.Cancelled, (await other.Quotations().CancelCashAsync(h2.CustomerId, q2.QuotationId, None)).Value!.Status);
        var online = await other.Quotations().CheckoutAsync(h2.CustomerId, q2.QuotationId, None);
        Assert.True(online.Succeeded, online.Message);
        Assert.Equal(PaymentStatuses.Paid, (await other.PayAsync(online.Value!))!.Status);
        Assert.Equal(1, await other.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid));
    }

    [Fact]
    public async Task AFTER_CASH_005_Not_accepted_rejected_or_another_customers_quotation_cannot_be_paid_in_cash()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var q = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (breaker.Id, 1))], null, null), None)).Value!.Quotations);

        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.QuotationNotFound, (await ctx.Quotations().SelectCashAsync(Guid.NewGuid(), q.QuotationId, None)).ErrorCode);
        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, q.QuotationId, None)).Succeeded);
        Assert.True((await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Succeeded);
        // Rejecting withdraws the pending cash choice; nothing is collected afterwards.
        Assert.True((await ctx.Quotations().RejectAsync(h.CustomerId, q.QuotationId, "changed my mind", None)).Succeeded);
        var attempt = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.ContractId == null);
        Assert.Equal(PaymentStatuses.Cancelled, attempt.Status);
        Assert.Equal(PaymentErrorCodes.CashNotSelected,
            (await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, attempt.Id, await ReceiptAsync(ctx, h.ManagerId, 150_000m), None)).ErrorCode);
    }

    [Fact]
    public async Task AFTER_CASH_006_Packages_of_a_paid_contract_can_be_paid_in_cash_alone_once()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var care = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var bought = await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, care.Id, null, false, PaymentMethods.Cash), None);
        Assert.True(bought.Succeeded, bought.Message);
        Assert.Equal((PaymentMethods.Cash, (Guid?)null, 2_000_000m), (bought.Value!.Checkout.Method, bought.Value.Checkout.ContractId, bought.Value.Checkout.Amount));
        // A second order of the same kind while the cash choice is pending is refused (also after the online checkout window).
        ctx.Clock.Now = ctx.Clock.Now.AddHours(2);
        Assert.Equal(PaymentErrorCodes.CashSelected,
            (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, care.Id, null, false, PaymentMethods.Cash), None)).ErrorCode);

        var paid = await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, bought.Value.Checkout.PaymentAttemptId, await ReceiptAsync(ctx, h.ManagerId, 2_000_000m), None);
        Assert.True(paid.Succeeded, paid.Message);
        var order = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync();
        Assert.True(order.Status is SubscriptionStatuses.Paid or SubscriptionStatuses.Active, order.Status);
        Assert.Equal(1, await ctx.Db.ServiceSubscriptions.CountAsync());
    }
}
