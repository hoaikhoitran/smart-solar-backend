using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>30/70 plan on the production handlers (SQLite): agreement contract, deposit, paper signature, readiness, customer acceptance, balance.</summary>
public sealed class PaymentPlanWorkflowTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ------------------------------------------------------------------ agreement → contract

    [Fact]
    public async Task AGREE_creates_exactly_one_previewable_contract_with_a_due_deposit_and_a_pending_balance()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        Assert.Equal((flow.ContractId, ContractStatuses.Draft, PaymentPlans.Deposit30Balance70), (contract.Id, contract.Status, contract.PaymentPlan));
        Assert.Equal(flow.Cart.Quotation.TotalAmount, contract.TotalAmount);
        Assert.NotNull(contract.CanonicalHash);
        Assert.Contains("BẢO LƯU QUYỀN SỞ HỮU", contract.Terms);
        Assert.Equal(new PaymentPlanOptions().TermsVersion, contract.TermsVersion);
        Assert.NotNull(contract.WarrantyMonths);

        var installments = await ctx.Db.ContractInstallments.AsNoTracking().OrderBy(i => i.Sequence).ToListAsync();
        Assert.Equal(2, installments.Count);
        var split = PaymentPlanCalculator.Split(contract.TotalAmount)!;
        Assert.Equal((InstallmentKinds.Deposit, InstallmentStatuses.Due, split.Deposit), (installments[0].Kind, installments[0].Status, installments[0].Amount));
        Assert.Equal((InstallmentKinds.Balance, InstallmentStatuses.Pending, split.Balance, (DateTimeOffset?)null),
            (installments[1].Kind, installments[1].Status, installments[1].Amount, installments[1].DueAt));

        // The customer previews the full contract before paying (DRAFT is visible for 30/70 contracts).
        var preview = await ctx.Contracts().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None);
        Assert.True(preview.Succeeded, preview.Message);
        Assert.Equal((PaymentPlanStages.DraftCreated, OwnershipStatuses.Retained, false), (preview.Value!.PaymentPlan!.Stage, preview.Value.PaymentPlan.OwnershipStatus, preview.Value.PaymentCompleted));
        Assert.Single(await ctx.Contracts().ListForCustomerAsync(flow.CustomerId, None));
        Assert.Contains(CustomerNotificationTypesSeen(ctx, flow.CustomerId), t => t == SmartSolar.Modules.Common.Notifications.CustomerNotificationTypes.ContractReadyForPreview);
        // The decision event is the technician's, never the customer's.
        Assert.All(await ctx.Db.ContractEvents.AsNoTracking().Where(e => e.ContractId == flow.ContractId).ToListAsync(), e => Assert.Equal(flow.TechnicianId, e.ActorUserId));
    }

    [Fact]
    public async Task AGREE_retried_with_the_old_revision_returns_the_same_contract_and_never_a_second_one()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        for (var i = 0; i < 3; i++)
        {
            var again = await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(flow.TechnicianId, flow.Cart.Quotation.QuotationId,
                new OnSiteDecisionInput(OnSiteDecisions.Agreed, flow.Cart.Quotation.Revision, null), None);
            Assert.True(again.Succeeded, again.Message);
            Assert.Equal(flow.ContractId, again.Value!.ContractId);
        }
        Assert.Equal(1, await ctx.Db.CustomerContracts.CountAsync());
        Assert.Equal(2, await ctx.Db.ContractInstallments.CountAsync());
    }

    [Fact]
    public async Task CUTOVER_a_quotation_agreed_before_the_release_and_never_paid_gets_its_30_70_contract_when_the_agreement_is_reconfirmed()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var legacyAgreed = await SimplifiedFlowFixture.AgreedAsync(ctx, cart);   // previous release: no contract at the agreement
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());

        var reconfirmed = await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(cart.TechnicianId, legacyAgreed.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, legacyAgreed.Revision, "re-confirmed after the 30/70 release"), None);
        Assert.True(reconfirmed.Succeeded, reconfirmed.Message);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        Assert.Equal((reconfirmed.Value!.ContractId!.Value, PaymentPlans.Deposit30Balance70), (contract.Id, contract.PaymentPlan));

        // A quotation with a legacy payment in progress is never converted (its payment keeps the previous rules).
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        var other = await SimplifiedFlowFixture.IssuedAsync(ctx, "Legacy payer");
        var agreedOther = await SimplifiedFlowFixture.AgreedAsync(ctx, other);
        Assert.True((await ctx.OnSitePayments().StartAsync(other.TechnicianId, agreedOther.QuotationId, "CASH", None)).Succeeded);
        var revision = (await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == agreedOther.QuotationId)).Revision;
        Assert.Equal(QuotationErrorCodes.AlreadyAccepted, (await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(other.TechnicianId, agreedOther.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, revision, null), None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.CustomerContracts.CountAsync());
    }

    [Fact]
    public async Task AGREE_without_a_default_warranty_records_nothing()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await SimplifiedFlowFixture.IssuedAsync(ctx);
        var agreed = await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(cart.TechnicianId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, cart.Quotation.Revision, null), None);
        Assert.Equal(ContractErrorCodes.WarrantyRequired, agreed.ErrorCode);
        Assert.Equal(QuotationStatuses.Issued, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == cart.Quotation.QuotationId)).Status);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());
    }

    [Fact]
    public async Task WITHDRAW_before_the_deposit_discards_the_contract_but_an_open_deposit_payment_blocks_it()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var quotation = await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == flow.Cart.Quotation.QuotationId);

        var cash = await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "CASH", None);
        Assert.True(cash.Succeeded, cash.Message);
        var refused = await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(flow.TechnicianId, quotation.Id,
            new OnSiteDecisionInput(OnSiteDecisions.Declined, quotation.Revision, "changed mind"), None);
        Assert.Equal(QuotationErrorCodes.PaymentStarted, refused.ErrorCode);

        Assert.True((await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Succeeded);
        var withdrawn = await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(flow.TechnicianId, quotation.Id,
            new OnSiteDecisionInput(OnSiteDecisions.Declined, quotation.Revision, "changed mind"), None);
        Assert.True(withdrawn.Succeeded, withdrawn.Message);
        var contract = await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync();
        Assert.Equal(ContractStatuses.Discarded, contract.Status);
        Assert.Contains("changed mind", contract.DiscardReason);
        Assert.All(await ctx.Db.ContractInstallments.AsNoTracking().ToListAsync(), i => Assert.Equal(InstallmentStatuses.Cancelled, i.Status));
        Assert.Equal(ContractErrorCodes.NotFound, (await ctx.Contracts().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).ErrorCode);
        Assert.Equal(PaymentPlanErrorCodes.InstallmentNotPayable,
            (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "CASH", None)).ErrorCode);
    }

    // ------------------------------------------------------------------ deposit

    [Fact]
    public async Task DEPOSIT_cash_confirmed_by_a_Manager_makes_the_contract_signable_but_never_paid()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var paid = await PayCashAsync(ctx, flow, 1);
        Assert.Equal(PaymentStatuses.Paid, paid.Status);

        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal(ContractStatuses.AwaitingSignature, view.Status);
        Assert.False(view.PaymentCompleted);                                   // 30% ≠ PAID
        Assert.Equal((PaymentPlanStages.DepositVerified, true, false, false), (view.PaymentPlan!.Stage, view.PaymentPlan.DepositPaid, view.PaymentPlan.FullyPaid,
            view.PaymentPlan.ReadyForInstallation));
        Assert.Equal(view.PaymentPlan.Total - view.PaymentPlan.Installments[0].Amount, view.PaymentPlan.Outstanding);
        var deposit = await InstallmentAsync(ctx, flow.ContractId, 1);
        Assert.Equal((InstallmentStatuses.Paid, paid.PaymentAttemptId, deposit.Amount), (deposit.Status, deposit.SettledPaymentAttemptId!.Value, deposit.PaidAmount!.Value));
        Assert.Equal(PaymentItemStatuses.Settled, (await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync()).Status);
        // The cash attempt names no contract (the line names the installment): the legacy "contract paid" logic never sees it.
        Assert.Null((await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync()).ContractId);
    }

    [Fact]
    public async Task DEPOSIT_balance_cannot_be_paid_before_acceptance_and_legacy_full_payment_is_refused()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        foreach (var payer in new[] { InstallmentPayer.Technician, InstallmentPayer.Customer })
            Assert.Equal(PaymentPlanErrorCodes.InstallmentNotPayable,
                (await ctx.InstallmentPayments().StartAsync(payer, payer == InstallmentPayer.Technician ? flow.TechnicianId : flow.CustomerId, flow.ContractId, 2, "ONLINE", None)).ErrorCode);
        await PayCashAsync(ctx, flow, 1);
        Assert.Equal(PaymentPlanErrorCodes.InstallmentPaid,
            (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 1, "CASH", None)).ErrorCode);
        Assert.Equal(PaymentPlanErrorCodes.InstallmentNotFound,
            (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 3, None)).ErrorCode);

        // The 100% contract payment of the previous flow never applies to a 30/70 contract.
        var payments = ctx.PaymentsWithCash();
        Assert.Equal(PaymentErrorCodes.ContractHasInstallments, (await payments.CreateOrGetAttemptAsync(flow.CustomerId, flow.ContractId, None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.ContractHasInstallments, (await payments.SelectCashAsync(flow.CustomerId, flow.ContractId, None)).ErrorCode);
        Assert.Equal(ContractPaymentStates.NotPayable, (await payments.GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.PaymentState);
    }

    [Fact]
    public async Task DEPOSIT_online_webhook_settles_once_duplicate_delivery_and_wrong_amount_are_never_applied()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var opened = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None)).Value!;
        Assert.Equal(InstallmentPaymentStates.OnlineInProgress, opened.PaymentState);
        Assert.NotNull(opened.Current!.QrCode ?? opened.Current.CheckoutUrl);
        // Same method again → the same link.
        Assert.Equal(opened.Current.PaymentAttemptId,
            (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None)).Value!.Current!.PaymentAttemptId);
        Assert.Equal(PaymentErrorCodes.CheckoutInProgress,
            (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "CASH", None)).ErrorCode);

        // A wrong amount is never applied: REVIEW_REQUIRED, the installment stays DUE.
        var payments = ctx.PaymentsWithSchedule();
        await payments.HandleWebhookAsync(ctx.PayOs.Webhook(opened.Current.OrderCode, (long)opened.Amount - 1, "FT-WRONG"), None);
        Assert.Equal(PaymentStatuses.ReviewRequired, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(InstallmentStatuses.Due, (await InstallmentAsync(ctx, flow.ContractId, 1)).Status);
        Assert.Equal(ContractStatuses.Draft, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync()).Status);

        // A new QR, paid exactly; the same delivery twice is a duplicate.
        var second = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None)).Value!.Current!;
        var body = ctx.PayOs.Webhook(second.OrderCode, (long)second.Amount, "FT-OK");
        Assert.Equal(WebhookOutcomes.Applied, (await payments.HandleWebhookAsync(body, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await payments.HandleWebhookAsync(body, None)).Outcome);
        Assert.Equal(InstallmentStatuses.Paid, (await InstallmentAsync(ctx, flow.ContractId, 1)).Status);
        Assert.Equal(1, await ctx.Db.PaymentAttemptItems.CountAsync(i => i.Status == PaymentItemStatuses.Settled));
        Assert.Equal(ContractStatuses.AwaitingSignature, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task DEPOSIT_paid_late_on_a_link_cancelled_after_the_contract_was_discarded_goes_to_manual_handling()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var qr = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None)).Value!.Current!;
        Assert.True((await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Succeeded);
        var quotation = await ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == flow.Cart.Quotation.QuotationId);
        Assert.True((await ctx.QuotationsWithPaymentPlan().RecordOnSiteDecisionAsync(flow.TechnicianId, quotation.Id,
            new OnSiteDecisionInput(OnSiteDecisions.Declined, quotation.Revision, "customer withdrew"), None)).Succeeded);

        // The customer had scanned the QR before the cancellation: the money arrives anyway. It is recorded, never applied.
        await ctx.PaymentsWithSchedule().ConfirmAsync(qr.PaymentAttemptId, (long)qr.Amount, "FT-LATE", ctx.Clock.GetUtcNow(), PaymentSources.Webhook, None);
        var line = await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync();
        Assert.Equal(PaymentItemStatuses.ReviewRequired, line.Status);
        Assert.Contains(InstallmentStatuses.Cancelled, line.Note);
        Assert.Equal(InstallmentStatuses.Cancelled, (await InstallmentAsync(ctx, flow.ContractId, 1)).Status);
        Assert.Equal(ContractStatuses.Discarded, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task RBAC_installment_payment_is_reachable_only_by_the_assigned_technician_and_the_owning_customer()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var other = await AgreedAsync(ctx, "Other customer");
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", SchedulingTestContext.BinhTan);
        var payments = ctx.InstallmentPayments();
        Assert.Equal(ContractErrorCodes.NotFound, (await payments.StartAsync(InstallmentPayer.Technician, stranger.Id, flow.ContractId, 1, "CASH", None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound, (await payments.StartAsync(InstallmentPayer.Customer, other.CustomerId, flow.ContractId, 1, "ONLINE", None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound, (await payments.GetAsync(InstallmentPayer.Customer, other.CustomerId, flow.ContractId, 1, None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound, (await ctx.Contracts().GetForCustomerAsync(other.CustomerId, flow.ContractId, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await payments.StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 1, "BITCOIN", None)).ErrorCode);
        // Staff read the state (read-all) but never open or cancel.
        Assert.True((await payments.GetAsync(InstallmentPayer.Staff, Guid.NewGuid(), flow.ContractId, 1, None)).Succeeded);
        await Assert.ThrowsAsync<ArgumentException>(() => payments.StartAsync(InstallmentPayer.Staff, Guid.NewGuid(), flow.ContractId, 1, "CASH", None));
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync());
    }

    // ------------------------------------------------------------------ paper signature

    [Fact]
    public async Task PAPER_needs_the_deposit_then_every_page_in_order_and_locks_on_finalize()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var signatures = ctx.PaperSignatures();
        Assert.Equal(PaymentPlanErrorCodes.DepositRequired,
            (await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(2, null, null), None)).ErrorCode);
        await PayCashAsync(ctx, flow, 1);

        Assert.Equal(PaymentPlanErrorCodes.SignatureNotStarted,
            (await signatures.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed,
            (await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(0, null, null), None)).ErrorCode);
        var started = (await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(3, null, "kitchen table"), None)).Value!;
        Assert.Equal((SignatureSetStatuses.Uploading, 3), (started.Status, started.ExpectedPageCount));
        Assert.Equal(new[] { 1, 2, 3 }, started.MissingPages);

        var page1 = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage);
        var page3 = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage, "png");
        Assert.True((await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, page1, None)).Succeeded);
        Assert.True((await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 3, page3, None)).Succeeded);
        Assert.Equal(PaymentPlanErrorCodes.PageOutOfRange,
            (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 4, await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage), None)).ErrorCode);
        Assert.Equal(FileErrorCodes.AlreadyLinked, (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 2, page1, None)).ErrorCode);

        var gap = await signatures.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None);
        Assert.Equal(PaymentPlanErrorCodes.PagesIncomplete, gap.ErrorCode);
        Assert.Contains("\"missingPages\":[2]", System.Text.Json.JsonSerializer.Serialize(gap.Details));

        // A blurred page 3 is replaced before finalizing: the replaced row stays as history.
        var page3Again = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage);
        Assert.True((await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 3, page3Again, None)).Succeeded);
        var page2 = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage, "pdf");
        var complete = (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 2, page2, None)).Value!;
        Assert.Empty(complete.MissingPages);
        Assert.Equal(4, complete.Pages.Count);
        Assert.Single(complete.Pages, p => p.ReplacedAt is not null && p.FileId == page3);

        var finalized = await signatures.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, complete.Revision, None);
        Assert.True(finalized.Succeeded, finalized.Message);
        Assert.Equal(SignatureSetStatuses.Finalized, finalized.Value!.Status);
        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Signed, PaymentPlanStages.ReadyForInstallation, true, true, false),
            (view.Status, view.PaymentPlan!.Stage, view.PaymentPlan.PaperSigned, view.PaymentPlan.ReadyForInstallation, view.PaymentCompleted));
        Assert.Contains(view.Events!, e => e.EventType == "SIGNED_PAGES_FINALIZED" && e.ActorUserId == flow.TechnicianId);

        // Locked: no new page, no restart, no second finalization.
        Assert.Equal(PaymentPlanErrorCodes.SignatureFinalized,
            (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage), None)).ErrorCode);
        Assert.Equal(PaymentPlanErrorCodes.SignatureFinalized,
            (await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(4, null, null), None)).ErrorCode);
        Assert.Equal(PaymentPlanErrorCodes.SignatureFinalized,
            (await signatures.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None)).ErrorCode);
    }

    [Fact]
    public async Task PAPER_only_the_assigned_technician_or_support_uploads_their_own_verified_page_files()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        await PayCashAsync(ctx, flow, 1);
        var signatures = ctx.PaperSignatures();
        var stranger = await ctx.Scheduling.SeedTechnicianAsync("Stranger", SchedulingTestContext.BinhTan);
        Assert.Equal(ContractErrorCodes.NotFound,
            (await signatures.StartAsync(SignatureActor.Technician, stranger.Id, flow.ContractId, new StartSignatureSetInput(1, null, null), None)).ErrorCode);

        var started = await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(2, null, null), None);
        // A different page count restarts the unfinished upload (the first set is kept as SUPERSEDED history).
        var restarted = await signatures.StartAsync(SignatureActor.Support, flow.Manager.Id, flow.ContractId, new StartSignatureSetInput(1, null, "phone died"), None);
        Assert.Equal((1, 2), (started.Value!.Version, restarted.Value!.Version));
        Assert.Contains(await signatures.ListAsync(flow.ContractId, None), s => s.Status == SignatureSetStatuses.Superseded);

        var strangersFile = await ctx.VerifiedFileAsync(stranger.Id, FilePurposes.SignedContractPage);
        Assert.Equal(FileErrorCodes.NotOwned, (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, strangersFile, None)).ErrorCode);
        var wrongPurpose = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.InstallationEvidence);
        Assert.Equal(FileErrorCodes.WrongPurpose, (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, wrongPurpose, None)).ErrorCode);
        var unverified = await ctx.UnverifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage);
        Assert.Equal(FileErrorCodes.NotVerified, (await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, unverified, None)).ErrorCode);

        var managersPage = await ctx.VerifiedFileAsync(flow.Manager.Id, FilePurposes.SignedContractPage);
        Assert.True((await signatures.PutPageAsync(SignatureActor.Support, flow.Manager.Id, flow.ContractId, 1, managersPage, None)).Succeeded);
        Assert.True((await signatures.FinalizeAsync(SignatureActor.Support, flow.Manager.Id, flow.ContractId, null, None)).Succeeded);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
    }

    [Fact]
    public async Task PAPER_finalize_refuses_a_page_whose_stored_object_changed_since_verification()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        await PayCashAsync(ctx, flow, 1);
        var signatures = ctx.PaperSignatures();
        await signatures.StartAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, new StartSignatureSetInput(1, null, null), None);
        var page = await ctx.VerifiedFileAsync(flow.TechnicianId, FilePurposes.SignedContractPage);
        Assert.True((await signatures.PutPageAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, 1, page, None)).Succeeded);
        var stored = await ctx.Db.StoredFiles.AsNoTracking().SingleAsync(f => f.Id == page);
        ctx.Storage.Delete(stored.Bucket, stored.ObjectKey);   // removed from storage after verification

        Assert.False((await signatures.FinalizeAsync(SignatureActor.Technician, flow.TechnicianId, flow.ContractId, null, None)).Succeeded);
        Assert.Equal(ContractStatuses.AwaitingSignature, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
    }

    // ------------------------------------------------------------------ installation readiness

    [Fact]
    public async Task READY_installation_needs_deposit_and_finalized_paper_not_100_percent()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await ctx.Installations().CreateProjectAsync(flow.Manager.Id, flow.ContractId, None)).ErrorCode);
        await PayCashAsync(ctx, flow, 1);
        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await ctx.Installations().CreateProjectAsync(flow.Manager.Id, flow.ContractId, None)).ErrorCode);
        Assert.DoesNotContain(await ctx.Installations().ListReadyContractsAsync(None), c => c.ContractId == flow.ContractId);
        // The technician may agree the date as soon as the deposit is verified; it is confirmed once the paper is signed.
        var planned = await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId, InstallationScheduleTests.Plan(note: "Agreed in person"), None);
        Assert.True(planned.Succeeded, planned.Message);
        Assert.Equal((InstallationScheduleStatuses.Planned, true, false), (planned.Value!.Status, planned.Value.AwaitingContractSignature, planned.Value.AwaitingPayment));

        await SignOnPaperAsync(ctx, flow, 2);
        var schedule = (await ctx.Schedules().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.First();
        Assert.Equal(InstallationScheduleStatuses.Confirmed, schedule.Status);
        Assert.NotNull(schedule.ProjectId);
        Assert.Equal(InstallationStatuses.Scheduled, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Status);
        // Balance still pending: 70% is not required to install.
        Assert.Equal(InstallmentStatuses.Pending, (await InstallmentAsync(ctx, flow.ContractId, 2)).Status);
    }

    // ------------------------------------------------------------------ customer acceptance → balance

    [Fact]
    public async Task ACCEPT_by_the_customer_completes_the_handover_and_opens_the_balance_due_72_hours_later()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        Assert.Contains(CustomerNotificationTypesSeen(ctx, flow.CustomerId), t => t == SmartSolar.Modules.Common.Notifications.CustomerNotificationTypes.InstallationAcceptanceRequested);

        // Nobody else decides for the customer: the technician (or another customer) gets 404.
        var revision = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Revision;
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await ctx.Installations().DecideAcceptanceAsync(flow.TechnicianId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.Accept, null, null, revision), None)).ErrorCode);

        var acceptedAt = ctx.Clock.GetUtcNow();
        var accepted = await AcceptAsync(ctx, flow, projectId);
        Assert.Equal(InstallationStatuses.Completed, accepted.Status);
        var balance = await InstallmentAsync(ctx, flow.ContractId, 2);
        Assert.Equal((InstallmentStatuses.Due, acceptedAt, acceptedAt.AddHours(72)), (balance.Status, balance.OpenedAt!.Value, balance.DueAt!.Value));
        var acceptance = await ctx.Db.InstallationAcceptances.AsNoTracking().SingleAsync();
        Assert.Equal((AcceptanceDecisions.Accepted, flow.CustomerId, AcceptanceDeciders.Customer), (acceptance.Decision, acceptance.DecidedByUserId!.Value, acceptance.DecidedByRole));

        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Signed, PaymentPlanStages.BalanceDue, false), (view.Status, view.PaymentPlan!.Stage, view.PaymentCompleted));
        // Accepting twice is refused (the round is closed); the deadline is never reset.
        Assert.Equal(InstallationErrorCodes.InvalidState, (await ctx.Installations().DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.Accept, null, null, null), None)).ErrorCode);
        Assert.Equal(acceptedAt.AddHours(72), (await InstallmentAsync(ctx, flow.ContractId, 2)).DueAt);
    }

    [Fact]
    public async Task REWORK_requested_by_the_customer_keeps_each_round_and_opens_nothing_until_acceptance()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        var installs = ctx.Installations();
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.RequestRework, " ", null, null), None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await installs.DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput("MAYBE", null, null, null), None)).ErrorCode);
        var photo = await ctx.VerifiedFileAsync(flow.CustomerId, FilePurposes.AcceptanceEvidence);
        var rework = await installs.DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.Reject, "Inverter display shows an error", [photo], null), None);
        Assert.True(rework.Succeeded, rework.Message);
        Assert.Equal(InstallationStatuses.ReworkRequired, rework.Value!.Status);
        Assert.Equal(InstallmentStatuses.Pending, (await InstallmentAsync(ctx, flow.ContractId, 2)).Status);
        var round1 = await ctx.Db.InstallationAcceptances.AsNoTracking().SingleAsync();
        Assert.Equal((AcceptanceDecisions.ReworkRequested, "Inverter display shows an error"), (round1.Decision, round1.Reason));
        Assert.Contains(photo.ToString(), round1.EvidenceFileIds);

        // Round 2: a new schedule (Manager + technician acceptance), new evidence, new report, accepted.
        await InstallationTests.ReworkScheduledAsync(ctx, projectId, flow.TechnicianId, flow.Manager.Id);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await installs.ReportCompletionAsync(flow.TechnicianId, projectId, "Inverter replaced and tested.", null, None)).Succeeded);
        await AcceptAsync(ctx, flow, projectId);
        var rounds = await ctx.Db.InstallationAcceptances.AsNoTracking().OrderBy(a => a.Round).ToListAsync();
        Assert.Equal(new[] { AcceptanceDecisions.ReworkRequested, AcceptanceDecisions.Accepted }, rounds.Select(r => r.Decision));
        Assert.Equal(InstallmentStatuses.Due, (await InstallmentAsync(ctx, flow.ContractId, 2)).Status);
    }

    [Fact]
    public async Task BALANCE_paid_completes_the_contract_and_ownership_becomes_transfer_eligible()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        await AcceptAsync(ctx, flow, projectId);
        var paid = await PayOnlineAsync(ctx, flow, 2, InstallmentPayer.Customer, "FT-BAL");
        Assert.Equal(PaymentStatuses.Paid, paid.Status);
        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, PaymentPlanStages.FullyPaid, OwnershipStatuses.TransferEligible, true, 0m),
            (view.Status, view.PaymentPlan!.Stage, view.PaymentPlan.OwnershipStatus, view.PaymentCompleted, view.PaymentPlan.Outstanding));
        Assert.False((await InstallmentAsync(ctx, flow.ContractId, 2)).PaidLate);
        Assert.Equal(2, await ctx.Db.PaymentAttemptItems.CountAsync(i => i.Status == PaymentItemStatuses.Settled));
        Assert.Equal(PaymentPlanErrorCodes.InstallmentPaid,
            (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "ONLINE", None)).ErrorCode);
    }

    internal static List<string> CustomerNotificationTypesSeen(InstallationWorkflowTestContext ctx, Guid customerId)
        => ctx.Db.AfterSalesNotifications.AsNoTracking().Where(n => n.RecipientUserId == customerId).Select(n => n.Type).ToList();
}
