using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;
using static SmartSolar.Tests.Unit.AfterSales.TechnicianPricedQuotationTests;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// MAINT (2026-11-18): technician-priced one-time maintenance. Request (SUBMITTED) → Manager assigns an INSPECTION visit (technician before any payment)
/// → technician records the work items → Manager decides coverage → the technician prices them from the catalog → customer accepts / rejects → cash or
/// PayOS → maintenance visit → report → customer acceptance → COMPLETED. Package maintenance (prepaid entitlement) is never quoted. The legacy
/// Sales-priced mode stays available only explicitly (<c>inspectionRequired: false</c>).
/// </summary>
public sealed class OneTimeMaintenanceTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    internal sealed record Inspected(HandedOver H, Guid RequestId, IReadOnlyList<Guid> Items);

    /// <summary>Requested → inspection visit by the installation technician → two work items → Manager: chargeable.</summary>
    internal static async Task<Inspected> InspectedAsync(AfterSalesTestContext ctx, int days = 10)
    {
        var h = await ctx.HandedOverAsync();
        var requested = await ctx.Customer().RequestOneTimeMaintenanceAsync(h.CustomerId, h.ProjectId, Later(days), "Yearly clean and check", None);
        Assert.True(requested.Succeeded, requested.Message);
        var items = await ctx.InspectedAsync(h, requested.Value!.RequestId, Later(days),
            new DiagnosedItemInput(null, "Panel cleaning", "Dust on 12 panels", null), new DiagnosedItemInput(null, "Connector check", "Loose MC4", null));
        await ctx.DecideAsync(h, requested.Value.RequestId, (items[0], ItemCoverages.Chargeable), (items[1], ItemCoverages.Chargeable));
        return new Inspected(h, requested.Value.RequestId, items);
    }

    internal static async Task<ServiceQuotationView> PricedAsync(AfterSalesTestContext ctx, Inspected m)
    {
        var cleaning = await CatalogAsync(ctx, "SVC-CLEAN", ProductTypes.OtherService, 50_000m, "panel");
        var connector = await CatalogAsync(ctx, "MC4-PAIR", "CONNECTOR", 40_000m, "pair");
        var labor = await CatalogAsync(ctx, "SVC-HOUR", ProductTypes.OtherService, 200_000m, "hour");
        var issued = await ctx.Quotations().IssueByTechnicianAsync(m.H.TechnicianId, m.RequestId, new TechnicianQuotationInput(
            [Line(m.Items[0], (cleaning.Id, 12)), Line(m.Items[1], (connector.Id, 2), (labor.Id, 1))], null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        return Assert.Single(issued.Value!.Quotations);
    }

    [Fact]
    public async Task MAINT_001_A_valid_request_waits_for_an_inspection_without_any_Sales_task()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var customer = ctx.Customer();

        var requested = await customer.RequestOneTimeMaintenanceAsync(h.CustomerId, h.ProjectId, Later(10), "Yearly clean", None);
        Assert.True(requested.Succeeded, requested.Message);
        Assert.Equal((ServiceRequestKinds.OneTimeMaintenance, ServiceRequestStatuses.Submitted), (requested.Value!.Kind, requested.Value.Status));
        Assert.Empty(await ctx.Tasks().ListOpenAsync(None));
        var created = await ctx.Db.AfterSalesEvents.AsNoTracking().SingleAsync(e => e.SubjectId == requested.Value.RequestId && e.EventType == "ONE_TIME_MAINTENANCE_REQUESTED");
        Assert.Contains("inspection and technician pricing", created.Detail);
        // Validation / ownership: too soon, someone else's installation.
        Assert.Equal(AfterSalesErrorCodes.BookingTooSoon, (await customer.RequestOneTimeMaintenanceAsync(h.CustomerId, h.ProjectId, Later(2), null, None)).ErrorCode);
        Assert.False((await customer.RequestOneTimeMaintenanceAsync(Guid.NewGuid(), h.ProjectId, Later(10), null, None)).Succeeded);
        // The legacy Sales-priced mode is only used when asked for explicitly.
        var legacy = await customer.RequestOneTimeMaintenanceAsync(h.CustomerId, h.ProjectId, Later(12), "no inspection", None, inspectionRequired: false);
        Assert.Equal(ServiceRequestStatuses.AwaitingQuotation, legacy.Value!.Status);
        Assert.Single(await ctx.Tasks().ListOpenAsync(None), t => t.TaskType == SalesTaskTypes.MaintenanceQuotation);
    }

    [Fact]
    public async Task MAINT_002_A_technician_is_assigned_for_the_inspection_before_any_payment()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = (await ctx.Customer().RequestOneTimeMaintenanceAsync(h.CustomerId, h.ProjectId, Later(10), "Yearly clean", None)).Value!;
        var revision = (await ctx.Manager().GetAsync(request.RequestId, None)).Value!.Revision;

        var scheduled = await ctx.Manager().ScheduleVisitAsync(h.ManagerId, request.RequestId, new ScheduleVisitInput(h.TechnicianId, Later(10), null, null, revision), None);

        Assert.True(scheduled.Succeeded, scheduled.Message);
        var visit = Assert.Single(scheduled.Value!.Visits);
        Assert.Equal((VisitPurposes.Inspection, h.TechnicianId), (visit.Purpose, visit.TechnicianUserId!.Value));
        Assert.Equal(ServiceRequestStatuses.Scheduled, scheduled.Value.Status);
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
    }

    [Fact]
    public async Task MAINT_003_The_inspecting_technician_issues_a_catalog_priced_quotation()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        Assert.Equal(ServiceRequestStatuses.AwaitingQuotation, (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Status);

        var q = await PricedAsync(ctx, m);

        // 12 × 50,000 cleaning (labor) + 2 × 40,000 connectors (parts) + 1 × 200,000 hour (labor).
        Assert.Equal((80_000m, 800_000m, 880_000m, QuotationIssuers.Technician), (q.PartsAmount, q.LaborAmount, q.CustomerAmount, q.IssuedByRole));
        Assert.Null(q.RepairWarrantyMonths);   // a maintenance carries no repair warranty
        Assert.Equal(ServiceRequestStatuses.AwaitingCustomer, (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Status);
        Assert.Empty(await ctx.Tasks().ListOpenAsync(None));
    }

    [Fact]
    public async Task MAINT_004_Another_technician_or_Sales_cannot_issue_the_quotation()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var product = await CatalogAsync(ctx, "SVC-CLEAN", ProductTypes.OtherService, 50_000m);
        var input = new TechnicianQuotationInput([Line(m.Items[0], (product.Id, 1)), Line(m.Items[1], (product.Id, 1))], null, null);
        var stranger = await ctx.Installation.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);

        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await ctx.Quotations().IssueByTechnicianAsync(stranger.Id, m.RequestId, input, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.Forbidden, (await ctx.Quotations().IssueAsync(m.H.SalesId, m.RequestId,
            new IssueQuotationInput([new QuotationLineInput(m.Items[0], null, 1, 0), new QuotationLineInput(m.Items[1], null, 1, 0)], null, null), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
    }

    [Fact]
    public async Task MAINT_005_A_missing_catalog_price_prevents_the_quotation()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var unpriced = await CatalogAsync(ctx, "SVC-NOPRICE", ProductTypes.OtherService, 0m, "JOB");
        var priced = await CatalogAsync(ctx, "SVC-CLEAN", ProductTypes.OtherService, 50_000m);

        var refused = await ctx.Quotations().IssueByTechnicianAsync(m.H.TechnicianId, m.RequestId,
            new TechnicianQuotationInput([Line(m.Items[0], (priced.Id, 1)), Line(m.Items[1], (unpriced.Id, 1))], null, null), None);

        Assert.Equal(AfterSalesErrorCodes.PriceMissing, refused.ErrorCode);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
    }

    [Fact]
    public async Task MAINT_006_007_The_customer_accepts_or_rejects_the_quotation()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var q = await PricedAsync(ctx, m);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(m.H.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);

        await using var other = Create();
        var r = await InspectedAsync(other);
        var rq = await PricedAsync(other, r);
        var rejected = await other.Quotations().RecordOnSiteDecisionAsync(r.H.TechnicianId, rq.QuotationId, ServiceOnSiteDecisions.Declined, "Too expensive", None);
        Assert.True(rejected.Succeeded, rejected.Message);
        Assert.Equal(ServiceRequestStatuses.Cancelled, rejected.Value!.Status);
        Assert.All(await other.Db.ServiceItems.AsNoTracking().ToListAsync(), i => Assert.Equal(ItemStatuses.Declined, i.Status));
        Assert.Equal(0, await other.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));
    }

    [Fact]
    public async Task MAINT_008_010_Cash_pays_once_and_every_second_payment_is_refused()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var q = await PricedAsync(ctx, m);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(m.H.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        var cash = (await ctx.Quotations().SelectCashAsync(m.H.CustomerId, q.QuotationId, None)).Value!;
        var evidence = await ctx.Installation.VerifiedFileAsync(m.H.ManagerId, FilePurposes.PaymentEvidence);

        Assert.True((await ctx.Payments().ConfirmCashAttemptAsync(m.H.ManagerId, cash.PaymentAttemptId, new(880_000m, "PT-M8", evidence, null), None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Status);

        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await ctx.Payments().ConfirmCashAttemptAsync(m.H.ManagerId, cash.PaymentAttemptId,
            new(880_000m, "PT-M8-2", await ctx.Installation.VerifiedFileAsync(m.H.ManagerId, FilePurposes.PaymentEvidence), null), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().CheckoutAsync(m.H.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await ctx.Quotations().SelectCashAsync(m.H.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(1, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null && a.Status == PaymentStatuses.Paid));
    }

    [Fact]
    public async Task MAINT_009_PayOS_pays_the_quotation_and_a_duplicate_webhook_changes_nothing()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var q = await PricedAsync(ctx, m);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(m.H.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        var link = (await ctx.Quotations().CheckoutAsync(m.H.CustomerId, q.QuotationId, None)).Value!;
        var webhook = ctx.Installation.PayOs.Webhook(link.OrderCode, (long)link.Amount, "FT-M9");

        Assert.Equal(WebhookOutcomes.Applied, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, (await ctx.Payments().HandleWebhookAsync(webhook, None)).Outcome);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Status);
        Assert.Equal(1, await ctx.Db.PaymentAttemptItems.CountAsync(i => i.QuotationId == q.QuotationId && i.Status == PaymentItemStatuses.Settled));
    }

    [Fact]
    public async Task MAINT_011_A_prepaid_package_maintenance_visit_needs_no_quotation_and_no_payment()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, visitMonths: "1,7"));
        var month1 = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id && e.PlanMonth == 1);
        var paidBefore = await ctx.Db.PaymentAttempts.CountAsync();
        var booking = (await ctx.Customer().BookMaintenanceAsync(h.CustomerId, month1.Id, Later(10), null, None)).Value!;

        var visit = await ctx.VisitStartedAsync(h, booking.RequestId, Later(10));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, visit, "Cleaned panels", [], [], null, None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.Completed, (await ctx.Customer().DecideCompletionAsync(h.CustomerId, booking.RequestId, true, null, null, None)).Value!.Status);

        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
        Assert.Equal(paidBefore, await ctx.Db.PaymentAttempts.CountAsync());
        // A technician cannot quote a package visit.
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, booking.RequestId,
            new TechnicianQuotationInput([Line(Guid.NewGuid(), (Guid.NewGuid(), 1))], null, null), None)).ErrorCode);
    }

    [Fact]
    public async Task MAINT_012_Additional_work_found_on_site_is_charged_alone_on_a_supplementary_quotation()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var q = await PricedAsync(ctx, m);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(m.H.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        await ctx.PayAsync((await ctx.Quotations().CheckoutAsync(m.H.CustomerId, q.QuotationId, None)).Value!);

        var visit = await ctx.VisitStartedAsync(m.H, m.RequestId, ctx.Clock.GetUtcNow().AddDays(2));
        var extra = await ctx.Technician().AddSupplementaryItemAsync(m.H.TechnicianId, visit, new DiagnosedItemInput(null, "Cracked junction box", "JB on R2", null), None);
        Assert.True(extra.Succeeded, extra.Message);
        Assert.True((await ctx.Technician().ReportWorkAsync(m.H.TechnicianId, visit, "Cleaned and fixed connectors",
            m.Items.Select(i => new ItemResultInput(i, "done", null)).ToList(), [], null, None)).Succeeded);
        // The extra item waits for its own decision; the request is not offered for acceptance yet.
        Assert.Equal(ServiceRequestStatuses.AwaitingDecision, (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Status);
        var extraItem = (await ctx.Db.ServiceItems.AsNoTracking().SingleAsync(i => i.IsSupplementary)).Id;
        await ctx.DecideAsync(m.H, m.RequestId, (extraItem, ItemCoverages.Chargeable));

        var box = await CatalogAsync(ctx, "JB-IP68", "JUNCTION_BOX", 120_000m);
        var supplementary = await ctx.Quotations().IssueByTechnicianAsync(m.H.TechnicianId, m.RequestId,
            new TechnicianQuotationInput([Line(extraItem, (box.Id, 1))], null, null), None);
        Assert.True(supplementary.Succeeded, supplementary.Message);
        var sq = supplementary.Value!.Quotations.Single(x => x.IsSupplementary);
        Assert.Equal(120_000m, sq.CustomerAmount);   // only the new work; the paid quotation is unchanged
        Assert.Equal((QuotationStatuses.Paid, 880_000m), ((await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(x => x.Id == q.QuotationId)).Status,
            (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(x => x.Id == q.QuotationId)).CustomerAmount));
    }

    [Fact]
    public async Task MAINT_013_An_expired_quotation_cannot_be_paid_and_the_technician_can_price_it_again()
    {
        await using var ctx = Create();
        var m = await InspectedAsync(ctx);
        var q = await PricedAsync(ctx, m);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(m.H.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        var cash = (await ctx.Quotations().SelectCashAsync(m.H.CustomerId, q.QuotationId, None)).Value!;
        var evidence = await ctx.Installation.VerifiedFileAsync(m.H.ManagerId, FilePurposes.PaymentEvidence);

        ctx.Clock.Now = q.ExpiresAt.AddMinutes(1).ToUniversalTime();
        Assert.Equal(PaymentErrorCodes.CashItemNotPayable, (await ctx.Payments().ConfirmCashAttemptAsync(m.H.ManagerId, cash.PaymentAttemptId, new(880_000m, "PT-M13", evidence, null), None)).ErrorCode);
        await ctx.Sweep().RunOnceAsync(None);
        Assert.Equal(QuotationStatuses.Expired, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(ServiceRequestStatuses.AwaitingQuotation, (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Status);

        var again = await ctx.Quotations().IssueByTechnicianAsync(m.H.TechnicianId, m.RequestId, new TechnicianQuotationInput(
            [Line(m.Items[0], ((await ctx.Db.Products.SingleAsync(p => p.Sku == "SVC-CLEAN")).Id, 12)),
             Line(m.Items[1], ((await ctx.Db.Products.SingleAsync(p => p.Sku == "MC4-PAIR")).Id, 2))], null, null), None);
        Assert.True(again.Succeeded, again.Message);
        Assert.Equal(2, await ctx.Db.ServiceQuotations.CountAsync());
    }

    [Fact]
    public async Task MAINT_014_Invalid_transitions_are_refused()
    {
        var ctx = Create();
        await using var first = ctx;
        var h = await ctx.HandedOverAsync();
        var request = (await ctx.Customer().RequestOneTimeMaintenanceAsync(h.CustomerId, h.ProjectId, Later(10), "Yearly clean", None)).Value!;
        var product = await CatalogAsync(ctx, "SVC-CLEAN", ProductTypes.OtherService, 50_000m);

        // No quotation before the inspection (the technician has no visit yet: 404).
        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, request.RequestId,
            new TechnicianQuotationInput([Line(Guid.NewGuid(), (product.Id, 1))], null, null), None)).ErrorCode);
        // No completion before any work.
        Assert.False((await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, true, null, null, None)).Succeeded);

        await using var next = Create();
        var m = await InspectedAsync(next, days: 14);
        var q = await PricedAsync(next, m);
        ctx = next;
        // No maintenance visit before payment, no payment before acceptance.
        var revision = (await ctx.Manager().GetAsync(m.RequestId, None)).Value!.Revision;
        Assert.False((await ctx.Manager().ScheduleVisitAsync(m.H.ManagerId, m.RequestId, new ScheduleVisitInput(m.H.TechnicianId, Later(20), null, null, revision), None)).Succeeded);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().CheckoutAsync(m.H.CustomerId, q.QuotationId, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().SelectCashAsync(m.H.CustomerId, q.QuotationId, None)).ErrorCode);
    }
}
