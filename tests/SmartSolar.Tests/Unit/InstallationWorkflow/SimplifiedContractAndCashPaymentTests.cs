using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SimplifiedFlowFixture;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// 2026-11-17 simplified flow, contract and payment part: the contract DRAFT is created exactly once when the customer accepts; Manager/Admin
/// prepare and confirm the signature; payment opens only on SIGNED, ONLINE (PayOS) or CASH, never both; only a Manager/Admin confirms cash,
/// exactly once and for exactly the contract total; late online money is a refund request, never a second settlement; the installation
/// project waits for PAID.
/// </summary>
public sealed class SimplifiedContractAndCashPaymentTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ------------------------------------------------------------------ contract draft on acceptance

    [Fact]
    public async Task Accepting_creates_exactly_one_contract_draft_from_the_accepted_snapshot()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);

        var draft = await AcceptAsync(ctx, cart);
        Assert.Equal(ContractStatuses.Draft, draft.Status);
        Assert.Equal(cart.Quotation.TotalAmount, draft.TotalAmount);
        Assert.Equal(cart.Quotation.Version, draft.QuotationVersion);
        Assert.Equal(cart.Quotation.Lines.Count, draft.PricingSnapshot.GetProperty("lines").GetArrayLength());
        Assert.Equal(120, draft.WarrantyMonths);
        Assert.Contains(draft.Events!, e => e.EventType == "AUTO_DRAFTED" && e.ActorUserId == cart.CustomerId);

        // Repeating the acceptance, or a manual creation, never makes a second contract.
        Assert.True((await ctx.QuotationsWithContracts().AcceptAsync(cart.CustomerId, cart.Quotation.QuotationId, None)).Succeeded);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        Assert.Equal(ContractErrorCodes.AlreadyExists, (await ctx.Contracts().CreateAsync(manager.Id, cart.Quotation.QuotationId, null, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.CustomerContracts.CountAsync());
        // The customer does not see the draft; Manager/Admin prepare it.
        Assert.Empty(await ctx.Contracts().ListForCustomerAsync(cart.CustomerId, None));
    }

    [Fact]
    public async Task A_quotation_accepted_before_the_change_gets_its_contract_through_the_manager()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);
        // Legacy wiring (no contract hook): an ACCEPTED quotation without a contract, as data accepted before 2026-11-17.
        Assert.True((await ctx.Quotations().AcceptAsync(cart.CustomerId, cart.Quotation.QuotationId, None)).Succeeded);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");

        var created = await ctx.Contracts().CreateAsync(manager.Id, cart.Quotation.QuotationId, "Terms", None);
        Assert.True(created.Succeeded, created.Message);
        Assert.Null((await ctx.Db.CustomerContracts.SingleAsync()).SalesUserId);
    }

    [Fact]
    public async Task Manager_prepares_publishes_and_confirms_the_signature_without_any_sales_owner()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx);
        var stored = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        Assert.Null(stored.SalesUserId);
        Assert.Equal(signed.Manager.Id, stored.ReviewedByUserId);
        Assert.Equal(signed.Manager.Id, stored.OfficialDocumentConfirmedByUserId);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Contracts().GetForCustomerAsync(signed.Cart.CustomerId, stored.Id, None)).Value!.Status);
    }

    // ------------------------------------------------------------------ cash

    [Fact]
    public async Task Choosing_cash_is_not_a_payment_and_needs_a_signed_contract_of_the_customer()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);
        var draft = await AcceptAsync(ctx, cart);
        var payments = ctx.PaymentsWithCash();
        Assert.Equal(PaymentErrorCodes.ContractNotSigned, (await payments.SelectCashAsync(cart.CustomerId, draft.ContractId, None)).ErrorCode);

        await using var ctx2 = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx2);
        var p2 = ctx2.PaymentsWithCash();
        var stranger = await ctx2.Scheduling.Inner.SeedUserAsync("Other customer");
        Assert.Equal(PaymentErrorCodes.ContractNotFound, (await p2.SelectCashAsync(stranger.Id, signed.Contract.ContractId, None)).ErrorCode);

        var chosen = await p2.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None);
        Assert.True(chosen.Succeeded, chosen.Message);
        Assert.Equal(PaymentProviders.Cash, chosen.Value!.Method);
        Assert.Equal(PaymentStatuses.Pending, chosen.Value.Status);
        Assert.Null(chosen.Value.ExpiresAt);
        Assert.Null(chosen.Value.CheckoutUrl);
        // Idempotent; the contract stays SIGNED and its payment state says what is awaited.
        Assert.Equal(chosen.Value.PaymentAttemptId, (await p2.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Value!.PaymentAttemptId);
        var view = (await p2.GetForCustomerAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Value!;
        Assert.Equal(ContractStatuses.Signed, view.ContractStatus);
        Assert.Equal(ContractPaymentStates.AwaitingCashConfirmation, view.PaymentState);
        Assert.Equal(0, ctx2.PayOs.CreateCalls);
        // Online payment is refused while cash is chosen.
        Assert.Equal(PaymentErrorCodes.CashSelected, (await p2.CreateOrGetAttemptAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).ErrorCode);
        Assert.Equal(0, ctx2.PayOs.CreateCalls);
    }

    [Fact]
    public async Task Only_staff_confirmation_with_the_exact_amount_receipt_and_evidence_marks_the_contract_paid_once()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        var total = signed.Contract.TotalAmount;
        var payments = ctx.PaymentsWithCash();
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);

        // Nothing chosen yet.
        Assert.Equal(PaymentErrorCodes.CashNotSelected, (await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(total, "PT-0001", evidence, null), None)).ErrorCode);
        Assert.True((await payments.SelectCashAsync(signed.Cart.CustomerId, contractId, None)).Succeeded);

        // Validation.
        var invalid = await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(0m, " ", Guid.Empty, new string('x', 501)), None);
        Assert.Equal(CommonErrorCodes.ValidationFailed, invalid.ErrorCode);
        Assert.Equal(400, OperationResultControllerBase.StatusFor(invalid.ErrorCode!));
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(total + 0.5m, "PT-0001", evidence, null), None)).ErrorCode);
        // Wrong amounts are never recorded (no partial or over-payment).
        foreach (var amount in new[] { total - 1m, total + 1_000m })
        {
            var wrong = await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(amount, "PT-0001", evidence, null), None);
            Assert.Equal(PaymentErrorCodes.CashAmountMismatch, wrong.ErrorCode);
            Assert.Equal(409, OperationResultControllerBase.StatusFor(wrong.ErrorCode!));
        }
        // Evidence must be a verified PAYMENT_EVIDENCE file of the confirming staff member.
        var foreign = await ctx.VerifiedFileAsync(signed.Cart.CustomerId, FilePurposes.PaymentEvidence);
        Assert.False((await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(total, "PT-0001", foreign, null), None)).Succeeded);
        var wrongPurpose = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.ContractDocument, "pdf");
        Assert.False((await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(total, "PT-0001", wrongPurpose, null), None)).Succeeded);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync()).Status);

        var confirmed = await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(total, " PT-0001 ", evidence, "Paid at the office"), None);
        Assert.True(confirmed.Succeeded, confirmed.Message);
        Assert.Equal(PaymentStatuses.Paid, confirmed.Value!.Status);
        Assert.Equal("PT-0001", confirmed.Value.ProviderReference);
        Assert.Equal(signed.Manager.Id, confirmed.Value.ConfirmedByUserId);
        Assert.Equal(evidence, confirmed.Value.EvidenceFileId);
        Assert.Equal(total, confirmed.Value.AmountPaid);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractStatuses.Paid, contract.Status);
        Assert.NotNull(contract.PaidAt);
        Assert.Contains(await ctx.Db.ContractEvents.AsNoTracking().Where(e => e.ContractId == contractId).ToListAsync(),
            e => e.ToStatus == ContractStatuses.Paid && e.ActorUserId == signed.Manager.Id && e.Detail!.Contains("PT-0001"));

        // A second confirmation is refused; nothing is settled twice.
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(total, "PT-0002", evidence, null), None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        var staffView = (await payments.GetForStaffAsync(contractId, None)).Value!;
        Assert.Equal(ContractPaymentStates.Paid, staffView.PaymentState);
        Assert.Contains(staffView.History!, e => e.EventType == "CASH_CONFIRMED" && e.Source == PaymentSources.Staff);
        // The customer view never shows who confirmed or the evidence file.
        var customerView = (await payments.GetForCustomerAsync(signed.Cart.CustomerId, contractId, None)).Value!;
        Assert.Null(customerView.Current!.ConfirmedByUserId);
        Assert.Null(customerView.Current.EvidenceFileId);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.SelectCashAsync(signed.Cart.CustomerId, contractId, None)).ErrorCode);
    }

    [Fact]
    public async Task A_receipt_reference_settles_only_one_contract()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var start = ctx.Clock.Now;
        var first = await SignedAsync(ctx, "First customer");
        ctx.Clock.Now = start;   // the second booking is made before the first survey day, like the first one
        var second = await SignedAsync(ctx, "Second customer");
        var payments = ctx.PaymentsWithCash();
        var evidence = await ctx.VerifiedFileAsync(first.Manager.Id, FilePurposes.PaymentEvidence);
        foreach (var s in new[] { first, second }) Assert.True((await payments.SelectCashAsync(s.Cart.CustomerId, s.Contract.ContractId, None)).Succeeded);

        Assert.True((await payments.ConfirmCashAsync(first.Manager.Id, first.Contract.ContractId, new(first.Contract.TotalAmount, "PT-7", evidence, null), None)).Succeeded);
        var reused = await payments.ConfirmCashAsync(first.Manager.Id, second.Contract.ContractId, new(second.Contract.TotalAmount, "PT-7", evidence, null), None);
        Assert.Equal(PaymentErrorCodes.CashReceiptDuplicate, reused.ErrorCode);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == second.Contract.ContractId)).Status);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.ContractId == second.Contract.ContractId)).Status);
    }

    [Fact]
    public async Task An_open_online_link_is_checked_with_payos_before_cash_and_the_customer_can_switch_methods()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx);
        var customer = signed.Cart.CustomerId;
        var contractId = signed.Contract.ContractId;
        var payments = ctx.PaymentsWithCash();

        var online = (await payments.CreateOrGetAttemptAsync(customer, contractId, None)).Value!;
        Assert.Equal(PaymentErrorCodes.CheckoutInProgress, (await payments.SelectCashAsync(customer, contractId, None)).ErrorCode);
        Assert.Equal(ContractPaymentStates.OnlineInProgress, (await payments.GetForCustomerAsync(customer, contractId, None)).Value!.PaymentState);

        // Cancel the link (checked with PayOS), choose cash, then withdraw the cash choice and pay online after all.
        Assert.True((await payments.CancelCurrentAsync(customer, contractId, None)).Succeeded);
        var cash = (await payments.SelectCashAsync(customer, contractId, None)).Value!;
        var withdrawn = (await payments.CancelCurrentAsync(customer, contractId, None)).Value!;
        Assert.Equal(cash.PaymentAttemptId, withdrawn.PaymentAttemptId);
        Assert.Equal(PaymentStatuses.Cancelled, withdrawn.Status);
        Assert.Equal(PaymentErrorCodes.CashNotSelected,
            (await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(signed.Contract.TotalAmount, "PT-1", await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence), null), None)).ErrorCode);
        var again = (await payments.CreateOrGetAttemptAsync(customer, contractId, None)).Value!;
        Assert.NotEqual(online.PaymentAttemptId, again.PaymentAttemptId);
        Assert.Equal("ONLINE", again.Method);
    }

    [Fact]
    public async Task Money_already_received_online_wins_over_a_cash_choice()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx);
        var payments = ctx.PaymentsWithCash();
        var online = (await payments.CreateOrGetAttemptAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Value!;
        // The customer paid at PayOS, but the webhook has not arrived yet.
        ctx.PayOs.Links[online.OrderCode] = ("PAID", (long)online.Amount, "FT-1");

        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await payments.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).ErrorCode);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.Provider == PaymentProviders.Cash));
    }

    [Fact]
    public async Task Late_and_duplicate_online_payments_after_cash_go_to_a_manual_refund_and_never_settle_twice()
    {
        await using var after = AfterSalesTestContext.Create();
        var ctx = after.Installation;
        var signed = await SignedAsync(ctx);
        var customer = signed.Cart.CustomerId;
        var contractId = signed.Contract.ContractId;
        var payments = after.Payments();   // production wiring: unapplied money opens a refund record

        // An online link is opened, then cancelled locally (PayOS reports it cancelled); the customer pays cash and it is confirmed.
        var online = (await payments.CreateOrGetAttemptAsync(customer, contractId, None)).Value!;
        Assert.True((await payments.CancelCurrentAsync(customer, contractId, None)).Succeeded);
        Assert.True((await payments.SelectCashAsync(customer, contractId, None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);
        Assert.True((await payments.ConfirmCashAsync(signed.Manager.Id, contractId, new(signed.Contract.TotalAmount, "PT-9", evidence, null), None)).Succeeded);

        // The bank transfer of the old link still arrives: signed webhook, delivered twice.
        var body = ctx.PayOs.Webhook(online.OrderCode, (long)online.Amount, "FT-LATE");
        Assert.Equal(WebhookOutcomes.Applied, (await payments.HandleWebhookAsync(body, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await payments.HandleWebhookAsync(body, None)).Outcome);

        var late = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == online.PaymentAttemptId);
        Assert.Equal(PaymentStatuses.RefundRequired, late.Status);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == contractId && a.Status == PaymentStatuses.Paid));
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync()).Status);
        // A refund request for a Manager is opened (no automatic refund), exactly once.
        var refund = Assert.Single(await ctx.Db.RefundRecords.AsNoTracking().Where(r => r.PaymentAttemptId == online.PaymentAttemptId).ToListAsync());
        Assert.Equal(RefundStatuses.PendingApproval, refund.Status);
    }

    [Fact]
    public async Task Reconciliation_never_asks_the_gateway_about_cash()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx);
        var payments = ctx.PaymentsWithCash();
        var cash = (await payments.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Value!;
        var before = ctx.PayOs.Requests.Count;

        var due = await new SmartSolar.Infrastructure.Persistence.InstallationWorkflow.PaymentUnitOfWork(ctx.Db)
            .ListAttemptsToReconcileAsync(ctx.Clock.GetUtcNow().AddYears(2), 50, None);
        Assert.DoesNotContain(cash.PaymentAttemptId, due);
        var reconciled = await payments.ReconcileAsync(cash.PaymentAttemptId, force: true, None);
        Assert.Equal(PaymentStatuses.Pending, reconciled!.Status);
        Assert.Equal(before, ctx.PayOs.Requests.Count);
    }

    // ------------------------------------------------------------------ installation

    [Fact]
    public async Task The_installation_project_is_created_only_after_the_contract_is_paid_in_cash()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var signed = await SignedAsync(ctx);
        var payments = ctx.PaymentsWithCash();
        var installations = ctx.Installations();

        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await installations.CreateProjectAsync(signed.Manager.Id, signed.Contract.ContractId, None)).ErrorCode);
        Assert.True((await payments.SelectCashAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Succeeded);
        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await installations.CreateProjectAsync(signed.Manager.Id, signed.Contract.ContractId, None)).ErrorCode);
        Assert.Empty(await installations.ListReadyContractsAsync(None));

        var evidence = await ctx.VerifiedFileAsync(signed.Manager.Id, FilePurposes.PaymentEvidence);
        Assert.True((await payments.ConfirmCashAsync(signed.Manager.Id, signed.Contract.ContractId, new(signed.Contract.TotalAmount, "PT-42", evidence, null), None)).Succeeded);
        Assert.Single(await installations.ListReadyContractsAsync(None));
        var project = await installations.CreateProjectAsync(signed.Manager.Id, signed.Contract.ContractId, None);
        Assert.True(project.Succeeded, project.Message);
        Assert.False((await installations.CreateProjectAsync(signed.Manager.Id, signed.Contract.ContractId, None)).Succeeded);
    }
}
