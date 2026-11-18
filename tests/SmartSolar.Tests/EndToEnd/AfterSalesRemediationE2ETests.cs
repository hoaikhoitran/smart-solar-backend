using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// After-sales journeys of the 2026-11-18 backend remediation on the production handlers (one SQLite database; PayOS and S3 verification are the only
/// fakes): customer fault → technician inspection → Manager coverage → technician-priced quotation → customer acceptance → cash or PayOS → repair →
/// customer acceptance → COMPLETED. Fully covered work completes without any payment; partially covered work charges only the chargeable item.
/// </summary>
public sealed class AfterSalesRemediationE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Repair visit → work report for the given items → customer accepts → COMPLETED.</summary>
    private static async Task RepairAndCloseAsync(AfterSalesTestContext ctx, HandedOver h, Guid requestId, IReadOnlyList<Guid> items)
    {
        var repair = await ctx.VisitStartedAsync(h, requestId, ctx.Clock.GetUtcNow().AddDays(2));
        var reported = await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Repaired",
            items.Select(i => new ItemResultInput(i, "repaired", null)).ToList(), [], null, None);
        Assert.True(reported.Succeeded, reported.Message);
        Assert.Equal(ServiceRequestStatuses.PendingCustomerAcceptance, (await ctx.Customer().GetAsync(h.CustomerId, requestId, None)).Value!.Status);
        var done = await ctx.Customer().DecideCompletionAsync(h.CustomerId, requestId, true, null, null, None);
        Assert.True(done.Succeeded, done.Message);
        Assert.Equal(ServiceRequestStatuses.Completed, done.Value!.Status);
    }

    private static async Task<(HandedOver H, Guid RequestId, IReadOnlyList<Guid> Items, ServiceQuotationView Quotation)> QuotedAsync(AfterSalesTestContext ctx,
        params string[] coverages)
    {
        var (h, requestId, items) = await DecidedAsync(ctx, coverages);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var labor = await CatalogAsync(ctx, "SVC-REPAIR", SmartSolar.Modules.Catalog.Constants.ProductTypes.OtherService, 300_000m, "JOB");
        var chargeable = items.Where((_, i) => coverages[i] == ItemCoverages.Chargeable).ToList();
        var issued = await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput(chargeable.Select(i => Line(i, (breaker.Id, 1), (labor.Id, 1))).ToList(), null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        var q = Assert.Single(issued.Value!.Quotations);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        return (h, requestId, items, q);
    }

    [Fact]
    public async Task E2E_AS_01_Technician_priced_repair_paid_in_cash_is_repaired_and_closed()
    {
        await using var ctx = Create();
        var (h, requestId, items, q) = await QuotedAsync(ctx, ItemCoverages.Chargeable);
        Assert.Equal(450_000m, q.CustomerAmount);

        var cash = (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).Value!;
        var evidence = await ctx.Installation.VerifiedFileAsync(h.ManagerId, FilePurposes.PaymentEvidence);
        Assert.True((await ctx.Payments().ConfirmCashAttemptAsync(h.ManagerId, cash.PaymentAttemptId, new(450_000m, "PT-E2E-AS1", evidence, null), None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);

        await RepairAndCloseAsync(ctx, h, requestId, items);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid));
    }

    [Fact]
    public async Task E2E_AS_02_Technician_priced_repair_paid_with_PayOS_is_repaired_and_closed_and_a_duplicate_webhook_changes_nothing()
    {
        await using var ctx = Create();
        var (h, requestId, items, q) = await QuotedAsync(ctx, ItemCoverages.Chargeable);

        var link = (await ctx.Quotations().CheckoutAsync(h.CustomerId, q.QuotationId, None)).Value!;
        var webhook = ctx.Installation.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-E2E-AS2");
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);

        await RepairAndCloseAsync(ctx, h, requestId, items);
        Assert.Equal(1, await ctx.Db.PaymentAttemptItems.CountAsync(i => i.QuotationId == q.QuotationId && i.Status == PaymentItemStatuses.Settled));
    }

    [Fact]
    public async Task E2E_AS_03_Fully_covered_warranty_repair_is_closed_without_any_quotation_or_payment()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.InstallationWarranty);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);

        await RepairAndCloseAsync(ctx, h, requestId, items);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));
    }

    [Fact]
    public async Task E2E_AS_04_Partially_covered_repair_charges_only_the_chargeable_item_and_both_are_repaired()
    {
        await using var ctx = Create();
        var (h, requestId, items, q) = await QuotedAsync(ctx, ItemCoverages.InstallationWarranty, ItemCoverages.Chargeable);
        Assert.Equal(450_000m, q.CustomerAmount);   // one item only: 150,000 part + 300,000 labor

        var link = (await ctx.Quotations().CheckoutAsync(h.CustomerId, q.QuotationId, None)).Value!;
        Assert.Equal(450_000m, link.Amount);
        await ctx.PayAsync(link);
        await RepairAndCloseAsync(ctx, h, requestId, items);
    }

    [Fact]
    public async Task E2E_AS_05_An_expired_or_rejected_quotation_never_leads_to_work()
    {
        await using var ctx = Create();
        var (h, requestId, _, q) = await QuotedAsync(ctx, ItemCoverages.Chargeable);
        ctx.Clock.Now = q.ExpiresAt.AddMinutes(5).ToUniversalTime();
        Assert.Equal(AfterSalesErrorCodes.QuotationExpired, (await ctx.Quotations().CheckoutAsync(h.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.QuotationExpired, (await ctx.Quotations().SelectCashAsync(h.CustomerId, q.QuotationId, None)).ErrorCode);
        await ctx.Sweep().RunOnceAsync(None);
        Assert.Equal(QuotationStatuses.Expired, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.NotEqual(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid));
    }

    /// <summary>MAINT-015: the complete technician-priced one-time maintenance, request → inspection → quotation → cash → visit → acceptance → COMPLETED.</summary>
    [Fact]
    public async Task E2E_MAINT_015_Technician_priced_one_time_maintenance_runs_from_request_to_closure()
    {
        await using var ctx = Create();
        var m = await SmartSolar.Tests.Unit.AfterSales.OneTimeMaintenanceTests.InspectedAsync(ctx);
        var q = await SmartSolar.Tests.Unit.AfterSales.OneTimeMaintenanceTests.PricedAsync(ctx, m);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(m.H.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        var cash = (await ctx.Quotations().SelectCashAsync(m.H.CustomerId, q.QuotationId, None)).Value!;
        var evidence = await ctx.Installation.VerifiedFileAsync(m.H.ManagerId, FilePurposes.PaymentEvidence);
        Assert.True((await ctx.Payments().ConfirmCashAttemptAsync(m.H.ManagerId, cash.PaymentAttemptId, new(q.CustomerAmount, "PT-MAINT", evidence, null), None)).Succeeded);

        await RepairAndCloseAsync(ctx, m.H, m.RequestId, m.Items);

        var closed = (await ctx.Customer().GetAsync(m.H.CustomerId, m.RequestId, None)).Value!;
        Assert.Equal((ServiceRequestStatuses.Completed, ServiceOutcomes.MaintenanceDone), (closed.Status, closed.Outcome));
        Assert.Equal(2, closed.Visits.Count);   // inspection + maintenance
        Assert.Equal(new[] { VisitPurposes.Inspection, VisitPurposes.Maintenance }, closed.Visits.Select(v => v.Purpose).ToArray());
        Assert.All(await ctx.Db.ServiceItems.AsNoTracking().ToListAsync(), i => Assert.Equal(ItemStatuses.Done, i.Status));
        Assert.Equal(0, await ctx.Db.SalesTasks.CountAsync(t => t.Status == SalesTaskStatuses.Open));
    }
}
