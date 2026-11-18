using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SimplifiedFlowFixture;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// 2026-11-17 simplified flow, quotation part: only the technician who completed the survey builds the quotation as a cart of catalog items;
/// prices are catalog snapshots, totals are computed by the backend, no Sales owner is needed, and nothing changes after issue or acceptance.
/// </summary>
public sealed class TechnicianCartQuotationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Only_the_technician_who_completed_the_survey_can_build_read_or_issue_the_cart()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await DraftAsync(ctx);
        var other = await ctx.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);
        var q = ctx.Quotations();
        var id = cart.Quotation.QuotationId;

        foreach (var stranger in new[] { other.Id, cart.Survey.Sales.Id, cart.CustomerId, Guid.NewGuid() })
        {
            Assert.Equal(QuotationErrorCodes.NotAssignedTechnician, (await q.CreateCartAsync(stranger, cart.RequestId, null, None)).ErrorCode);
            Assert.Equal(QuotationErrorCodes.NotAssignedTechnician, (await q.ListForTechnicianAsync(stranger, cart.RequestId, None)).ErrorCode);
            Assert.Equal(QuotationErrorCodes.NotFound, (await q.GetForTechnicianAsync(stranger, id, None)).ErrorCode);
            Assert.Equal(QuotationErrorCodes.NotFound, (await q.AddCartLineAsync(stranger, id, cart.Quotation.Revision, cart.Catalog.Panel.Id, 1m, None)).ErrorCode);
            Assert.Equal(QuotationErrorCodes.NotFound, (await q.IssueCartAsync(stranger, id, cart.Quotation.Revision, None)).ErrorCode);
        }
        Assert.Equal(404, OperationResultControllerBase.StatusFor(QuotationErrorCodes.NotAssignedTechnician));
        // The owner sees it; nothing above changed it.
        var own = (await q.GetForTechnicianAsync(cart.TechnicianId, id, None)).Value!;
        Assert.Equal(cart.Quotation.Revision, own.Revision);
        Assert.Empty(own.Lines);
        Assert.Equal(QuotationOrigins.Technician, own.Origin);
    }

    [Fact]
    public async Task A_cart_needs_a_completed_feasible_survey()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var survey = await ctx.ConfirmedSurveyAsync();
        var q = ctx.Quotations();
        // Not surveyed yet: there is no report for anyone (404 for the caller).
        Assert.Equal(QuotationErrorCodes.NotAssignedTechnician, (await q.CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, null, None)).ErrorCode);

        await OnSiteSurveyTests.CompleteAsync(ctx, survey);
        await ctx.Db.OnSiteSurveyReports.Where(r => r.SurveyRequestId == survey.Request.Request.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.InstallationFeasibility, "NOT_FEASIBLE"));
        Assert.Equal(QuotationErrorCodes.SurveyNotFeasible, (await q.CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, null, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.Quotations.CountAsync());
    }

    [Fact]
    public async Task Cart_lines_use_catalog_prices_merge_repeated_products_and_totals_are_computed_by_the_backend()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = 8m;
        var cart = await DraftAsync(ctx);
        var q = ctx.Quotations();
        var v = cart.Quotation;

        v = (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, cart.Catalog.Panel.Id, 2m, None)).Value!;
        v = (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, cart.Catalog.Panel.Id, 1m, None)).Value!;   // same product: merged
        v = (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, cart.Catalog.Labor.Id, 1m, None)).Value!;
        var panel = Assert.Single(v.Lines, l => l.ProductId == cart.Catalog.Panel.Id);
        Assert.Equal(3m, panel.Quantity);
        Assert.Equal(3_000_000m, panel.UnitPrice);
        Assert.Equal(3_000_000m, panel.CatalogUnitPrice);
        Assert.Equal(9_000_000m, panel.LineTotal);
        Assert.Equal("INSTALLATION_LABOR", Assert.Single(v.Lines, l => l.ProductId == cart.Catalog.Labor.Id).Description);
        // Every cart line (equipment and service items) is in the items subtotal; no other cost, no discount; tax from the configuration.
        Assert.Equal(11_000_000m, v.EquipmentSubtotal);
        Assert.Equal((0m, 0m, 0m, 0m), (v.LaborCost, v.TransportCost, v.OtherServiceCost, v.DiscountAmount));
        Assert.Equal(11_000_000m, v.Subtotal);
        Assert.Equal(880_000m, v.TaxAmount);
        Assert.Equal(11_880_000m, v.TotalAmount);

        var updated = (await q.UpdateCartLineAsync(cart.TechnicianId, v.QuotationId, panel.LineId, v.Revision, 1m, None)).Value!;
        Assert.Equal(3_000_000m, Assert.Single(updated.Lines, l => l.ProductId == cart.Catalog.Panel.Id).LineTotal);
        var removed = (await q.RemoveCartLineAsync(cart.TechnicianId, v.QuotationId, panel.LineId, updated.Revision, None)).Value!;
        Assert.DoesNotContain(removed.Lines, l => l.ProductId == cart.Catalog.Panel.Id);
        Assert.Equal(2_160_000m, removed.TotalAmount);
    }

    [Fact]
    public async Task Invalid_quantities_unknown_inactive_or_unpriced_products_and_stale_revisions_are_refused()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await DraftAsync(ctx);
        var q = ctx.Quotations();
        var v = cart.Quotation;
        var unpriced = await ctx.ProductAsync("NO-PRICE", 0m);
        var inactive = await ctx.ProductAsync("INACTIVE", 1_000_000m);
        await ctx.Db.Products.Where(p => p.Id == inactive.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, SmartSolar.Modules.Catalog.Enums.ProductStatus.Inactive));

        foreach (var quantity in new[] { 0m, -1m, 100_001m, 1.0005m })
            Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, cart.Catalog.Panel.Id, quantity, None)).ErrorCode);
        var missing = await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, unpriced.Id, 1m, None);
        Assert.Equal(QuotationErrorCodes.PriceMissing, missing.ErrorCode);
        Assert.Equal(400, OperationResultControllerBase.StatusFor(missing.ErrorCode!));
        Assert.Equal(QuotationErrorCodes.ProductUnavailable, (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, inactive.Id, 1m, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.ProductUnavailable, (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, Guid.NewGuid(), 1m, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.StaleRevision, (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision + 1, cart.Catalog.Panel.Id, 1m, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.Empty, (await q.IssueCartAsync(cart.TechnicianId, v.QuotationId, v.Revision, None)).ErrorCode);
        Assert.Equal(0, await ctx.Db.QuotationLines.CountAsync());
    }

    [Fact]
    public async Task Prices_are_snapshotted_from_the_catalog_at_issue_and_never_change_afterwards()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = 0m;
        var cart = await DraftAsync(ctx);
        var q = ctx.Quotations();
        var v = (await q.AddCartLineAsync(cart.TechnicianId, cart.Quotation.QuotationId, cart.Quotation.Revision, cart.Catalog.Panel.Id, 2m, None)).Value!;
        Assert.Equal(6_000_000m, v.TotalAmount);

        // The catalog price changes while the cart is a draft: the issue snapshots today's price.
        await ctx.Db.Products.Where(p => p.Id == cart.Catalog.Panel.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UnitPrice, 3_500_000m));
        var issued = (await q.IssueCartAsync(cart.TechnicianId, v.QuotationId, v.Revision, None)).Value!;
        Assert.Equal(QuotationStatuses.Issued, issued.Status);
        Assert.Equal(7_000_000m, issued.TotalAmount);
        Assert.Equal(3_500_000m, Assert.Single(issued.Lines).UnitPrice);
        Assert.NotNull(issued.ValidUntil);
        Assert.True(issued.CanAccept);

        // A later catalog change never alters the issued offer.
        await ctx.Db.Products.Where(p => p.Id == cart.Catalog.Panel.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UnitPrice, 9_000_000m));
        var shown = (await q.GetForTechnicianAsync(cart.TechnicianId, issued.QuotationId, None)).Value!;
        Assert.Equal(7_000_000m, shown.TotalAmount);
        Assert.Equal(3_500_000m, Assert.Single(shown.Lines).UnitPrice);
        // Finalizing sends nothing to the customer's app: the technician presents it on site.
        Assert.Equal(0, await ctx.Db.AfterSalesNotifications.CountAsync(n => n.RecipientUserId == cart.CustomerId && n.Type.Contains("QUOTATION")));
    }

    [Fact]
    public async Task An_item_that_lost_its_catalog_price_blocks_the_issue_and_is_named()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await DraftAsync(ctx);
        var q = ctx.Quotations();
        var v = (await q.AddCartLineAsync(cart.TechnicianId, cart.Quotation.QuotationId, cart.Quotation.Revision, cart.Catalog.Inverter.Id, 1m, None)).Value!;
        await ctx.Db.Products.Where(p => p.Id == cart.Catalog.Inverter.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.UnitPrice, 0m));

        var refused = await q.IssueCartAsync(cart.TechnicianId, v.QuotationId, v.Revision, None);
        Assert.Equal(QuotationErrorCodes.PriceMissing, refused.ErrorCode);
        Assert.Contains(cart.Catalog.Inverter.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(refused.Details));
        Assert.Equal(QuotationStatuses.Draft, (await ctx.Db.Quotations.SingleAsync()).Status);
    }

    [Fact]
    public async Task No_sales_owner_is_needed_from_cart_to_paid_contract()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx);
        var survey = await ctx.QuotableSurveyAsync();
        await ctx.Db.SurveyRequests.Where(r => r.Id == survey.Request.Request.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.AssignedSaleId, (Guid?)null));
        var q = ctx.Quotations();

        var draft = (await q.CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, null, None)).Value!;
        var v = (await q.AddCartLineAsync(survey.Technician.Id, draft.QuotationId, draft.Revision, catalog.Panel.Id, 4m, None)).Value!;
        var issued = await q.IssueCartAsync(survey.Technician.Id, v.QuotationId, v.Revision, None);
        Assert.True(issued.Succeeded, issued.Message);
        Assert.Null((await ctx.Db.Quotations.SingleAsync()).SalesUserId);
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        await SimplifiedFlowFixture.PaidOnSiteAsync(ctx, new SimplifiedFlowFixture.Cart(survey, catalog, issued.Value!), manager);
        Assert.Null((await ctx.Db.CustomerContracts.SingleAsync()).SalesUserId);
    }

    [Fact]
    public async Task Only_the_surveying_technician_reads_or_decides_the_quotation()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);
        var other = await ctx.Scheduling.SeedTechnicianAsync("Other technician", SchedulingTestContext.BinhTan);
        var q = ctx.Quotations();

        Assert.Equal(QuotationErrorCodes.NotFound, (await q.GetForTechnicianAsync(other.Id, cart.Quotation.QuotationId, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.RecordOnSiteDecisionAsync(other.Id, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, cart.Quotation.Revision, null), None)).ErrorCode);
        // Nobody else can record the decision in the customer's name either: the customer's own id is not a technician of the survey.
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.RecordOnSiteDecisionAsync(cart.CustomerId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, cart.Quotation.Revision, null), None)).ErrorCode);
        Assert.Equal(QuotationStatuses.Issued, (await ctx.Db.Quotations.SingleAsync()).Status);
        Assert.Equal(0, await ctx.Db.CustomerContracts.CountAsync());
    }

    [Fact]
    public async Task Issued_and_accepted_quotations_cannot_be_edited_or_revised()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);
        var q = ctx.Quotations();
        var v = cart.Quotation;
        var line = v.Lines[0];

        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, v.Revision, cart.Catalog.Panel.Id, 1m, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.UpdateCartLineAsync(cart.TechnicianId, v.QuotationId, line.LineId, v.Revision, 5m, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.RemoveCartLineAsync(cart.TechnicianId, v.QuotationId, line.LineId, v.Revision, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.IssueCartAsync(cart.TechnicianId, v.QuotationId, v.Revision, None)).ErrorCode);

        var agreed = await AgreedAsync(ctx, cart);
        Assert.Equal(QuotationErrorCodes.NotDraft, (await q.AddCartLineAsync(cart.TechnicianId, v.QuotationId, agreed.Revision, cart.Catalog.Panel.Id, 1m, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.CannotRevise, (await q.CreateCartRevisionAsync(cart.TechnicianId, v.QuotationId, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.AlreadyAccepted, (await q.RecordOnSiteDecisionAsync(cart.TechnicianId, v.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, agreed.Revision, null), None)).ErrorCode);
        var stored = await ctx.Db.QuotationLines.AsNoTracking().Where(l => l.QuotationId == v.QuotationId).OrderBy(l => l.LineNumber).ToListAsync();
        Assert.Equal(v.Lines.Select(l => (l.Quantity, l.UnitPrice)), stored.Select(l => (l.Quantity, l.UnitPrice)));
        Assert.Equal(v.TotalAmount, (await ctx.Db.Quotations.SingleAsync()).TotalAmount);
    }

    [Fact]
    public async Task Changes_asked_on_site_are_a_new_technician_version_that_supersedes_the_old_one()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var cart = await IssuedAsync(ctx);
        var q = ctx.Quotations();
        Assert.True((await q.RecordOnSiteDecisionAsync(cart.TechnicianId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.RevisionNeeded, cart.Quotation.Revision, "Only one transport trip please"), None)).Succeeded);

        var draft = (await q.CreateCartRevisionAsync(cart.TechnicianId, cart.Quotation.QuotationId, None)).Value!;
        Assert.Equal(2, draft.Version);
        Assert.Equal(cart.Quotation.QuoteNumber, draft.QuoteNumber);
        Assert.Equal(cart.Quotation.Lines.Count, draft.Lines.Count);
        Assert.Equal(QuotationErrorCodes.DraftExists, (await q.CreateCartRevisionAsync(cart.TechnicianId, cart.Quotation.QuotationId, None)).ErrorCode);
        var transport = draft.Lines.Single(l => l.ProductId == cart.Catalog.Transport.Id);
        var trimmed = (await q.RemoveCartLineAsync(cart.TechnicianId, draft.QuotationId, transport.LineId, draft.Revision, None)).Value!;
        var v2 = (await q.IssueCartAsync(cart.TechnicianId, trimmed.QuotationId, trimmed.Revision, None)).Value!;

        Assert.Equal(QuotationStatuses.Issued, v2.Status);
        Assert.Equal(QuotationStatuses.Superseded, (await ctx.Db.Quotations.SingleAsync(x => x.Id == cart.Quotation.QuotationId)).Status);
        Assert.Equal(cart.Quotation.Subtotal - 500_000m, v2.Subtotal);
        var old = (await q.GetForTechnicianAsync(cart.TechnicianId, cart.Quotation.QuotationId, None)).Value!;
        Assert.Equal(QuotationErrorCodes.NotIssued, (await q.RecordOnSiteDecisionAsync(cart.TechnicianId, cart.Quotation.QuotationId,
            new OnSiteDecisionInput(OnSiteDecisions.Agreed, old.Revision, null), None)).ErrorCode);
        Assert.True((await q.RecordOnSiteDecisionAsync(cart.TechnicianId, v2.QuotationId, new OnSiteDecisionInput(OnSiteDecisions.Agreed, v2.Revision, null), None)).Succeeded);
    }

    [Fact]
    public async Task A_legacy_sales_draft_is_closed_with_a_reason_when_the_technician_starts_the_cart()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var survey = await ctx.QuotableSurveyAsync();
        var legacy = await ctx.Quotations().CreateAsync(survey.Sales.Id, survey.Request.Request.Id,
            new QuotationHeaderInput(1_000_000m, 0m, 0m, 0m, 8m, null, null, null), None);
        Assert.True(legacy.Succeeded, legacy.Message);

        var cart = await ctx.Quotations().CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, null, None);
        Assert.True(cart.Succeeded, cart.Message);
        var old = await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == legacy.Value!.QuotationId);
        Assert.Equal(QuotationStatuses.Cancelled, old.Status);
        Assert.StartsWith("LEGACY_DRAFT_REPLACED", old.CloseReason);
        Assert.Equal(survey.Technician.Id, old.ClosedByUserId);
        Assert.Equal(2, cart.Value!.Version);
        // Idempotent: asking again returns the same technician draft.
        Assert.Equal(cart.Value.QuotationId, (await ctx.Quotations().CreateCartAsync(survey.Technician.Id, survey.Request.Request.Id, null, None)).Value!.QuotationId);
        Assert.Equal(2, await ctx.Db.Quotations.CountAsync());
    }
}
