using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>Overdue balance → collection case (people follow up; nothing automatic on money or assets), late payment, staff directory, finance overview.</summary>
public sealed class PaymentPlanCollectionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(Agreed Flow, DateTimeOffset DueAt)> BalanceDueAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var flow = await AgreedAsync(ctx, customer);
        var projectId = await CompletionReportedAsync(ctx, flow);
        await AcceptAsync(ctx, flow, projectId);
        return (flow, (await InstallmentAsync(ctx, flow.ContractId, 2)).DueAt!.Value);
    }

    [Fact]
    public async Task OVERDUE_sweep_opens_one_case_only_after_the_deadline_and_never_twice()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (flow, due) = await BalanceDueAsync(ctx);
        var ledger = ctx.Ledger();

        ctx.Clock.Now = due;                                           // exactly at the deadline: on time
        Assert.Equal(0, await ledger.SweepOverdueAsync(50, None));
        ctx.Clock.Now = due.AddSeconds(1);
        Assert.Equal(1, await ledger.SweepOverdueAsync(50, None));
        Assert.Equal(0, await ledger.SweepOverdueAsync(50, None));      // one open case per installment
        var collectionCase = await ctx.Db.ContractCollectionCases.AsNoTracking().SingleAsync();
        Assert.Equal((CollectionCaseStatuses.SalesContactRequired, due, (await InstallmentAsync(ctx, flow.ContractId, 2)).Amount),
            (collectionCase.Status, collectionCase.DueAt, collectionCase.AmountDue));
        // Overdue is information: the installment stays DUE (payable), the contract stays SIGNED, no money or ownership action.
        var balance = await InstallmentAsync(ctx, flow.ContractId, 2);
        Assert.Equal((InstallmentStatuses.Due, due), (balance.Status, balance.DueAt!.Value));
        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Signed, PaymentPlanStages.BalanceOverdue, OwnershipStatuses.Retained, true),
            (view.Status, view.PaymentPlan!.Stage, view.PaymentPlan.OwnershipStatus, view.PaymentPlan.Overdue));
        Assert.Single(view.PaymentPlan.Collection!);
        Assert.Contains(PaymentPlanWorkflowTests.CustomerNotificationTypesSeen(ctx, flow.CustomerId),
            t => t == SmartSolar.Modules.Common.Notifications.CustomerNotificationTypes.ContractBalanceOverdue);
        // The customer never sees the collection case.
        Assert.Null((await ctx.Contracts().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.PaymentPlan!.Collection);
    }

    [Fact]
    public async Task COLLECTION_sales_records_contacts_and_agreements_manager_escalates_to_legal_review_and_resolves()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (flow, due) = await BalanceDueAsync(ctx);
        ctx.Clock.Now = due.AddHours(5);
        await ctx.Ledger().SweepOverdueAsync(50, None);
        var caseId = (await ctx.Db.ContractCollectionCases.AsNoTracking().SingleAsync()).Id;
        var sales = await ctx.Scheduling.SeedSalesAsync("Collector");
        var collections = ctx.Collections();

        Assert.Equal(CommonErrorCodes.ValidationFailed, (await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("CONTACT", "PHONE", " ", null, null, null), None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("AGREEMENT", "PHONE", "promised", null, null, null), None)).ErrorCode);   // promised date required
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("REPOSSESS", null, "x", null, null, null), None)).ErrorCode);

        var contacted = await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId, new CollectionActivityInput("CONTACT", "PHONE", "Called, customer travelling", null, null, null), None);
        Assert.Equal(CollectionCaseStatuses.Contacted, contacted.Value!.Status);
        var evidence = await ctx.VerifiedFileAsync(sales.Id, FilePurposes.CollectionEvidence);
        var promised = DateOnly.FromDateTime(due.UtcDateTime.AddDays(7));
        var agreed = await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("AGREEMENT", "ZALO", "Will pay next week", promised, [evidence], contacted.Value.Revision), None);
        Assert.True(agreed.Succeeded, agreed.Message);
        Assert.Equal((CollectionCaseStatuses.AgreementRecorded, promised), (agreed.Value!.Status, agreed.Value.PromisedPaymentDate!.Value));
        // An agreement never extends the contractual deadline.
        Assert.Equal(due, (await InstallmentAsync(ctx, flow.ContractId, 2)).DueAt);
        Assert.Equal(PaymentPlanErrorCodes.StaleRevision, (await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("NOTE", null, "stale", null, null, contacted.Value.Revision), None)).ErrorCode);

        // Sales escalates; legal review and resolution are Manager/Admin only.
        Assert.True((await collections.EscalateAsync(sales.Id, RoleCodes.Sales, caseId, "Promise broken twice", null, None)).Succeeded);
        Assert.Equal(PaymentPlanErrorCodes.CollectionInvalidTransition, (await collections.EscalateAsync(sales.Id, RoleCodes.Sales, caseId, "again", null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.Forbidden, (await collections.RequestLegalReviewAsync(sales.Id, RoleCodes.Sales, caseId, "x", null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.Forbidden, (await collections.ResolveAsync(sales.Id, RoleCodes.Sales, caseId, "x", null, None)).ErrorCode);
        // Sales activity while escalated is recorded but does not take the case back from the Manager.
        Assert.Equal(CollectionCaseStatuses.EscalatedToManager, (await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("CONTACT", "VISIT", "Visited, nobody home", null, null, null), None)).Value!.Status);
        var legal = await collections.RequestLegalReviewAsync(flow.Manager.Id, RoleCodes.Manager, caseId, "Retention clause enforceability to be checked", null, None);
        Assert.Equal(CollectionCaseStatuses.LegalReviewRequired, legal.Value!.Status);
        Assert.True((await StaffViewAsync(ctx, flow.ContractId)).PaymentPlan!.LegalReviewRequired);
        var resolved = await collections.ResolveAsync(flow.Manager.Id, RoleCodes.Manager, caseId, "Instalment plan signed by both parties (outside the system)", null, None);
        Assert.Equal(CollectionCaseStatuses.Resolved, resolved.Value!.Status);
        Assert.Equal(PaymentPlanErrorCodes.CollectionClosed, (await collections.RecordActivityAsync(sales.Id, RoleCodes.Sales, caseId,
            new CollectionActivityInput("NOTE", null, "late", null, null, null), None)).ErrorCode);

        // Every step is attributed; nothing touched money: the balance is still DUE and payable.
        var activities = resolved.Value.Activities!;
        Assert.Equal(new[] { "OPENED", "CONTACT", "AGREEMENT", "ESCALATE", "CONTACT", "LEGAL_REVIEW", "RESOLVE" }, activities.Select(a => a.Kind));
        Assert.All(activities.Skip(1), a => Assert.NotNull(a.ActorUserId));
        Assert.Equal(new[] { evidence }, activities[2].EvidenceFileIds);
        Assert.Equal(InstallmentStatuses.Due, (await InstallmentAsync(ctx, flow.ContractId, 2)).Status);
        Assert.Equal(0, await ctx.Db.RefundRecords.CountAsync());
    }

    [Fact]
    public async Task LATE_balance_is_accepted_flagged_late_and_closes_the_open_case_automatically()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (flow, due) = await BalanceDueAsync(ctx);
        ctx.Clock.Now = due.AddDays(2);
        await ctx.Ledger().SweepOverdueAsync(50, None);
        await PayCashAsync(ctx, flow, 2, InstallmentPayer.Customer);

        var balance = await InstallmentAsync(ctx, flow.ContractId, 2);
        Assert.Equal((InstallmentStatuses.Paid, true), (balance.Status, balance.PaidLate));
        var collectionCase = await ctx.Db.ContractCollectionCases.AsNoTracking().SingleAsync();
        Assert.Equal((CollectionCaseStatuses.Resolved, "PAID"), (collectionCase.Status, collectionCase.Resolution));
        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, PaymentPlanStages.FullyPaid, false), (view.Status, view.PaymentPlan!.Stage, view.PaymentPlan.Overdue));
        Assert.Contains(view.Events!, e => e.EventType == "BALANCE_PAID" && e.Detail!.Contains("LATE"));
        Assert.Equal(0, await ctx.Ledger().SweepOverdueAsync(50, None));
    }

    [Fact]
    public async Task DIRECTORY_staff_read_every_contract_with_filters_search_and_debt_figures()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (overdue, due) = await BalanceDueAsync(ctx, "Overdue customer");
        var fresh = await AgreedAsync(ctx, "Fresh customer");
        ctx.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        var legacy = await SimplifiedFlowFixture.SignedAsync(ctx, "Legacy customer");
        ctx.Clock.Now = due.AddHours(1);
        var directory = ctx.Directory();

        var all = (await directory.PageAsync(new ContractListFilter(), new PageRequest(1, 20), None)).Value!;
        Assert.Equal(3, all.TotalItems);
        var row = all.Items.Single(i => i.ContractId == overdue.ContractId);
        var split = PaymentPlanCalculator.Split(row.TotalAmount)!;
        Assert.Equal((split.Deposit, split.Balance, true, false, true, PaymentPlanStages.BalanceOverdue, OwnershipStatuses.Retained),
            (row.Paid, row.Outstanding, row.DepositPaid, row.FullyPaid, row.Overdue, row.Stage, row.OwnershipStatus));
        Assert.NotNull(row.ProjectId);
        var legacyRow = all.Items.Single(i => i.ContractId == legacy.Contract.ContractId);
        Assert.Equal((null, 0m, legacyRow.TotalAmount, OwnershipStatuses.NotApplicable), (legacyRow.PaymentPlan, legacyRow.Paid, legacyRow.Outstanding, legacyRow.OwnershipStatus));

        Assert.Equal(new[] { overdue.ContractId }, (await directory.PageAsync(new ContractListFilter(Overdue: true), new PageRequest(1, 20), None)).Value!.Items.Select(i => i.ContractId));
        Assert.Equal(new[] { fresh.ContractId }, (await directory.PageAsync(new ContractListFilter(CustomerUserId: fresh.CustomerId), new PageRequest(1, 20), None)).Value!.Items.Select(i => i.ContractId));
        Assert.Equal(new[] { legacy.Contract.ContractId }, (await directory.PageAsync(new ContractListFilter(PaymentPlan: "LEGACY"), new PageRequest(1, 20), None)).Value!.Items.Select(i => i.ContractId));
        Assert.Equal(new[] { overdue.ContractId }, (await directory.PageAsync(new ContractListFilter(Search: "overdue CUST"), new PageRequest(1, 20), None)).Value!.Items.Select(i => i.ContractId));
        Assert.Equal(new[] { fresh.ContractId }, (await directory.PageAsync(new ContractListFilter(Search: (await ctx.Db.CustomerContracts.SingleAsync(c => c.Id == fresh.ContractId)).ContractNumber[^4..]),
            new PageRequest(1, 20), None)).Value!.Items.Select(i => i.ContractId));
        var page = (await directory.PageAsync(new ContractListFilter(), new PageRequest(2, 2), None)).Value!;
        Assert.Equal((1, 3, 2), (page.Items.Count, page.TotalItems, page.TotalPages));
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await directory.PageAsync(new ContractListFilter(Status: "LOST"), new PageRequest(1, 20), None)).ErrorCode);

        // Any staff reader reads any contract (no ownership); customers only their own.
        var sales = await ctx.Scheduling.SeedSalesAsync("Unrelated sales");
        Assert.True((await ctx.Contracts().GetForStaffAsync(sales.Id, false, fresh.ContractId, None)).Succeeded);
        Assert.Equal(ContractErrorCodes.NotFound, (await ctx.Contracts().GetForCustomerAsync(fresh.CustomerId, overdue.ContractId, None)).ErrorCode);
        Assert.Equal(new[] { fresh.ContractId }, (await ctx.Contracts().ListForCustomerAsync(fresh.CustomerId, None)).Select(c => c.ContractId));
    }

    [Fact]
    public async Task FINANCE_overview_counts_applied_money_once_and_never_pending_or_unapplied_money()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var (accepted, due) = await BalanceDueAsync(ctx, "Accepted customer");
        var signed = await AgreedAsync(ctx, "Deposit customer");
        await PayOnlineAsync(ctx, signed, 1);
        var pending = await AgreedAsync(ctx, "Pending customer");
        // A pending cash choice (not confirmed) and a wrong-amount online payment.
        Assert.True((await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, pending.TechnicianId, pending.ContractId, 1, "CASH", None)).Succeeded);
        var other = await AgreedAsync(ctx, "Wrong amount customer");
        var qr = (await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Technician, other.TechnicianId, other.ContractId, 1, "ONLINE", None)).Value!.Current!;
        await ctx.PaymentsWithSchedule().HandleWebhookAsync(ctx.PayOs.Webhook(qr.OrderCode, (long)qr.Amount + 5, "FT-OVER"), None);
        ctx.Clock.Now = due.AddHours(1);

        var today = DateOnly.FromDateTime(ctx.Clock.GetUtcNow().UtcDateTime);
        var overview = (await ctx.Finance().GetAsync(today.AddDays(-60), today, null, None)).Value!;
        decimal Deposit(Agreed f) => ctx.Db.ContractInstallments.AsNoTracking().Single(i => i.ContractId == f.ContractId && i.Sequence == 1).Amount;
        decimal Balance(Agreed f) => ctx.Db.ContractInstallments.AsNoTracking().Single(i => i.ContractId == f.ContractId && i.Sequence == 2).Amount;

        Assert.Equal(4, overview.PaymentPlanContracts.Count);
        Assert.Equal((2, Deposit(accepted) + Deposit(signed)), (overview.DepositsPaid.Count, overview.DepositsPaid.Amount));
        Assert.Equal((2, Deposit(accepted) + Deposit(signed)), (overview.DepositsCollected.Count, overview.DepositsCollected.Amount));
        Assert.Equal((2, Deposit(pending) + Deposit(other)), (overview.DepositsDue.Count, overview.DepositsDue.Amount));
        Assert.Equal((1, Balance(accepted)), (overview.BalancesOverdue.Count, overview.BalancesOverdue.Amount));
        Assert.Equal(0, overview.BalancesDue.Count);
        Assert.Equal((1, Balance(accepted)), (overview.AcceptedUnpaid.Count, overview.AcceptedUnpaid.Amount));
        Assert.Equal(0, overview.BalancesCollected.Count);
        // PayOS deposit online, cash deposit by the Manager: each counted once under its method; the pending cash choice is not collected.
        Assert.Equal(Deposit(signed), overview.InstallmentsCollectedByMethod.Single(b => b.Key == "ONLINE").Amount);
        Assert.Equal(Deposit(accepted), overview.InstallmentsCollectedByMethod.Single(b => b.Key == "CASH").Amount);
        // The wrong-amount money is unapplied (manual handling), never collected.
        Assert.Equal((1, Deposit(other) + 5), (overview.Unapplied.Count, overview.Unapplied.Amount));
        var totalPlan = overview.PaymentPlanContracts.Amount;
        Assert.Equal(totalPlan - overview.DepositsPaid.Amount - overview.BalancesPaid.Amount, overview.Outstanding.Amount);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await ctx.Finance().GetAsync(today, today.AddDays(-1), null, None)).ErrorCode);
        // Zone filter: only contracts with an installation project in that zone.
        var zone = (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).OperationalZoneId;
        var zoned = (await ctx.Finance().GetAsync(today.AddDays(-60), today, zone, None)).Value!;
        Assert.Equal(zone is null ? 0 : 1, zoned.PaymentPlanContracts.Count);
    }
}
