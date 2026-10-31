using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Automatic STANDARD installation quotation: the technician's bill of materials is priced from the catalog when the survey is
/// completed and issued without Sales; any pricing problem leaves a DRAFT for Sales. All prices are SYNTHETIC fixture values.
/// </summary>
public sealed class AutomaticQuotationTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey, QuotationCatalog Catalog)> ReadyAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = 0m;   // fixture: tax 0 %
        var survey = await ctx.ConfirmedSurveyAsync();
        return (ctx, survey, await AutomaticQuotationFixture.SeedCatalogAsync(ctx));
    }

    [Fact]
    public void Pricing_formula_matches_the_reference_example()
    {
        // 2 panels × 3,000,000 + 1 inverter × 10,000,000; labor 2,000,000; transport 500,000; discount 500,000; tax 0 %.
        var lines = new[] { QuotationPricing.LineTotal(2m, 3_000_000m), QuotationPricing.LineTotal(1m, 10_000_000m) };

        var totals = QuotationPricing.Compute(lines, 2_000_000m, 500_000m, 0m, 500_000m, 0m);

        Assert.Equal((16_000_000m, 18_500_000m, 0m, 18_000_000m), (totals.EquipmentSubtotal, totals.Subtotal, totals.TaxAmount, totals.TotalAmount));
        // Whole-VND rounding of a line and of the tax (8 % on 18,000,000 → 1,440,000; 0.5 rounds away from zero).
        Assert.Equal(3m, QuotationPricing.LineTotal(1m, 2.5m));
        Assert.Equal(1_440_000m, QuotationPricing.Compute([18_000_000m], 0, 0, 0, 0, 8m).TaxAmount);
    }

    [Fact]
    public async Task Completing_the_survey_issues_the_standard_quotation_automatically_with_catalog_snapshots()
    {
        // Arrange
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;

        // Act
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());

        // Assert: issued by the backend (nobody from Sales clicked Issue), priced only from the catalog.
        Assert.True(completed.Succeeded, completed.Message);
        Assert.Equal(AutomaticQuotationOutcomes.Issued, completed.Value!.AutomaticQuotationOutcome);
        var stored = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
        Assert.Equal((QuotationStatuses.Issued, QuotationOrigins.Automatic, s.Sales.Id), (stored.Status, stored.Origin, stored.SalesUserId));
        Assert.Equal((16_000_000m, 2_000_000m, 500_000m, 0m, 18_500_000m, 0m, 18_500_000m),
            (stored.EquipmentSubtotal, stored.LaborCost, stored.TransportCost, stored.DiscountAmount, stored.Subtotal, stored.TaxAmount, stored.TotalAmount));
        var customerView = (await ctx.Quotations().GetForCustomerAsync(s.Request.Customer.Id, stored.Id, None)).Value!;
        Assert.True(customerView.CanAccept);
        Assert.Null(customerView.PricingBasis);   // internal diagnostics are staff-only
        Assert.Null(customerView.PricingReview);
        var view = (await ctx.Quotations().GetForSalesAsync(s.Sales.Id, false, stored.Id, None)).Value!;
        Assert.Equal(new[] { (catalog.Panel.Sku, 2m, 3_000_000m, 6_000_000m), (catalog.Inverter.Sku, 1m, 10_000_000m, 10_000_000m) },
            view.Lines.Select(l => (l.Sku!, l.Quantity, l.UnitPrice, l.LineTotal)));
        Assert.All(view.Lines, l => Assert.Equal(l.UnitPrice, l.CatalogUnitPrice));
        var basis = view.PricingBasis!;
        Assert.Equal((1, 1.1m, 0), (basis.OnSiteSurveyVersion, basis.InstalledCapacityKwp!.Value, basis.Issues.Count));   // 2 × 550 W
        Assert.Equal(new[] { 2_000_000m, 500_000m }, basis.Services.Select(i => i.LineTotal!.Value));
        var events = await ctx.Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == stored.Id).Select(e => e.EventType).ToListAsync();
        Assert.Equal(new[] { QuotationEventTypes.AutomaticallyGenerated, QuotationEventTypes.Issued }, events.Order());
    }

    [Fact]
    public async Task Repeating_the_completion_or_the_generation_never_creates_a_second_quotation_and_catalog_changes_do_not_alter_it()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());

        var completedAgain = await ctx.OnSiteWithAutomaticQuotation().CompleteAsync(new CompleteOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, 0), None);
        var generatedAgain = await ctx.Quotations().GenerateForStaffAsync(s.Sales.Id, false, s.Request.Request.Id, None);
        await ctx.Db.Products.Where(p => p.Id == catalog.Panel.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.UnitPrice, 9_999_999m));

        Assert.Equal(AutomaticQuotationOutcomes.AlreadyGenerated, completedAgain.Value!.AutomaticQuotationOutcome);
        Assert.Equal(AutomaticQuotationOutcomes.AlreadyGenerated, generatedAgain.Value!.Outcome);
        var stored = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
        Assert.Equal(18_500_000m, stored.TotalAmount);
        Assert.Equal(3_000_000m, (await ctx.Db.QuotationLines.AsNoTracking().FirstAsync(l => l.QuotationId == stored.Id && l.ProductId == catalog.Panel.Id)).UnitPrice);
    }

    [Fact]
    public async Task A_missing_price_leaves_a_draft_for_sales_which_issues_it_after_review()
    {
        // Arrange: the labor product has no price (0 VND).
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        ctx.QuotationOptions.DefaultTaxRatePercent = 0m;
        var s = await ctx.ConfirmedSurveyAsync();
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx, laborPrice: 0m);

        // Act
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());

        // Assert: nothing invented, nothing visible to the customer, a reviewable draft for Sales.
        Assert.Equal(AutomaticQuotationOutcomes.PendingSalesReview, completed.Value!.AutomaticQuotationOutcome);
        var draft = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
        Assert.Equal((QuotationStatuses.Draft, 0m), (draft.Status, draft.LaborCost));
        Assert.Empty(await ctx.Quotations().ListForCustomerAsync(s.Request.Customer.Id, None));
        Assert.Equal(QuotationErrorCodes.NotFound, (await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, draft.Id, None)).ErrorCode);
        var queue = await ctx.Quotations().ListPendingReviewAsync(s.Sales.Id, false, None);
        var issue = Assert.Single(Assert.Single(queue).PricingBasis!.Issues);
        Assert.Equal((PricingIssueCodes.PriceMissing, catalog.Labor.Id), (issue.Code, issue.ProductId!.Value));
        Assert.Empty(await ctx.Quotations().ListPendingReviewAsync(Guid.NewGuid(), false, None));   // another Sales user sees nothing

        // Sales resolves the exception with an explicit labor amount and issues it through the existing path.
        var q = ctx.Quotations();
        var current = (await q.GetForSalesAsync(s.Sales.Id, false, draft.Id, None)).Value!;
        var updated = await q.UpdateHeaderAsync(s.Sales.Id, draft.Id, current.Revision,
            new QuotationHeaderInput(2_000_000m, current.TransportCost, 0m, 0m, 0m, current.ValidUntil, "Labor confirmed by Sales", null), None);
        Assert.True(updated.Succeeded, updated.Message);
        var issued = await q.IssueAsync(s.Sales.Id, draft.Id, updated.Value!.Revision, None);
        Assert.True(issued.Succeeded, issued.Message);
        Assert.Equal((QuotationStatuses.Issued, 18_500_000m), (issued.Value!.Status, issued.Value.TotalAmount));
        Assert.Empty(await ctx.Quotations().ListPendingReviewAsync(s.Sales.Id, false, None));
    }

    [Theory]
    [InlineData("FEASIBLE_WITH_CONDITIONS", AutomaticQuotationOutcomes.PendingSalesReview, 1)]
    [InlineData("NOT_FEASIBLE", AutomaticQuotationOutcomes.NotFeasible, 0)]
    public async Task Feasibility_decides_whether_an_installation_quotation_can_be_issued(string feasibility, string outcome, int quotations)
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;

        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill(), feasibility);

        Assert.Equal(outcome, completed.Value!.AutomaticQuotationOutcome);
        var stored = await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id);
        Assert.Equal(quotations, stored.Count);
        Assert.All(stored, q => Assert.Equal(QuotationStatuses.Draft, q.Status));
    }

    [Fact]
    public async Task Without_a_bill_of_materials_sales_prepares_the_quotation_manually_as_before()
    {
        var (ctx, s, _) = await ReadyAsync();
        await using var _ = ctx;

        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, bom: null);
        var manual = await ctx.Quotations().CreateAsync(s.Sales.Id, s.Request.Request.Id,
            new QuotationHeaderInput(1m, 0m, 0m, 0m, 0m, ctx.Clock.Now.AddDays(10), null, null), None);

        Assert.Equal(AutomaticQuotationOutcomes.ManualQuotationRequired, completed.Value!.AutomaticQuotationOutcome);
        Assert.True(manual.Succeeded, manual.Message);
        Assert.Equal(QuotationOrigins.Sales, manual.Value!.Origin);
    }

    [Fact]
    public async Task A_bill_of_materials_with_missing_parts_is_routed_to_sales_instead_of_guessed()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var panelsOnly = new BillOfMaterialsInput([new BillOfMaterialsItem(catalog.Panel.Id, 4m)], [], null);

        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, panelsOnly);

        Assert.Equal(AutomaticQuotationOutcomes.PendingSalesReview, completed.Value!.AutomaticQuotationOutcome);
        var draft = Assert.Single(await ctx.Quotations().ListPendingReviewAsync(s.Sales.Id, false, None));
        Assert.Equal(new[] { PricingIssueCodes.LaborNotSpecified, PricingIssueCodes.MissingInverter },
            draft.PricingBasis!.Issues.Select(i => i.Code).Order());
    }

    [Fact]
    public async Task The_technician_bill_of_materials_is_validated_against_the_catalog()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var onSite = ctx.OnSiteWithAutomaticQuotation();
        ctx.Clock.Now = At(10).ToUniversalTime();
        var started = (await onSite.StartAsync(s.Technician.Id, s.AppointmentId, None)).Value!;

        // Structure: zero quantity and duplicate products are refused when saving.
        var badStructure = new BillOfMaterialsInput([new BillOfMaterialsItem(catalog.Panel.Id, 0m), new BillOfMaterialsItem(catalog.Panel.Id, 2m)], [], null);
        Assert.Equal(Modules.Common.Results.CommonErrorCodes.ValidationFailed, (await onSite.SaveAsync(
            new SaveOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, started.Revision, AutomaticQuotationFixture.Data(badStructure)), None)).ErrorCode);

        // Catalog: a service listed as equipment and half a panel are refused at completion; the survey stays open.
        await ctx.Db.Products.Where(p => p.Id == catalog.Inverter.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, ProductStatus.Inactive));
        var wrong = new BillOfMaterialsInput(
            [new BillOfMaterialsItem(catalog.Panel.Id, 1.5m), new BillOfMaterialsItem(catalog.Labor.Id, 1m), new BillOfMaterialsItem(catalog.Inverter.Id, 1m)], [], null);
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, wrong);

        Assert.Equal(OnSiteSurveyErrorCodes.BillOfMaterialsInvalid, completed.ErrorCode);
        var details = System.Text.Json.JsonSerializer.Serialize(completed.Details);
        Assert.Contains("whole number", details);
        Assert.Contains("is a service", details);
        Assert.Contains("not active", details);
        Assert.Equal(OnSiteSurveyStatuses.InProgress, (await ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
    }

    [Fact]
    public async Task Generation_waits_for_a_sales_owner_and_only_the_owner_or_a_manager_can_retry_it()
    {
        // Arrange: the request has no Sales owner when the survey is completed.
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        await ctx.Db.SurveyRequests.Where(r => r.Id == s.Request.Request.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.AssignedSaleId, (Guid?)null));

        // Act
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());
        var stranger = await ctx.Quotations().GenerateForStaffAsync(s.Sales.Id, false, s.Request.Request.Id, None);
        await ctx.Db.SurveyRequests.Where(r => r.Id == s.Request.Request.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.AssignedSaleId, (Guid?)s.Sales.Id));
        var retried = await ctx.Quotations().GenerateForStaffAsync(s.Sales.Id, false, s.Request.Request.Id, None);

        // Assert
        Assert.Equal(AutomaticQuotationOutcomes.SalesOwnerRequired, completed.Value!.AutomaticQuotationOutcome);
        Assert.Equal(QuotationErrorCodes.NotFound, stranger.ErrorCode);
        Assert.Equal(AutomaticQuotationOutcomes.Issued, retried.Value!.Outcome);
    }

    [Fact]
    public async Task An_amended_survey_never_replaces_the_issued_quotation_automatically()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());
        var report = await ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync();

        var amended = await ctx.OnSiteWithAutomaticQuotation().AmendAsync(new AmendOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, "Roof needs 4 panels",
            AutomaticQuotationFixture.Data(catalog.StandardBill(panels: 4m)) with { RoofType = null, Obstacles = null }, null, report.Revision), None);
        var generated = await ctx.Quotations().GenerateForStaffAsync(s.Sales.Id, false, s.Request.Request.Id, None);

        Assert.True(amended.Succeeded, amended.Message);
        Assert.Equal(2, amended.Value!.Version);
        Assert.Contains(amended.Value.Amendments, a => a.Changes.TryGetProperty("billOfMaterials", out var bomChange) && bomChange.ValueKind != System.Text.Json.JsonValueKind.Undefined);
        Assert.Equal(AutomaticQuotationOutcomes.SurveyChangedReviewRequired, generated.Value!.Outcome);
        var stored = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
        Assert.Equal((QuotationStatuses.Issued, 1, 16_000_000m), (stored.Status, stored.OnSiteSurveyVersion, stored.EquipmentSubtotal));
    }
}
