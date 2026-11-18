using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// AFTER-QUOTE (2026-11-18): the technician of a visit of a FAULT request prices its chargeable / disaster-protected items with catalog products
/// (replacement parts and priced labor); the backend computes every amount and snapshots the products. Coverage stays the Manager's decision.
/// </summary>
public sealed class TechnicianPricedQuotationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static DiagnosedItemInput Item(string description) => new(null, description, $"{description}: diagnosis", null);

    internal static async Task<Product> CatalogAsync(AfterSalesTestContext ctx, string sku, string productType, decimal price, string unit = "piece",
        ProductStatus status = ProductStatus.Active)
    {
        var product = PreSurveyTestContext.NewPanelProduct(sku);
        product.ProductType = productType;
        product.Unit = unit;
        product.UnitPrice = price;
        product.Status = status;
        ctx.Db.Products.Add(product);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return product;
    }

    internal static TechnicianQuotationLineInput Line(Guid itemId, params (Guid Product, decimal Quantity)[] components)
        => new(itemId, components.Select(c => new QuotationComponentInput(c.Product, c.Quantity)).ToList(), null);

    /// <summary>A handed-over installation with a FAULT request inspected by the installation's technician and decided by the Manager.</summary>
    internal static async Task<(HandedOver H, Guid RequestId, IReadOnlyList<Guid> Items)> DecidedAsync(AfterSalesTestContext ctx, params string[] coverages)
    {
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), coverages.Select((_, i) => Item($"Item {i}")).ToArray());
        await ctx.DecideAsync(h, request.RequestId, coverages.Select((c, i) => (items[i], c)).ToArray());
        return (h, request.RequestId, items);
    }

    [Fact]
    public async Task AFTER_QUOTE_001_The_assigned_technician_prices_items_from_the_catalog_and_the_customer_accepts_and_pays()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var cable = await CatalogAsync(ctx, "CBL-4MM", "CABLE", 25_000m, "m");
        var labor = await CatalogAsync(ctx, "SVC-REPAIR", ProductTypes.OtherService, 300_000m, "JOB");

        var issued = await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (breaker.Id, 2), (labor.Id, 1)), Line(items[1], (cable.Id, 12.5m))], null, "Replace both"), None);

        Assert.True(issued.Succeeded, issued.Message + " " + JsonSerializer.Serialize(issued.Details));
        var q = Assert.Single(issued.Value!.Quotations);
        // Parts: 2 × 150,000 + 12.5 × 25,000 = 612,500; labor: 300,000 (service product) — computed by the backend, whole VND.
        Assert.Equal((612_500m, 300_000m, 912_500m, 912_500m, QuotationIssuers.Technician), (q.PartsAmount, q.LaborAmount, q.TotalAmount, q.CustomerAmount, q.IssuedByRole));
        Assert.Equal(ServiceRequestStatuses.AwaitingCustomer, issued.Value.Status);
        var stored = await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync();
        Assert.Equal((h.TechnicianId, QuotationIssuers.Technician), (stored.SalesUserId, stored.IssuedByRole));
        var components = JsonDocument.Parse(stored.LinesJson).RootElement.EnumerateArray().SelectMany(l => l.GetProperty("components").EnumerateArray()).ToList();
        Assert.Equal(3, components.Count);
        var laborLine = components.Single(c => c.GetProperty("sku").GetString() == "SVC-REPAIR");
        Assert.Equal(("LABOR", 300_000m, 1m), (laborLine.GetProperty("kind").GetString(), laborLine.GetProperty("unitPrice").GetDecimal(), laborLine.GetProperty("quantity").GetDecimal()));
        // The Sales queue task is closed: Sales is not needed any more.
        Assert.DoesNotContain(await ctx.Tasks().ListOpenAsync(None), t => t.RequestId == requestId);

        // The customer reads it, accepts and pays (PayOS); the request is ready for the repair.
        var seen = (await ctx.Customer().GetAsync(h.CustomerId, requestId, None)).Value!;
        Assert.Equal(912_500m, Assert.Single(seen.Quotations).CustomerAmount);
        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).Succeeded);
        var checkout = (await ctx.Quotations().CheckoutAsync(h.CustomerId, q.QuotationId, None)).Value!;
        Assert.Equal(PaymentStatuses.Paid, (await ctx.PayAsync(checkout))!.Status);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
    }

    [Fact]
    public async Task AFTER_QUOTE_002_Missing_or_invalid_catalog_prices_and_invalid_lines_are_refused_and_nothing_is_written()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var unpriced = await CatalogAsync(ctx, "SVC-NOPRICE", ProductTypes.OtherService, 0m, "JOB");
        var inactive = await CatalogAsync(ctx, "BRK-OLD", "BREAKER", 100_000m, status: ProductStatus.Inactive);
        var good = await CatalogAsync(ctx, "BRK-C20", "BREAKER", 120_000m);
        var q = ctx.Quotations();
        TechnicianQuotationInput One(params (Guid, decimal)[] c) => new([Line(items[0], c)], null, null);

        Assert.Equal(AfterSalesErrorCodes.PriceMissing, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, One((unpriced.Id, 1), (good.Id, 1)), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ProductUnavailable, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, One((inactive.Id, 1)), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ProductUnavailable, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, One((Guid.NewGuid(), 1)), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, One((good.Id, 0)), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, One((good.Id, 1), (good.Id, 2)), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, One(), None)).ErrorCode);
        // An item that is not a chargeable item of this diagnosis.
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed,
            (await q.IssueByTechnicianAsync(h.TechnicianId, requestId, new TechnicianQuotationInput([Line(Guid.NewGuid(), (good.Id, 1))], null, null), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
    }

    [Fact]
    public async Task AFTER_QUOTE_003_Only_the_technician_of_a_visit_of_a_fault_request_may_price_it()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var good = await CatalogAsync(ctx, "BRK-C20", "BREAKER", 120_000m);
        var input = new TechnicianQuotationInput([Line(items[0], (good.Id, 1))], null, null);

        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await ctx.Quotations().IssueByTechnicianAsync(h.SalesId, requestId, input, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await ctx.Quotations().IssueByTechnicianAsync(Guid.NewGuid(), requestId, input, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, Guid.NewGuid(), input, None)).ErrorCode);
        // Sales still needs a claimed task (unchanged), and a technician-issued version is not Sales' to supersede.
        Assert.True((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId, input, None)).Succeeded);
        Assert.Equal(CommonErrorCodes.Forbidden, (await ctx.Quotations().IssueAsync(h.SalesId, requestId,
            new IssueQuotationInput([new QuotationLineInput(items[0], null, 1, 1)], null, null), None)).ErrorCode);
    }

    [Fact]
    public async Task AFTER_QUOTE_004_Partial_coverage_prices_only_the_chargeable_item_and_disaster_support_reduces_the_customer_part()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.InstallationWarranty, ItemCoverages.Chargeable);
        var good = await CatalogAsync(ctx, "BRK-C20", "BREAKER", 120_000m);
        var q = ctx.Quotations();

        // The free (warranty) item is never priced.
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await q.IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (good.Id, 1)), Line(items[1], (good.Id, 1))], null, null), None)).ErrorCode);
        var issued = await q.IssueByTechnicianAsync(h.TechnicianId, requestId, new TechnicianQuotationInput([Line(items[1], (good.Id, 1))], null, null), None);
        Assert.True(issued.Succeeded, issued.Message);
        Assert.Equal(120_000m, Assert.Single(issued.Value!.Quotations).CustomerAmount);
        var free = await ctx.Db.ServiceItems.AsNoTracking().SingleAsync(i => i.Id == items[0]);
        Assert.Equal((ItemStatuses.Authorized, (Guid?)null), (free.Status, free.QuotationId));

        // Disaster-protected repair: the support is computed and reserved exactly as for a Sales quotation (70 %, capped).
        await using var storm = Create();
        var hs = await storm.HandedOverAsync();
        var term = await storm.ActiveTermAsync(hs, await storm.PackageAsync(hs.AdminId, ServicePackageKinds.DisasterProtection));
        storm.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var fault = await storm.FaultAsync(hs, "Storm broke panels", disasterType: DisasterTypes.Storm, incidentAt: storm.Clock.GetUtcNow().AddHours(-2));
        Assert.True((await storm.Manager().DecideClaimAsync(hs.ManagerId, fault.RequestId, true, "confirmed", None)).Succeeded);
        var stormItems = await storm.InspectedAsync(hs, fault.RequestId, storm.Clock.GetUtcNow().AddDays(2), Item("Panels"));
        await storm.DecideAsync(hs, fault.RequestId, (stormItems[0], ItemCoverages.DisasterProtection));
        var panel = await CatalogAsync(storm, "PNL-REPL", ProductTypes.SolarPanel, 2_000_000m);
        var supported = await storm.Quotations().IssueByTechnicianAsync(hs.TechnicianId, fault.RequestId,
            new TechnicianQuotationInput([Line(stormItems[0], (panel.Id, 2))], null, null), None);
        var sq = Assert.Single(supported.Value!.Quotations);
        Assert.Equal((4_000_000m, 2_800_000m, 1_200_000m), (sq.TotalAmount, sq.DisasterSupportAmount, sq.CustomerAmount));
        Assert.Equal(2_800_000m, (await storm.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).SupportReserved);
    }

    [Fact]
    public async Task AFTER_QUOTE_005_A_new_version_supersedes_the_open_one_and_keeps_the_old_catalog_snapshot()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var input = new TechnicianQuotationInput([Line(items[0], (breaker.Id, 1))], null, null);
        var v1 = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId, input, None)).Value!.Quotations);

        var tracked = await ctx.Db.Products.SingleAsync(p => p.Id == breaker.Id);
        tracked.UnitPrice = 180_000m;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var v2 = (await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId, input, None)).Value!.Quotations.Single(q => q.Version == 2);

        Assert.Equal((150_000m, 180_000m), (v1.TotalAmount, v2.TotalAmount));
        var old = await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(q => q.Id == v1.QuotationId);
        Assert.Equal((QuotationStatuses.Superseded, 150_000m), (old.Status, old.TotalAmount));
        Assert.Contains("150000", old.LinesJson);
        Assert.Equal(1, await ctx.Db.ServiceQuotations.CountAsync(q => q.Status == QuotationStatuses.Issued));
    }

    [Fact]
    public async Task AFTER_QUOTE_006_An_expired_quotation_cannot_be_accepted_and_a_rejected_one_declines_its_items()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.Chargeable);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);
        var q = Assert.Single((await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (breaker.Id, 1))], null, null), None)).Value!.Quotations);

        var now = ctx.Clock.Now;
        ctx.Clock.Now = q.ExpiresAt.AddMinutes(1).ToUniversalTime();
        Assert.Equal(AfterSalesErrorCodes.QuotationExpired, (await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).ErrorCode);
        ctx.Clock.Now = now;

        Assert.True((await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Declined, "Too expensive", None)).Succeeded);
        Assert.Equal(QuotationStatuses.Rejected, (await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(ItemStatuses.Declined, (await ctx.Db.ServiceItems.AsNoTracking().SingleAsync(i => i.Id == items[0])).Status);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().RecordOnSiteDecisionAsync(h.TechnicianId, q.QuotationId, ServiceOnSiteDecisions.Agreed, null, None)).ErrorCode);
    }

    [Fact]
    public async Task AFTER_QUOTE_007_A_fully_covered_request_needs_no_quotation_and_creates_no_payment()
    {
        await using var ctx = Create();
        var (h, requestId, items) = await DecidedAsync(ctx, ItemCoverages.InstallationWarranty);
        var breaker = await CatalogAsync(ctx, "BRK-C16", "BREAKER", 150_000m);

        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().IssueByTechnicianAsync(h.TechnicianId, requestId,
            new TechnicianQuotationInput([Line(items[0], (breaker.Id, 1))], null, null), None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.ServiceQuotations.CountAsync());
        Assert.Equal(0, await ctx.Db.PaymentAttempts.CountAsync(a => a.ContractId == null));
    }
}
