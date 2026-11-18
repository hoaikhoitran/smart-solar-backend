using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SimplifiedFlowFixture;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// ONSITE-PAY (2026-11-18): the technician opens the payment of the quotation the customer agreed to on site; the payment is verified BEFORE the
/// contract exists and the verified payment creates exactly one contract from the paid snapshot. Money that cannot be applied (wrong amount, quotation
/// changed or closed) never creates a contract: it is flagged and recorded as unapplied money for a Manager. A browser return never marks it paid.
/// </summary>
public sealed class OnSiteQuotationPaymentTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(Cart Cart, QuotationView Agreed)> AgreedCartAsync(InstallationWorkflowTestContext ctx)
    {
        var cart = await IssuedAsync(ctx);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        return (cart, await AgreedAsync(ctx, cart, "agreed at home"));
    }

    [Fact]
    public async Task ONSITE_PAY_001_PayOS_QR_paid_by_the_bank_app_creates_one_contract_and_a_duplicate_webhook_changes_nothing()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _) = await AgreedCartAsync(ctx);
        var payments = ctx.OnSitePayments();

        var opened = await payments.StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "ONLINE", None);
        Assert.True(opened.Succeeded, opened.Message);
        var link = opened.Value!.Current!;
        Assert.Equal((QuotationPaymentStates.OnlineInProgress, cart.Quotation.TotalAmount), (opened.Value.PaymentState, link.Amount));
        Assert.False(string.IsNullOrEmpty(link.CheckoutUrl));
        Assert.Equal(PaymentSources.Technician, (await ctx.Db.PaymentEvents.AsNoTracking().SingleAsync(e => e.EventType == "CREATED")).Source);
        // The same tap again returns the same link; no contract exists before the money is verified.
        Assert.Equal(link.PaymentAttemptId, (await payments.StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "ONLINE", None)).Value!.Current!.PaymentAttemptId);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());

        var webhook = ctx.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-ONSITE-1");
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.PaymentsWithCash().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.PaymentsWithCash().HandleWebhookAsync(webhook, None)).Outcome);

        var paid = (await payments.GetAsync(cart.TechnicianId, cart.Quotation.QuotationId, None)).Value!;
        Assert.Equal(QuotationPaymentStates.Paid, paid.PaymentState);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        Assert.Equal((paid.ContractId, ContractStatuses.Draft, cart.Quotation.TotalAmount), ((Guid?)contract.Id, contract.Status, contract.TotalAmount));
        Assert.Equal(1, await ctx.Db.PaymentAttemptItems.CountAsync(i => i.Status == PaymentItemStatuses.Settled && i.InstallationQuotationId == cart.Quotation.QuotationId));
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None)).ErrorCode);
    }

    [Fact]
    public async Task ONSITE_PAY_002_Cash_needs_a_manager_confirmation_and_the_technician_cannot_mark_it_paid()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _) = await AgreedCartAsync(ctx);
        var opened = (await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None)).Value!;
        Assert.Equal(QuotationPaymentStates.AwaitingCashConfirmation, opened.PaymentState);
        var attemptId = opened.Current!.PaymentAttemptId;

        // A technician confirming cash (evidence of their own) is not a payment: only Manager/Admin call this route (API: 403); here, the evidence
        // the technician uploaded is refused for a staff confirmation by somebody else, and nothing is paid.
        var techEvidence = await ctx.VerifiedFileAsync(cart.TechnicianId, FilePurposes.PaymentEvidence);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        Assert.False((await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(manager.Id, attemptId, new CashConfirmation(opened.Amount, "PT-X", techEvidence, null), None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.PaymentEvidence);
        Assert.Equal(PaymentErrorCodes.CashAmountMismatch,
            (await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(manager.Id, attemptId, new CashConfirmation(opened.Amount - 1_000m, "PT-1", evidence, null), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());

        Assert.True((await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(manager.Id, attemptId, new CashConfirmation(opened.Amount, "PT-1", evidence, "on site"), None)).Succeeded);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid,
            (await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(manager.Id, attemptId, new CashConfirmation(opened.Amount, "PT-2", evidence, null), None)).ErrorCode);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        var attempt = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal((PaymentStatuses.Paid, (Guid?)manager.Id, (Guid?)contract.Id), (attempt.Status, attempt.ConfirmedByUserId, attempt.ContractId));
    }

    [Fact]
    public async Task ONSITE_PAY_003_Only_an_agreed_unexpired_quotation_of_the_technicians_survey_can_be_paid()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);
        var payments = ctx.OnSitePayments();

        Assert.Equal(QuotationErrorCodes.NotAgreed, (await payments.StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None)).ErrorCode);
        await AgreedAsync(ctx, cart);
        Assert.Equal("VALIDATION_FAILED", (await payments.StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CHEQUE", None)).ErrorCode);
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);
        Assert.Equal(QuotationErrorCodes.NotFound, (await payments.StartAsync(stranger.Id, cart.Quotation.QuotationId, "CASH", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await payments.StartAsync(cart.CustomerId, cart.Quotation.QuotationId, "CASH", None)).ErrorCode);
        ctx.Clock.Now = cart.Quotation.ValidUntil!.Value.AddMinutes(1).ToUniversalTime();
        Assert.Equal(QuotationErrorCodes.Expired, (await payments.StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync());
    }

    [Fact]
    public async Task ONSITE_PAY_004_A_wrong_amount_never_creates_a_contract_and_is_recorded_as_unapplied_money()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _) = await AgreedCartAsync(ctx);
        var link = (await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "ONLINE", None)).Value!.Current!;
        // Production settlement wiring with the after-sales ledger.
        var data = new SmartSolar.Infrastructure.Persistence.AfterSales.AfterSalesUnitOfWork(ctx.Db);
        var settlement = new InstallationQuotationPaymentSettlement(ctx.Contracts(),
            new AfterSalesPaymentSettlement(data, new SubscriptionTerms(data, ctx.Scheduling.Policies, ctx.Scheduling.Calendar)));
        var payments = new PaymentService(new SmartSolar.Infrastructure.Persistence.InstallationWorkflow.PaymentUnitOfWork(ctx.Db), ctx.PayOs.Gateway(), ctx.PaymentOptions,
            ctx.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentService>.Instance, settlement);

        await payments.HandleWebhookAsync(ctx.PayOs.Webhook(link.OrderCode, (long)link.Amount - 1_000, "FT-WRONG"), None);
        var attempt = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentStatuses.ReviewRequired, attempt.Status);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());
        Assert.Equal(1, await ctx.Db.RefundRecords.CountAsync(r => r.PaymentAttemptId == attempt.Id && r.Status == RefundStatuses.PendingApproval));
        // The quotation stays agreed: a correct payment can still be opened.
        Assert.True((await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", None)).Succeeded);
    }

    [Fact]
    public async Task ONSITE_PAY_005_Money_for_a_quotation_that_can_no_longer_be_contracted_is_flagged_never_applied()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _) = await AgreedCartAsync(ctx);
        var link = (await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "ONLINE", None)).Value!.Current!;
        // The quotation was cancelled behind the payment's back (e.g. data repaired by staff): the late money must not create a contract.
        await ctx.Db.Quotations.Where(q => q.Id == cart.Quotation.QuotationId).ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, SmartSolar.Modules.Quotations.QuotationStatuses.Cancelled)
            .SetProperty(q => q.ClosedAt, ctx.Clock.GetUtcNow()).SetProperty(q => q.ClosedByUserId, cart.TechnicianId).SetProperty(q => q.CloseReason, "test"));

        await ctx.PaymentsWithCash().HandleWebhookAsync(ctx.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-LATE"), None);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());
        var item = await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentItemStatuses.ReviewRequired, item.Status);
        Assert.Contains("CANCELLED", item.Note);
    }

    [Fact]
    public async Task ONSITE_PAY_006_A_request_with_an_open_payment_cannot_be_withdrawn_or_closed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (cart, _) = await AgreedCartAsync(ctx);
        Assert.True((await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "ONLINE", None)).Succeeded);

        Assert.Equal(SalesOutcomeErrorCodes.PaymentInProgress,
            (await ctx.Quotations().WithdrawRequestAsync(cart.CustomerId, cart.RequestId, "changed my mind", None)).ErrorCode);
        Assert.Equal(SalesOutcomeErrorCodes.PaymentInProgress,
            (await ctx.Quotations().CloseRequestAsync(cart.Survey.Sales.Id, cart.RequestId, "CUSTOMER_DECLINED", null, None)).ErrorCode);

        // Cancelling the open payment releases it.
        var cancelled = await ctx.OnSitePayments().CancelAsync(cart.TechnicianId, cart.Quotation.QuotationId, None);
        Assert.True(cancelled.Succeeded, cancelled.Message);
        Assert.Equal(QuotationPaymentStates.AwaitingPayment, cancelled.Value!.PaymentState);
        Assert.True((await ctx.Quotations().WithdrawRequestAsync(cart.CustomerId, cart.RequestId, "changed my mind", None)).Succeeded);
    }

    [Fact]
    public async Task ONSITE_PAY_007_The_contract_born_paid_is_never_charged_again_and_is_PAID_once_signed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await PrepaidAsync(ctx);
        Assert.Equal(ContractStatuses.Paid, signed.Contract.Status);
        Assert.True(signed.Contract.PaymentCompleted);
        var payments = ctx.PaymentsWithCash();
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.CreateOrGetAttemptAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync());
        var events = await ctx.Db.ContractEvents.AsNoTracking().Where(e => e.ContractId == signed.Contract.ContractId).Select(e => e.ToStatus).ToListAsync();
        Assert.Contains(ContractStatuses.Signed, events);
        Assert.Contains(ContractStatuses.Paid, events);
    }

    [Fact]
    public async Task ONSITE_PAY_008_A_legacy_contract_signed_before_the_change_is_still_paid_by_the_customer_once()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var legacy = await SignedAsync(ctx);   // accepted in the app and signed under the previous flow, unpaid
        Assert.False(legacy.Contract.PaymentCompleted);
        var payments = ctx.PaymentsWithCash();
        Assert.True((await payments.SelectCashAsync(legacy.Cart.CustomerId, legacy.Contract.ContractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(legacy.Manager.Id, FilePurposes.PaymentEvidence);
        Assert.True((await payments.ConfirmCashAsync(legacy.Manager.Id, legacy.Contract.ContractId,
            new CashConfirmation(legacy.Contract.TotalAmount, "PT-LEGACY", evidence, null), None)).Succeeded);
        var view = (await ctx.Contracts().GetForStaffAsync(legacy.Manager.Id, true, legacy.Contract.ContractId, None)).Value!;
        Assert.Equal((ContractStatuses.Paid, true), (view.Status, view.PaymentCompleted));
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.SelectCashAsync(legacy.Cart.CustomerId, legacy.Contract.ContractId, None)).ErrorCode);
    }
}
