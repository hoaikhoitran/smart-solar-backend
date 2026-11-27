using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.PaymentPlanFlow;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// E2E Flow 2 with physical cash receipts (2026-11-27): agreement → cash deposit (receipt rejected, resent, accepted) → paper signature → installation →
/// customer ACCEPT → cash balance (receipt accepted) → FULLY PAID. Money applied exactly once per installment; who took, who confirmed, when.
/// </summary>
public sealed class TechnicianCashReceiptE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task E2E_cash_deposit_and_balance_through_technician_receipts_reviewed_by_a_manager()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var flow = await AgreedAsync(ctx);
        var payments = ctx.PaymentsWithSchedule();

        // Deposit 30 %: first receipt rejected, second accepted.
        var depositAttempt = await TechnicianCashReceiptTests.CashChosenAsync(ctx, flow, 1);
        var blurred = await TechnicianCashReceiptTests.SentAsync(ctx, flow, 1, depositAttempt, "PT-DEP-01");
        Assert.True((await payments.RejectCashReceiptAsync(flow.Manager.Id, blurred.CashReceiptId, blurred.Revision, "Ảnh mờ", None)).Succeeded);
        var clear = await TechnicianCashReceiptTests.SentAsync(ctx, flow, 1, depositAttempt, "PT-DEP-01");
        Assert.True((await payments.AcceptCashReceiptAsync(flow.Manager.Id, clear.CashReceiptId, clear.Revision, null, None)).Succeeded);
        Assert.Equal(PaymentPlanStages.DepositVerified, (await StaffViewAsync(ctx, flow.ContractId)).PaymentPlan!.Stage);

        // Paper, schedule, installation, report, customer acceptance (production steps, unchanged).
        var planned = await ctx.Schedules().ProposeAsync(flow.TechnicianId, false, flow.ContractId, InstallationScheduleTests.Plan(note: "Agreed in person"), None);
        Assert.True(planned.Succeeded, planned.Message);
        await SignOnPaperAsync(ctx, flow);
        var projectId = (await ctx.Schedules().GetForCustomerAsync(flow.CustomerId, flow.ContractId, None)).Value!.First().ProjectId!.Value;
        await InstallationTests.SatisfyPrerequisitesAsync(ctx, projectId, flow.Manager.Id);
        Assert.True((await ctx.Installations().StartProjectAsync(flow.TechnicianId, projectId, None)).Succeeded);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, projectId, flow.TechnicianId);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, projectId, flow.TechnicianId)).Succeeded);
        Assert.True((await ctx.Installations().ReportCompletionAsync(flow.TechnicianId, projectId, "Done", null, None)).Succeeded);
        await AcceptAsync(ctx, flow, projectId);

        // Balance 70 % in cash through a receipt.
        var balanceAttempt = await TechnicianCashReceiptTests.CashChosenAsync(ctx, flow, 2);
        var balance = await TechnicianCashReceiptTests.SentAsync(ctx, flow, 2, balanceAttempt, "PT-BAL-01");
        Assert.Equal(ContractStatuses.Signed, (await StaffViewAsync(ctx, flow.ContractId)).Status);   // not paid by sending
        Assert.True((await payments.AcceptCashReceiptAsync(flow.Manager.Id, balance.CashReceiptId, balance.Revision, null, None)).Succeeded);

        var view = await StaffViewAsync(ctx, flow.ContractId);
        Assert.Equal((ContractStatuses.Paid, PaymentPlanStages.FullyPaid, 0m), (view.Status, view.PaymentPlan!.Stage, view.PaymentPlan.Outstanding));
        Assert.Equal(view.TotalAmount, view.PaymentPlan.Paid);
        Assert.Equal(InstallationStatuses.Completed, (await ctx.Db.InstallationProjects.AsNoTracking().SingleAsync()).Status);
        var paid = await ctx.Db.PaymentAttempts.AsNoTracking().Where(a => a.Status == PaymentStatuses.Paid).ToListAsync();
        Assert.Equal(2, paid.Count);
        Assert.All(paid, a => Assert.Equal(flow.Manager.Id, a.ConfirmedByUserId));
        Assert.Equal(["PT-BAL-01", "PT-DEP-01"], paid.Select(a => a.ProviderReference!).Order());
        Assert.Equal(view.TotalAmount, await ctx.Db.PaymentAttemptItems.Where(i => i.Status == PaymentItemStatuses.Settled).SumAsync(i => i.Amount));
        var receipts = await ctx.Db.CashReceiptSubmissions.AsNoTracking().ToListAsync();
        Assert.Equal(3, receipts.Count);
        Assert.Equal(2, receipts.Count(r => r.Status == CashReceiptStatuses.Accepted));
        Assert.Single(receipts, r => r.Status == CashReceiptStatuses.Rejected);
        Assert.All(receipts, r => Assert.Equal(flow.TechnicianId, r.CollectedByUserId));
        // Every photo is the technician's own PAYMENT_EVIDENCE file; the manager uploaded nothing.
        var photos = receipts.Select(r => r.EvidenceFileId).ToList();
        Assert.All(await ctx.Db.StoredFiles.AsNoTracking().Where(f => photos.Contains(f.Id)).ToListAsync(), f =>
        {
            Assert.Equal(FilePurposes.PaymentEvidence, f.Purpose);
            Assert.Equal(flow.TechnicianId, f.UploadedByUserId);
        });
        Assert.Equal(0, await ctx.Db.StoredFiles.CountAsync(f => f.UploadedByUserId == flow.Manager.Id && f.Purpose == FilePurposes.PaymentEvidence));
    }
}
