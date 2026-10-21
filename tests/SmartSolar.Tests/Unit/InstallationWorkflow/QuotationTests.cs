using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

public sealed class QuotationTests
{
    private static QuotationHeaderInput Header(InstallationWorkflowTestContext ctx, decimal discount = 0m, decimal tax = 8m, int validDays = 30)
        => new(LaborCost: 5_000_000m, TransportCost: 1_000_000m, OtherServiceCost: 0m, DiscountAmount: discount, TaxRatePercent: tax,
            ValidUntil: ctx.Clock.Now.AddDays(validDays), Terms: "Payment 100% before installation", Notes: null);

    /// <summary>Survey completed, draft with 12 panels and 1 inverter, issued. Returns the issued view.</summary>
    public static async Task<(ConfirmedSurvey Survey, QuotationView Issued)> IssuedAsync(InstallationWorkflowTestContext ctx, string customer = "Customer")
    {
        var s = await ctx.QuotableSurveyAsync(customer);
        var panel = await ctx.ProductAsync($"PNL-{Guid.NewGuid():N}"[..20], 2_500_000m);
        var inverter = await ctx.ProductAsync($"INV-{Guid.NewGuid():N}"[..20], 12_345_678m);
        var q = ctx.Quotations();
        var draft = (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, new QuotationHeaderInput(5_000_000m, 1_000_000m, 0m, 0m, 8m,
            ctx.Clock.Now.AddDays(30), "Terms", null), CancellationToken.None)).Value!;
        draft = (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, draft.Revision, new QuotationLineInput(panel.Id, null, null, 12m, null, null), CancellationToken.None)).Value!;
        draft = (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, draft.Revision, new QuotationLineInput(inverter.Id, null, null, 1m, null, null), CancellationToken.None)).Value!;
        var issued = await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, CancellationToken.None);
        Assert.True(issued.Succeeded, issued.Message);
        return (s, issued.Value!);
    }

    [Fact]
    public void Money_is_rounded_to_whole_vnd_and_tax_applies_after_discount()
    {
        var totals = QuotationPricing.Compute([QuotationPricing.LineTotal(3.333m, 1_000_001m), 10_000_000m], 1_000_000.4m, 0m, 0m, 500_000m, 8m);

        Assert.Equal(3_333_003m + 10_000_000m, totals.EquipmentSubtotal);     // 3.333 × 1,000,001 = 3,333,003.333 → 3,333,003
        Assert.Equal(14_333_003m, totals.Subtotal);
        Assert.Equal(Math.Round((14_333_003m - 500_000m) * 0.08m, 0, MidpointRounding.AwayFromZero), totals.TaxAmount);
        Assert.Equal(14_333_003m - 500_000m + totals.TaxAmount, totals.TotalAmount);
        Assert.Equal(3m, QuotationPricing.Round(2.5m));
    }

    [Fact]
    public async Task Sales_builds_and_issues_a_quotation_with_server_computed_totals()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await IssuedAsync(ctx);

        Assert.Equal(QuotationStatuses.Issued, issued.Status);
        Assert.Equal(2, issued.Lines.Count);
        Assert.Equal(30_000_000m + 12_345_678m, issued.EquipmentSubtotal);
        Assert.Equal(issued.EquipmentSubtotal + 6_000_000m, issued.Subtotal);
        Assert.Equal(QuotationPricing.Round(issued.Subtotal * 0.08m), issued.TaxAmount);
        Assert.Equal(issued.Subtotal + issued.TaxAmount, issued.TotalAmount);
        Assert.StartsWith("QT-", issued.QuoteNumber);
        Assert.True(issued.CanAccept);

        var mine = await ctx.Quotations().ListForCustomerAsync(s.Request.Customer.Id, CancellationToken.None);
        Assert.Equal(issued.QuotationId, Assert.Single(mine).QuotationId);
    }

    [Fact]
    public async Task Only_the_owning_sales_quotes_and_only_after_the_onsite_survey_is_completed()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await ctx.ConfirmedSurveyAsync();
        var q = ctx.Quotations();

        Assert.Equal(QuotationErrorCodes.SurveyNotCompleted, (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None)).ErrorCode);
        await OnSiteSurveyTests.CompleteAsync(ctx, s);
        var stranger = await ctx.Scheduling.Inner.SeedSalesUserAsync();
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.CreateAsync(stranger.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None)).ErrorCode);

        var draft = await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None);
        Assert.True(draft.Succeeded, draft.Message);
        Assert.Equal(QuotationErrorCodes.DraftExists, (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None)).ErrorCode);
        // Drafts are invisible to the customer.
        Assert.Empty(await q.ListForCustomerAsync(s.Request.Customer.Id, CancellationToken.None));
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.GetForCustomerAsync(s.Request.Customer.Id, draft.Value!.QuotationId, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Catalog_price_changes_never_alter_an_issued_quotation()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await IssuedAsync(ctx);
        var productId = issued.Lines[0].ProductId!.Value;

        var product = await ctx.Db.Products.SingleAsync(p => p.Id == productId);
        product.UnitPrice = 9_999_999m;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var reloaded = (await ctx.Quotations().GetForCustomerAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).Value!;
        Assert.Equal(2_500_000m, reloaded.Lines[0].UnitPrice);
        Assert.Equal(issued.TotalAmount, reloaded.TotalAmount);
    }

    [Fact]
    public async Task Issued_quotations_are_immutable()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await IssuedAsync(ctx);

        var edit = await ctx.Quotations().UpdateHeaderAsync(s.Sales.Id, issued.QuotationId, issued.Revision, Header(ctx, discount: 1m), CancellationToken.None);

        Assert.Equal(QuotationErrorCodes.NotDraft, edit.ErrorCode);
    }

    [Fact]
    public async Task Customer_accepts_an_issued_quotation_once_and_expired_or_foreign_ones_are_refused()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await IssuedAsync(ctx);
        var q = ctx.Quotations();

        Assert.Equal(QuotationErrorCodes.NotFound, (await q.AcceptAsync(Guid.NewGuid(), issued.QuotationId, CancellationToken.None)).ErrorCode);
        var savedClock = ctx.Clock.Now;
        ctx.Clock.Now = issued.ValidUntil!.Value.AddMinutes(1);
        Assert.Equal(QuotationErrorCodes.Expired, (await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).ErrorCode);
        ctx.Clock.Now = savedClock;

        var accepted = await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None);
        Assert.Equal(QuotationStatuses.Accepted, accepted.Value!.Status);
        Assert.NotNull(accepted.Value.AcceptedAt);
        Assert.Equal(QuotationStatuses.Accepted, (await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).Value!.Status);   // idempotent
        Assert.Equal(QuotationErrorCodes.CannotRevise, (await q.CreateRevisionAsync(s.Sales.Id, issued.QuotationId, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Revision_request_leads_to_a_new_version_that_supersedes_the_old_one()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await IssuedAsync(ctx);
        var q = ctx.Quotations();

        var requested = await q.RequestRevisionAsync(s.Request.Customer.Id, issued.QuotationId, "Please reduce the labour cost", CancellationToken.None);
        Assert.Equal(QuotationStatuses.RevisionRequested, requested.Value!.Status);
        Assert.Equal(QuotationErrorCodes.NotIssued, (await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).ErrorCode);

        var v2 = (await q.CreateRevisionAsync(s.Sales.Id, issued.QuotationId, CancellationToken.None)).Value!;
        Assert.Equal(2, v2.Version);
        Assert.Equal(issued.QuotationId, v2.PreviousQuotationId);
        Assert.Equal(issued.QuoteNumber, v2.QuoteNumber);
        v2 = (await q.UpdateHeaderAsync(s.Sales.Id, v2.QuotationId, v2.Revision, Header(ctx) with { LaborCost = 3_000_000m }, CancellationToken.None)).Value!;
        var issuedV2 = (await q.IssueAsync(s.Sales.Id, v2.QuotationId, v2.Revision, CancellationToken.None)).Value!;
        Assert.True(issuedV2.TotalAmount < issued.TotalAmount);

        var old = (await q.GetForCustomerAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).Value!;
        Assert.Equal(QuotationStatuses.Superseded, old.Status);
        Assert.Equal(issued.TotalAmount, old.TotalAmount);                 // the issued version itself never changed
        Assert.Equal(QuotationErrorCodes.NotIssued, (await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).ErrorCode);
        Assert.True((await q.AcceptAsync(s.Request.Customer.Id, issuedV2.QuotationId, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task A_revision_cannot_be_issued_once_the_customer_accepted_the_previous_version()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var (s, issued) = await IssuedAsync(ctx);
        var q = ctx.Quotations();
        var v2 = (await q.CreateRevisionAsync(s.Sales.Id, issued.QuotationId, CancellationToken.None)).Value!;

        Assert.True((await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, CancellationToken.None)).Succeeded);
        var issue = await q.IssueAsync(s.Sales.Id, v2.QuotationId, v2.Revision, CancellationToken.None);

        Assert.Equal(QuotationErrorCodes.CannotRevise, issue.ErrorCode);
        Assert.Equal(QuotationStatuses.Accepted, (await q.GetForSalesAsync(s.Sales.Id, false, issued.QuotationId, CancellationToken.None)).Value!.Status);
    }

    [Fact]
    public async Task Stale_revisions_and_invalid_amounts_are_rejected()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await ctx.QuotableSurveyAsync();
        var q = ctx.Quotations();
        var draft = (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None)).Value!;

        var first = await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, Header(ctx) with { LaborCost = 1m }, CancellationToken.None);
        var second = await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, Header(ctx) with { LaborCost = 2m }, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(QuotationErrorCodes.StaleRevision, second.ErrorCode);

        Assert.Equal(QuotationErrorCodes.InvalidLine, (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, first.Value!.Revision,
            Header(ctx, discount: 999_999_999m), CancellationToken.None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, first.Value.Revision,
            Header(ctx, tax: 50m), CancellationToken.None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.InvalidLine, (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, first.Value.Revision,
            new QuotationLineInput(null, null, null, 1m, 100m, null), CancellationToken.None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.Empty, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, first.Value.Revision, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Inactive_products_cannot_be_quoted_but_custom_lines_can()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await ctx.QuotableSurveyAsync();
        var inactive = await ctx.ProductAsync("OLD-1", 1_000m);
        var row = await ctx.Db.Products.SingleAsync(p => p.Id == inactive.Id);
        row.Status = Modules.Catalog.Enums.ProductStatus.Inactive;
        await ctx.Db.SaveChangesAsync();
        var q = ctx.Quotations();
        var draft = (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None)).Value!;

        Assert.Equal(QuotationErrorCodes.ProductUnavailable, (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, draft.Revision,
            new QuotationLineInput(inactive.Id, null, null, 1m, null, null), CancellationToken.None)).ErrorCode);
        var custom = await q.AddLineAsync(s.Sales.Id, draft.QuotationId, draft.Revision,
            new QuotationLineInput(null, "Cable tray", "SET", 2m, 750_000m, "Galvanised"), CancellationToken.None);
        Assert.Equal(1_500_000m, custom.Value!.Lines.Single().LineTotal);

        var removed = await q.RemoveLineAsync(s.Sales.Id, draft.QuotationId, custom.Value.Lines[0].LineId, custom.Value.Revision, CancellationToken.None);
        Assert.Empty(removed.Value!.Lines);
        Assert.Equal(0m, removed.Value.EquipmentSubtotal);
    }

    private static async Task<Modules.Catalog.Entities.Product> InverterAsync(InstallationWorkflowTestContext ctx, string model, decimal price)
    {
        var inverter = PreSurveyTestContext.NewPanelProduct($"INV-{model}", ratedPowerW: null, widthMm: null, heightMm: null,
            productType: Modules.Catalog.Constants.ProductTypes.Inverter, spec: "{\"ratedAcPowerW\":5000,\"phase\":\"SINGLE_PHASE\"}");
        inverter.Name = $"Inverter {model}";
        inverter.Brand = "Huawei";
        inverter.Model = model;
        inverter.UnitPrice = price;
        ctx.Db.Products.Add(inverter);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return inverter;
    }

    [Fact]
    public async Task Sales_quotes_several_inverter_models_and_the_lines_keep_their_catalog_snapshot()
    {
        // Arrange: two active inverter models in the catalog.
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var s = await ctx.QuotableSurveyAsync();
        var small = await InverterAsync(ctx, "SUN2000-5K", 15_000_000m);
        var large = await InverterAsync(ctx, "SUN2000-10K", 30_000_000m);
        var q = ctx.Quotations();
        var draft = (await q.CreateAsync(s.Sales.Id, s.Request.Request.Id, Header(ctx), CancellationToken.None)).Value!;

        // Act: Sales picks both models with their own quantities, then changes the quantity of the first line.
        draft = (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, draft.Revision, new QuotationLineInput(small.Id, null, null, 2m, null, null), CancellationToken.None)).Value!;
        draft = (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, draft.Revision, new QuotationLineInput(large.Id, null, null, 1m, null, null), CancellationToken.None)).Value!;
        draft = (await q.UpdateLineAsync(s.Sales.Id, draft.QuotationId, draft.Lines[0].LineId, draft.Revision, 3m, null, null, CancellationToken.None)).Value!;
        var issued = await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, CancellationToken.None);

        // Assert: catalog prices are used and totals are computed by the server.
        Assert.True(issued.Succeeded, issued.Message);
        var lines = issued.Value!.Lines;
        Assert.Equal(new[] { ("SUN2000-5K", 3m, 15_000_000m, 45_000_000m), ("SUN2000-10K", 1m, 30_000_000m, 30_000_000m) },
            lines.Select(l => (l.Model!, l.Quantity, l.UnitPrice, l.LineTotal)));
        Assert.Equal(75_000_000m, issued.Value.EquipmentSubtotal);
        Assert.Equal(75_000_000m + 6_000_000m, issued.Value.Subtotal);   // + labor 5,000,000 and transport 1,000,000
        Assert.Equal(issued.Value.Subtotal + QuotationPricing.Round(issued.Value.Subtotal * 0.08m), issued.Value.TotalAmount);

        // A later catalog change (name and price) does not touch the quotation.
        var row = await ctx.Db.Products.SingleAsync(p => p.Id == small.Id);
        row.Name = "Renamed inverter";
        row.UnitPrice = 1m;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var reloaded = (await q.GetForCustomerAsync(s.Request.Customer.Id, issued.Value.QuotationId, CancellationToken.None)).Value!;
        Assert.Equal(("Inverter SUN2000-5K", 15_000_000m, (decimal?)15_000_000m), (reloaded.Lines[0].Name, reloaded.Lines[0].UnitPrice, reloaded.Lines[0].CatalogUnitPrice));
        Assert.Equal(issued.Value.TotalAmount, reloaded.TotalAmount);
    }

    [Theory]
    [InlineData("ux_quotation_one_live", true)]
    [InlineData("ux_quotation_one_draft", true)]
    [InlineData("quotation.survey_request_id, quotation.customer_user_id", true)]
    [InlineData("ux_quotation_line_number", false)]
    [InlineData("quotation_line.quotation_id, quotation_line.line_number", false)]
    [InlineData(null, false)]
    public void Only_the_one_live_quotation_indexes_mean_another_quotation_is_live(string? constraint, bool expected)
        => Assert.Equal(expected, QuotationHandlers.IsOneLivePerRequestConstraint(constraint));
}
