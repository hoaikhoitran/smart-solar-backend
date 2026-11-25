using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Common.Notifications;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// The seven 30/70 business workflows end to end on the production handlers (2026-11-19). Each journey checks the money (applied exactly once), the
/// separate lifecycles (contract / installment / installation / collection) and who acted.
/// </summary>
public sealed class PaymentPlanE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>E2E-3070-1: agreement → QR deposit → paper → installation → customer ACCEPT → QR balance by the customer → FULLY PAID.</summary>
    [Fact]
    public async Task E2E_3070_1_qr_deposit_paper_signature_installation_customer_acceptance_qr_balance_fully_paid()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow, payOnline: true);
        Assert.Equal(ContractStatuses.Signed, (await StaffViewAsync(ctx, flow.ContractId)).Status);   // installed with 30% only
        await AcceptAsync(ctx, flow, projectId);
        await PayOnlineAsync(ctx, flow, 2, InstallmentPayer.Customer, "FT-BALANCE");

        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, PaymentPlanStages.FullyPaid, true, 0m), (view.Status, view.PaymentPlan!.Stage, view.PaymentCompleted, view.PaymentPlan.Outstanding));
        Assert.Equal(view.TotalAmount, view.PaymentPlan.Paid);
        Assert.Equal(2, await ctx.Db.PaymentAttempts.CountAsync(a => a.Status == PaymentStatuses.Paid));
        Assert.Equal(view.TotalAmount, await ctx.Db.PaymentAttemptItems.Where(i => i.Status == PaymentItemStatuses.Settled).SumAsync(i => i.Amount));
        Assert.Equal(InstallationStatuses.Completed, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Status);
        var notifications = PaymentPlanWorkflowTests.CustomerNotificationTypesSeen(ctx, flow.CustomerId);
        foreach (var expected in new[] { CustomerNotificationTypes.ContractReadyForPreview, CustomerNotificationTypes.ContractDepositReceived,
                     CustomerNotificationTypes.ContractPaperSigned, CustomerNotificationTypes.InstallationAcceptanceRequested, CustomerNotificationTypes.ContractBalanceDue,
                     CustomerNotificationTypes.ContractFullyPaid })
            Assert.Contains(expected, notifications);
        // The contract history tells the whole story in order; the status sequence is the 30/70 one.
        var statuses = view.Events!.Where(e => e.EventType == "STATUS_CHANGED").Select(e => e.ToStatus).ToList();
        Assert.Equal(new[] { ContractStatuses.AwaitingSignature, ContractStatuses.Signed, ContractStatuses.Paid }, statuses);
    }

    /// <summary>E2E-3070-2: cash for both installments, each confirmed by a Manager (unique receipt, verified evidence); the technician never confirms.</summary>
    [Fact]
    public async Task E2E_3070_2_cash_deposit_and_cash_balance_confirmed_by_a_manager()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        await AcceptAsync(ctx, flow, projectId);
        // The balance: the customer chooses cash in the app; until a Manager records it, nothing is paid.
        var choice = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "CASH", None)).Value!;
        Assert.Equal(InstallmentPaymentStates.AwaitingCashConfirmation, choice.PaymentState);
        Assert.Equal(ContractStatuses.Signed, (await StaffViewAsync(ctx, flow.ContractId)).Status);
        // Evidence uploaded by someone else is refused (the confirming staff member registers it).
        var techEvidence = await ctx.VerifiedFileAsync(flow.TechnicianId, SmartSolar.Modules.Files.FilePurposes.PaymentEvidence);
        Assert.False((await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(flow.Manager.Id, choice.Current!.PaymentAttemptId,
            new CashConfirmation(choice.Amount, "PT-B-1", techEvidence, null), None)).Succeeded);
        var evidence = await ctx.VerifiedFileAsync(flow.Manager.Id, SmartSolar.Modules.Files.FilePurposes.PaymentEvidence);
        Assert.Equal(PaymentErrorCodes.CashAmountMismatch, (await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(flow.Manager.Id, choice.Current.PaymentAttemptId,
            new CashConfirmation(choice.Amount + 1, "PT-B-1", evidence, null), None)).ErrorCode);
        var confirmed = await ctx.PaymentsWithCash().ConfirmCashAttemptAsync(flow.Manager.Id, choice.Current.PaymentAttemptId,
            new CashConfirmation(choice.Amount, "PT-B-1", evidence, "cashier receipt"), None);
        Assert.True(confirmed.Succeeded, confirmed.Message);
        Assert.Equal(flow.Manager.Id, confirmed.Value!.ConfirmedByUserId);

        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, OwnershipStatuses.TransferEligible), (view.Status, view.PaymentPlan!.OwnershipStatus));
        Assert.All(await ctx.Db.PaymentAttempts.AsNoTracking().ToListAsync(), a => Assert.Equal((PaymentProviders.Cash, flow.Manager.Id), (a.Provider, a.ConfirmedByUserId!.Value)));
    }

    /// <summary>E2E-3070-3: the customer asks for rework (reason + photo), a new round is reported, then accepted; the balance opens only then.</summary>
    [Fact]
    public async Task E2E_3070_3_customer_rework_then_acceptance_opens_the_balance()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        var photo = await ctx.VerifiedFileAsync(flow.CustomerId, SmartSolar.Modules.Files.FilePurposes.AcceptanceEvidence);
        Assert.True((await ctx.Installations().DecideAcceptanceAsync(flow.CustomerId, projectId,
            new CustomerAcceptanceInput(CustomerAcceptanceDecisions.RequestRework, "Two panels are cracked", [photo], null), None)).Succeeded);
        // The Manager coordinates the rework; a second report goes back to the customer.
        Assert.Equal(InstallmentStatuses.Pending, (await InstallmentAsync(ctx, flow.ContractId, 2)).Status);
        await InstallationTests.ReworkScheduledAsync(ctx, projectId, flow.TechnicianId, flow.Manager.Id);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await ctx.Installations().ReportCompletionAsync(flow.TechnicianId, projectId, "Panels replaced.", null, None)).Succeeded);
        var acceptedAt = ctx.Clock.GetUtcNow();
        await AcceptAsync(ctx, flow, projectId);
        var balance = await InstallmentAsync(ctx, flow.ContractId, 2);
        Assert.Equal((InstallmentStatuses.Due, acceptedAt.AddHours(72)), (balance.Status, balance.DueAt!.Value));
        var rounds = await ctx.Db.InstallationAcceptances.AsNoTracking().OrderBy(a => a.Round).ToListAsync();
        Assert.Equal(new[] { (1, AcceptanceDecisions.ReworkRequested), (2, AcceptanceDecisions.Accepted) }, rounds.Select(r => (r.Round, r.Decision)));
        Assert.All(rounds, r => Assert.Equal(flow.CustomerId, r.DecidedByUserId));
    }

    /// <summary>E2E-3070-4: overdue balance → collection case → Sales contact + agreement → escalation → late payment closes the case.</summary>
    [Fact]
    public async Task E2E_3070_4_overdue_balance_collection_follow_up_and_late_payment()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var projectId = await CompletionReportedAsync(ctx, flow);
        await AcceptAsync(ctx, flow, projectId);
        var due = (await InstallmentAsync(ctx, flow.ContractId, 2)).DueAt!.Value;
        ctx.Clock.Now = due.AddDays(1);
        Assert.Equal(1, await ctx.Ledger().SweepOverdueAsync(50, None));
        var sales = await ctx.Scheduling.SeedSalesAsync("Collector");
        var list = (await ctx.Collections().PageAsync(new CollectionCaseFilter(OpenOnly: true), new PageRequest(1, 20), None)).Value!;
        var collectionCase = Assert.Single(list.Items);
        Assert.Equal((flow.ContractId, 1), (collectionCase.ContractId, collectionCase.DaysOverdue));
        Assert.True((await ctx.Collections().RecordActivityAsync(sales.Id, RoleCodes.Sales, collectionCase.CaseId,
            new CollectionActivityInput("CONTACT", "PHONE", "Customer will transfer this week", null, null, null), None)).Succeeded);
        Assert.True((await ctx.Collections().RecordActivityAsync(sales.Id, RoleCodes.Sales, collectionCase.CaseId,
            new CollectionActivityInput("AGREEMENT", "PHONE", "Promised Friday", DateOnly.FromDateTime(due.UtcDateTime.AddDays(4)), null, null), None)).Succeeded);
        Assert.True((await ctx.Collections().EscalateAsync(sales.Id, RoleCodes.Sales, collectionCase.CaseId, "Friday passed", null, None)).Succeeded);

        ctx.Clock.Now = due.AddDays(6);
        await PayOnlineAsync(ctx, flow, 2, InstallmentPayer.Customer, "FT-LATE");
        var balance = await InstallmentAsync(ctx, flow.ContractId, 2);
        Assert.Equal((InstallmentStatuses.Paid, true, due), (balance.Status, balance.PaidLate, balance.DueAt!.Value));
        var closed = (await ctx.Collections().GetAsync(collectionCase.CaseId, None)).Value!;
        Assert.Equal((CollectionCaseStatuses.Resolved, "PAID"), (closed.Status, closed.Resolution));
        Assert.Empty((await ctx.Collections().PageAsync(new CollectionCaseFilter(OpenOnly: true), new PageRequest(1, 20), None)).Value!.Items);
        Assert.Equal(ContractStatuses.Paid, (await StaffViewAsync(ctx, flow.ContractId)).Status);
        Assert.Equal(0, await ctx.Db.RefundRecords.CountAsync());
    }

    /// <summary>E2E-3070-5: the customer asks for changes before paying; the agreed contract is discarded, the new version is agreed with its own contract.</summary>
    [Fact]
    public async Task E2E_3070_5_revision_before_the_deposit_discards_the_first_contract_and_the_new_version_gets_its_own()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var q = ctx.QuotationsWithPaymentPlan();
        var first = await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == flow.Cart.Quotation.QuotationId);
        Assert.True((await q.RecordOnSiteDecisionAsync(flow.TechnicianId, first.Id,
            new OnSiteDecisionInput(OnSiteDecisions.RevisionNeeded, first.Revision, "Only one transport trip"), None)).Succeeded);
        var draft = (await q.CreateCartRevisionAsync(flow.TechnicianId, first.Id, None)).Value!;
        var transport = draft.Lines.Single(l => l.ProductId == flow.Cart.Catalog.Transport.Id);
        var trimmed = (await q.RemoveCartLineAsync(flow.TechnicianId, draft.QuotationId, transport.LineId, draft.Revision, None)).Value!;
        var v2 = (await q.IssueCartAsync(flow.TechnicianId, trimmed.QuotationId, trimmed.Revision, None)).Value!;
        var agreed = (await q.RecordOnSiteDecisionAsync(flow.TechnicianId, v2.QuotationId, new OnSiteDecisionInput(OnSiteDecisions.Agreed, v2.Revision, null), None)).Value!;

        var contracts = await ctx.Db.CustomerContracts.AsNoTracking().OrderBy(c => c.CreatedAt).ToListAsync();
        Assert.Equal(new[] { ContractStatuses.Discarded, ContractStatuses.Draft }, contracts.Select(c => c.Status));
        Assert.Equal((agreed.ContractId!.Value, v2.TotalAmount, 2), (contracts[1].Id, contracts[1].TotalAmount, contracts[1].QuotationVersion));
        Assert.Equal(new[] { contracts[1].Id }, (await ctx.Contracts().ListForCustomerAsync(flow.CustomerId, None)).Select(c => c.ContractId));
        // The new contract follows the normal plan.
        await PayCashAsync(ctx, flow with { ContractId = contracts[1].Id }, 1);
        Assert.Equal(ContractStatuses.AwaitingSignature, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == contracts[1].Id)).Status);
    }

    /// <summary>E2E-3070-6: payment anomalies — duplicate webhook, wrong amount, a second payment of an already paid installment — are never applied twice.</summary>
    [Fact]
    public async Task E2E_3070_6_duplicate_wrong_amount_and_double_payments_go_to_manual_handling_and_are_never_applied_twice()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var payments = ctx.PaymentsWithSchedule();
        // Two QR links for the same deposit can never be open at once; a cash choice and a link neither.
        var qr = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, "ONLINE", None)).Value!.Current!;
        // The technician cancels the QR (the customer said the bank app failed) and takes cash; a Manager confirms it.
        Assert.True((await ctx.InstallmentPayments().CancelAsync(InstallmentPayer.Technician, flow.TechnicianId, flow.ContractId, 1, None)).Succeeded);
        await PayCashAsync(ctx, flow, 1);
        // ... but the customer had actually paid the QR: the money arrives after the cash confirmation. It is never applied a second time.
        await payments.ConfirmAsync(qr.PaymentAttemptId, (long)qr.Amount, "FT-DOUBLE", ctx.Clock.GetUtcNow(), PaymentSources.Webhook, None);
        var doubled = await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.PaymentAttemptId == qr.PaymentAttemptId);
        Assert.Equal(PaymentItemStatuses.ReviewRequired, doubled.Status);
        Assert.Contains("already paid", doubled.Note);
        Assert.Equal(1, await ctx.Db.PaymentAttemptItems.CountAsync(i => i.ContractInstallmentId != null && i.Status == PaymentItemStatuses.Settled));
        var deposit = await InstallmentAsync(ctx, flow.ContractId, 1);
        Assert.Equal(deposit.Amount, deposit.PaidAmount);
        Assert.NotEqual(qr.PaymentAttemptId, deposit.SettledPaymentAttemptId);
        // Finance: applied once; the double payment is unapplied (manual refund decision; no refund button exists).
        var today = DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime);
        var overview = (await ctx.Finance().GetAsync(today.AddDays(-1), today.AddDays(1), null, None)).Value!;
        Assert.Equal((1, deposit.Amount), (overview.DepositsCollected.Count, overview.DepositsCollected.Amount));
        Assert.Equal((1, deposit.Amount), (overview.Unapplied.Count, overview.Unapplied.Amount));
    }

    /// <summary>
    /// E2E-3070-7: RBAC and coexistence — staff read every contract, each customer only their own (several sites / contracts), a legacy fully paid
    /// contract is untouched by the plan (no balance at acceptance), and after-sales stays 100% upfront (no bundling with a 30/70 contract).
    /// </summary>
    [Fact]
    public async Task E2E_3070_7_read_all_staff_customer_isolation_legacy_contracts_and_after_sales_stay_100_percent()
    {
        await using var after = AfterSalesTestContext.Create();
        var ctx = after.Installation;
        var flow = await AgreedAsync(ctx, "Plan customer");
        var second = await AgreedAsync(ctx, "Other customer");
        var projectId = await CompletionReportedAsync(ctx, flow);
        await AcceptAsync(ctx, flow, projectId);

        // Staff read-all, customers isolated.
        var technicianOfOther = second.TechnicianId;
        Assert.True((await ctx.Contracts().GetForStaffAsync(technicianOfOther, false, flow.ContractId, None)).Succeeded);
        Assert.Equal(2, (await ctx.Directory().PageAsync(new ContractListFilter(), new PageRequest(1, 20), None)).Value!.TotalItems);
        Assert.Equal(ContractErrorCodes.NotFound, (await ctx.Contracts().GetForCustomerAsync(second.CustomerId, flow.ContractId, None)).ErrorCode);
        Assert.Equal(ContractErrorCodes.NotFound,
            (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Customer, second.CustomerId, flow.ContractId, 2, None)).ErrorCode);

        // After-sales: never bundled with a 30/70 contract; packages only once the contract is fully paid, and then paid 100% upfront.
        var admin = await ctx.Scheduling.Inner.SeedUserAsync("Admin");
        var care = await after.PackageAsync(admin.Id, ServicePackageKinds.Maintenance);
        Assert.Equal(AfterSalesErrorCodes.InvalidState,
            (await after.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, care.Id, null, true), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.InvalidState,
            (await after.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, care.Id, null, false), None)).ErrorCode);
        await PayOnlineAsync(ctx, flow, 2, InstallmentPayer.Customer, "FT-BAL-7");
        var bought = await after.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, care.Id, null, false), None);
        Assert.True(bought.Succeeded, bought.Message);
        Assert.Equal(care.Price, bought.Value!.Checkout.Amount);                      // 100% of the package, no installment
        var paid = await after.PayAsync(bought.Value.Checkout);
        Assert.Equal(PaymentStatuses.Paid, paid!.Status);
        Assert.DoesNotContain(await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.PaymentAttemptId == paid.Id).Select(i => i.ItemType).ToListAsync(),
            t => t == PaymentItemTypes.ContractInstallment);

        // A legacy contract (paid 100% before the release) keeps its meaning: accepting it opens no balance and creates no installment.
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        var legacy = await SimplifiedFlowFixture.PrepaidAsync(ctx, "Legacy customer");
        Assert.Null((await ctx.Contracts().GetForStaffAsync(Guid.Empty, true, legacy.Contract.ContractId, None)).Value!.PaymentPlan);
        Assert.Empty(await ctx.Db.ContractInstallments.AsNoTracking().Where(i => i.ContractId == legacy.Contract.ContractId).ToListAsync());
        Assert.Equal(PaymentPlanErrorCodes.NotApplicable,
            (await ctx.InstallmentPayments().GetAsync(InstallmentPayer.Customer, legacy.Cart.CustomerId, legacy.Contract.ContractId, 1, None)).ErrorCode);
    }
}
