using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.AfterSales;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// E2E after-sales journeys paid in cash with technician receipts (2026-11-27): quotation on site → AGREED → CASH → receipt → Manager review →
/// existing after-sales settlement → work → completion. Money applied once; who took the cash, who confirmed it and when.
/// </summary>
public sealed class UnifiedCashReceiptE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task E2E_paid_repair_cash_receipt_rejected_resent_accepted_then_repaired_and_completed()
    {
        await using var ctx = Create();
        var t = await UnifiedCashReceiptTests.TechnicianQuotedAsync(ctx);
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId);
        var first = await UnifiedCashReceiptTests.SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-E2E-R");
        Assert.True((await ctx.Payments().RejectCashReceiptAsync(t.H.ManagerId, first.CashReceiptId, first.Revision, "Ảnh mờ", None)).Succeeded);
        var second = await UnifiedCashReceiptTests.SentAsync(ctx, t.H.TechnicianId, t.Quotation.QuotationId, attemptId, "PT-E2E-R", first.CollectedAt);
        Assert.True((await ctx.Payments().AcceptCashReceiptAsync(t.H.ManagerId, second.CashReceiptId, second.Revision, null, None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(t.RequestId, None)).Value!.Status);

        var repair = await ctx.VisitStartedAsync(t.H, t.RequestId, ctx.Clock.GetUtcNow().AddDays(2));
        Assert.True((await ctx.Technician().ReportWorkAsync(t.H.TechnicianId, repair, "Repaired", [new ItemResultInput(t.Items[0], "repaired", null)], [], null, None)).Succeeded);
        var closed = await ctx.Customer().ConfirmCompletionOnSiteAsync(t.H.TechnicianId, t.RequestId, "Customer checked the output", null, None);
        Assert.True(closed.Succeeded, closed.Message);
        Assert.Equal(ServiceRequestStatuses.Completed, closed.Value!.Status);

        var paid = await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attemptId);
        Assert.Equal((PaymentStatuses.Paid, 450_000m, "PT-E2E-R", (Guid?)t.H.ManagerId), (paid.Status, paid.AmountPaid!.Value, paid.ProviderReference, paid.ConfirmedByUserId));
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid && a.CreatedAt >= t.Quotation.IssuedAt));
        Assert.Equal(450_000m, await ctx.Db.PaymentAttemptItems.Where(i => i.PaymentAttemptId == attemptId && i.Status == PaymentItemStatuses.Settled).SumAsync(i => i.Amount));
        var receipts = await ctx.Db.CashReceiptSubmissions.AsNoTracking().ToListAsync();
        Assert.Equal([CashReceiptStatuses.Accepted, CashReceiptStatuses.Rejected], receipts.Select(r => r.Status).Order());
        Assert.All(receipts, r => Assert.Equal((CashReceiptKinds.ServiceQuotation, t.H.TechnicianId), (r.Kind, r.CollectedByUserId)));
    }

    [Fact]
    public async Task E2E_one_time_maintenance_cash_receipt_then_maintenance_visit_and_customer_acceptance()
    {
        await using var ctx = Create();
        var m = await OneTimeMaintenanceTests.InspectedAsync(ctx);
        var q = await OneTimeMaintenanceTests.PricedAsync(ctx, m);
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, m.H.TechnicianId, q.QuotationId);
        var receipt = await UnifiedCashReceiptTests.SentAsync(ctx, m.H.TechnicianId, q.QuotationId, attemptId);
        Assert.True((await ctx.Payments().AcceptCashReceiptAsync(m.H.ManagerId, receipt.CashReceiptId, receipt.Revision, null, None)).Succeeded);
        var visit = await ctx.VisitStartedAsync(m.H, m.RequestId, ctx.Clock.GetUtcNow().AddDays(2));
        Assert.True((await ctx.Technician().ReportWorkAsync(m.H.TechnicianId, visit, "Cleaned and fixed", m.Items.Select(i => new ItemResultInput(i, "done", null)).ToList(), [], null, None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.Completed, (await ctx.Customer().DecideCompletionAsync(m.H.CustomerId, m.RequestId, true, null, null, None)).Value!.Status);
        Assert.Equal(QuotationStatuses.Paid, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(x => x.Id == q.QuotationId)).Status);
    }

    [Fact]
    public async Task E2E_partial_disaster_coverage_customer_part_in_cash_then_repair_consumes_the_reserved_support()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection));
        ctx.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var fault = await ctx.FaultAsync(h, "Storm broke panels", disasterType: DisasterTypes.Storm, incidentAt: ctx.Clock.GetUtcNow().AddHours(-2));
        Assert.True((await ctx.Manager().DecideClaimAsync(h.ManagerId, fault.RequestId, true, "confirmed", None)).Succeeded);
        var items = await ctx.InspectedAsync(h, fault.RequestId, ctx.Clock.GetUtcNow().AddDays(2), new DiagnosedItemInput(null, "Panels", "cracked", null));
        await ctx.DecideAsync(h, fault.RequestId, (items[0], ItemCoverages.DisasterProtection));
        var panel = await CatalogAsync(ctx, "PNL-E2E", ProductTypes.SolarPanel, 2_000_000m);
        var q = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, fault.RequestId,
            new TechnicianQuotationInput([Line(items[0], (panel.Id, 2))], null, null), None)).Value!.Quotations);
        var attemptId = await UnifiedCashReceiptTests.CashOpenedAsync(ctx, h.TechnicianId, q.QuotationId);
        var receipt = await UnifiedCashReceiptTests.SentAsync(ctx, h.TechnicianId, q.QuotationId, attemptId);
        Assert.Equal(1_200_000m, receipt.Amount);
        Assert.True((await ctx.Payments().AcceptCashReceiptAsync(h.ManagerId, receipt.CashReceiptId, receipt.Revision, null, None)).Succeeded);

        var repair = await ctx.VisitStartedAsync(h, fault.RequestId, ctx.Clock.GetUtcNow().AddDays(3));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Panels replaced", [new ItemResultInput(items[0], "ok", null)], [], null, None)).Succeeded);
        Assert.True((await ctx.Customer().DecideCompletionAsync(h.CustomerId, fault.RequestId, true, null, null, None)).Succeeded);
        var consumed = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id);
        Assert.Equal((0m, 2_800_000m), (consumed.SupportReserved, consumed.SupportConsumed));   // coverage ledger unchanged by the cash path
        Assert.Empty(await ctx.Db.AfterSalesEvents.Where(e => e.EventType == AfterSalesWorkflow.SupportLedgerMismatch).ToListAsync());
    }
}
