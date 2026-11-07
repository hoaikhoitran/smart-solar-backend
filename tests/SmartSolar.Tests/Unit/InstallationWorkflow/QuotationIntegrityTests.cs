using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Remediation of the business audit (2026-10-30): survey versions stay truthful through quotation, acceptance and contract
/// (BL-02, BL-05, BL-06); a missing price is never issued as free (BL-03); customers never receive internal pricing diagnostics
/// (BL-04); same-timestamp events read in lifecycle order (BL-15). Prices are SYNTHETIC fixture values.
/// </summary>
public sealed class QuotationIntegrityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey, QuotationCatalog Catalog)> ReadyAsync(decimal laborPrice = 2_000_000m)
    {
        var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = 0m;
        var survey = await ctx.ConfirmedSurveyAsync();
        return (ctx, survey, await AutomaticQuotationFixture.SeedCatalogAsync(ctx, laborPrice));
    }

    /// <summary>The technician amends the completed survey's bill of materials (production wiring, so the quotations are notified).</summary>
    private static async Task<OperationResult<OnSiteSurveyView>> AmendBillAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s, BillOfMaterialsInput bom)
    {
        var onSite = ctx.OnSiteWithAutomaticQuotation();
        var report = (await onSite.GetForTechnicianAsync(s.Technician.Id, s.AppointmentId, None)).Value!;
        return await onSite.AmendAsync(new AmendOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, "Customer asked for 4 panels",
            new OnSiteSurveyData(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, bom), null, report.Revision), None);
    }

    private static async Task<Quotation> IssuedAutomaticAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s, QuotationCatalog catalog)
    {
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());
        Assert.Equal(AutomaticQuotationOutcomes.Issued, completed.Value!.AutomaticQuotationOutcome);
        return Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
    }

    // ------------------------------------------------------------------ BL-03 / BL-04

    [Fact]
    public async Task An_automatic_draft_with_a_missing_price_cannot_be_issued_until_the_price_is_entered()
    {
        var (ctx, s, catalog) = await ReadyAsync(laborPrice: 0m);
        await using var _ = ctx;
        await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill());
        var q = ctx.Quotations();
        var draft = Assert.Single(await q.ListPendingReviewAsync(s.Sales.Id, false, None));

        // Issuing it unchanged (labor 0) is refused, with the outstanding issue; a review note cannot excuse a missing price.
        var unchanged = await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, None, reviewNote: "looks fine");
        Assert.Equal(QuotationErrorCodes.PricingReviewRequired, unchanged.ErrorCode);
        Assert.Contains(PricingIssueCodes.PriceMissing, System.Text.Json.JsonSerializer.Serialize(unchanged.Details));
        Assert.Equal(PricingIssueCodes.PriceMissing, Assert.Single(draft.PricingReview!.Outstanding).Code);

        var updated = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision,
            new QuotationHeaderInput(2_000_000m, draft.TransportCost, 0m, 0m, 0m, draft.ValidUntil, null, null), None)).Value!;
        Assert.Empty(updated.PricingReview!.Outstanding);   // the diagnostic follows the edit
        var issued = await q.IssueAsync(s.Sales.Id, draft.QuotationId, updated.Revision, None);

        Assert.True(issued.Succeeded, issued.Message);
        Assert.Equal(18_500_000m, issued.Value!.TotalAmount);
        Assert.Null(issued.Value.PricingReview);   // review state exists only for drafts
    }

    [Fact]
    public async Task Conditions_to_review_need_a_review_note_which_is_recorded_on_the_issue()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill(), feasibility: "FEASIBLE_WITH_CONDITIONS");
        var q = ctx.Quotations();
        var draft = Assert.Single(await q.ListPendingReviewAsync(s.Sales.Id, false, None));
        Assert.True(draft.PricingReview!.NoteRequired);

        Assert.Equal(QuotationErrorCodes.PricingReviewRequired, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, None, new string('x', 1001))).ErrorCode);
        var issued = await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, None, "Scaffolding included in labor; roof reinforcement not needed");

        Assert.True(issued.Succeeded, issued.Message);
        var issuedEvent = issued.Value!.Events!.Single(e => e.EventType == QuotationEventTypes.Issued);
        Assert.Contains("Scaffolding included", issuedEvent.Detail);
    }

    [Fact]
    public async Task Customers_never_receive_pricing_diagnostics_or_the_event_history()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var quotation = await IssuedAutomaticAsync(ctx, s, catalog);
        var q = ctx.Quotations();
        var customer = s.Request.Customer.Id;

        var read = (await q.GetForCustomerAsync(customer, quotation.Id, None)).Value!;
        var listed = Assert.Single(await q.ListForCustomerAsync(customer, None));
        var revision = (await q.RequestRevisionAsync(customer, quotation.Id, "Cheaper inverter please", None)).Value!;

        Assert.All(new[] { read, listed, revision }, v =>
        {
            Assert.Null(v.PricingBasis);
            Assert.Null(v.PricingReview);
            Assert.Null(v.Events);
        });
        Assert.NotNull((await q.GetForSalesAsync(s.Sales.Id, false, quotation.Id, None)).Value!.PricingBasis);
    }

    // ------------------------------------------------------------------ BL-02 / BL-05

    [Fact]
    public async Task An_amended_survey_blocks_acceptance_and_signals_sales_once_per_version()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var quotation = await IssuedAutomaticAsync(ctx, s, catalog);

        var amended = await AmendBillAsync(ctx, s, catalog.StandardBill(panels: 4));
        Assert.True(amended.Succeeded, amended.Message);
        await ctx.Quotations().RecordSurveyAmendedAsync(s.Request.Request.Id, amended.Value!.Version, s.Technician.Id, None);   // retried

        var q = ctx.Quotations();
        var accept = await q.AcceptAsync(s.Request.Customer.Id, quotation.Id, None);
        Assert.Equal(QuotationErrorCodes.SurveyChanged, accept.ErrorCode);
        var customerView = (await q.GetForCustomerAsync(s.Request.Customer.Id, quotation.Id, None)).Value!;
        Assert.Equal((false, true, 2), (customerView.CanAccept, customerView.SurveyChanged, customerView.CurrentSurveyVersion!.Value));
        var queued = Assert.Single(await q.ListPendingReviewAsync(s.Sales.Id, false, None));
        Assert.Equal((quotation.Id, true), (queued.QuotationId, queued.SurveyChanged));
        Assert.Single(await ctx.Db.QuotationEvents.Where(e => e.QuotationId == quotation.Id && e.EventType == QuotationEventTypes.SurveyAmended).ToListAsync());
        Assert.Equal(QuotationStatuses.Issued, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == quotation.Id)).Status);   // history untouched
    }

    [Fact]
    public async Task A_contract_cannot_be_created_from_a_quotation_whose_survey_was_amended_after_acceptance()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var quotation = await IssuedAutomaticAsync(ctx, s, catalog);
        Assert.True((await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, quotation.Id, None)).Succeeded);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        Assert.True((await AmendBillAsync(ctx, s, catalog.StandardBill(panels: 4))).Succeeded);

        var contract = await ctx.Contracts().CreateAsync(s.Sales.Id, quotation.Id, "Standard terms", None);

        Assert.Equal(ContractErrorCodes.SurveyChanged, contract.ErrorCode);
        Assert.Empty(await ctx.Db.CustomerContracts.ToListAsync());
    }

    // ------------------------------------------------------------------ BL-06

    [Fact]
    public async Task A_revision_keeps_the_survey_version_of_its_copied_lines_until_sales_confirms_the_current_one()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var v1 = await IssuedAutomaticAsync(ctx, s, catalog);
        Assert.True((await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, v1.Id, None)).Succeeded);
        Assert.True((await AmendBillAsync(ctx, s, catalog.StandardBill(panels: 4))).Succeeded);
        var q = ctx.Quotations();

        // A stale ACCEPTED quotation without a contract can be revised; the copy states the survey version of its lines.
        var v2 = (await q.CreateRevisionAsync(s.Sales.Id, v1.Id, None)).Value!;
        Assert.Equal((1, 2, true), (v2.OnSiteSurveyVersion, v2.CurrentSurveyVersion!.Value, v2.SurveyChanged));
        Assert.Equal(QuotationErrorCodes.SurveyChanged, (await q.IssueAsync(s.Sales.Id, v2.QuotationId, v2.Revision, None)).ErrorCode);

        // Sales adds the two extra panels, then confirms the draft against survey v2 with a reason.
        var withPanels = (await q.UpdateLineAsync(s.Sales.Id, v2.QuotationId, v2.Lines.Single(l => l.ProductId == catalog.Panel.Id).LineId, v2.Revision,
            4m, null, null, None)).Value!;
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.ConfirmSurveyVersionAsync(s.Sales.Id, v2.QuotationId, withPanels.Revision, " ", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.ConfirmSurveyVersionAsync(s.Technician.Id, v2.QuotationId, withPanels.Revision, "x", None)).ErrorCode);
        var confirmed = (await q.ConfirmSurveyVersionAsync(s.Sales.Id, v2.QuotationId, withPanels.Revision, "Lines checked against the 4-panel survey", None)).Value!;
        Assert.Equal((2, false), (confirmed.OnSiteSurveyVersion, confirmed.SurveyChanged));

        var issued = (await q.IssueAsync(s.Sales.Id, v2.QuotationId, confirmed.Revision, None)).Value!;
        Assert.True((await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, None)).Succeeded);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var contract = await ctx.Contracts().CreateAsync(s.Sales.Id, issued.QuotationId, "Standard terms", None);

        Assert.True(contract.Succeeded, contract.Message);
        Assert.Equal(24_500_000m, contract.Value!.TotalAmount);   // 4 × 3,000,000 + 10,000,000 + 2,000,000 + 500,000
        var stored = await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id);
        Assert.Equal(new[] { (1, QuotationStatuses.Superseded, 1), (2, QuotationStatuses.Accepted, 2) }, stored.Select(x => (x.Version, x.Status, x.OnSiteSurveyVersion)));
        Assert.Contains(issued.Events!, e => e.EventType == QuotationEventTypes.SurveyVersionConfirmed && e.Detail!.StartsWith("survey v1 → v2"));
    }

    [Fact]
    public async Task An_accepted_quotation_with_a_contract_is_never_revised_or_superseded()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var v1 = await IssuedAutomaticAsync(ctx, s, catalog);
        Assert.True((await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, v1.Id, None)).Succeeded);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        Assert.True((await ctx.Contracts().CreateAsync(s.Sales.Id, v1.Id, "Standard terms", None)).Succeeded);
        // D-02: once a contract exists the survey is locked; the commercial snapshot is never changed underneath it.
        Assert.Equal(OnSiteSurveyErrorCodes.CommerciallyLocked, (await AmendBillAsync(ctx, s, catalog.StandardBill(panels: 4))).ErrorCode);
        Assert.Equal(1, (await ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync(r => r.SurveyRequestId == s.Request.Request.Id)).Version);

        Assert.Equal(QuotationErrorCodes.CannotRevise, (await ctx.Quotations().CreateRevisionAsync(s.Sales.Id, v1.Id, None)).ErrorCode);
        Assert.Equal(QuotationStatuses.Accepted, (await ctx.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == v1.Id)).Status);
    }

    // ------------------------------------------------------------------ BL-14

    [Fact]
    public async Task A_contract_without_terms_cannot_be_issued_until_terms_are_entered()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var quotation = await IssuedAutomaticAsync(ctx, s, catalog);   // automatic quotations carry no terms by default
        Assert.True((await ctx.Quotations().AcceptAsync(s.Request.Customer.Id, quotation.Id, None)).Succeeded);
        await ctx.EnsureInstallationWarrantyOptionAsync();
        var c = ctx.Contracts();
        var draft = (await c.CreateAsync(s.Sales.Id, quotation.Id, null, None)).Value!;
        Assert.Null(draft.Terms);

        Assert.Equal(ContractErrorCodes.TermsRequired, (await c.IssueAsync(s.Sales.Id, draft.ContractId, draft.Revision, None)).ErrorCode);
        Assert.Equal(400, Api.Controllers.OperationResultControllerBase.StatusFor(ContractErrorCodes.TermsRequired));
        var withTerms = (await c.UpdateTermsAsync(s.Sales.Id, draft.ContractId, draft.Revision, "Standard installation terms v3", None)).Value!;
        var issued = await c.IssueAsync(s.Sales.Id, draft.ContractId, withTerms.Revision, None);

        Assert.True(issued.Succeeded, issued.Message);
        Assert.Equal(ContractStatuses.Issued, issued.Value!.Status);
    }

    // ------------------------------------------------------------------ BL-15

    [Fact]
    public async Task Events_written_in_the_same_instant_are_listed_in_lifecycle_order()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var quotation = await IssuedAutomaticAsync(ctx, s, catalog);

        var events = (await ctx.Quotations().GetForSalesAsync(s.Sales.Id, false, quotation.Id, None)).Value!.Events!;

        Assert.Equal(new[] { QuotationEventTypes.AutomaticallyGenerated, QuotationEventTypes.Issued }, events.Select(e => e.EventType));
        Assert.Equal(events[0].CreatedAt, events[1].CreatedAt);
    }
}
