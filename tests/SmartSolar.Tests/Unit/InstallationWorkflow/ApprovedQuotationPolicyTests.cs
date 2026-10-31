using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Quotations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

/// <summary>
/// Product Owner decisions of 2026-10-31 on quotations: D-01 Manager approval of every discount / reduced / zero price, bound to the exact
/// pricing; D-02 a survey-version confirmation that cannot re-label outdated quantities; D-03 no quotation for a NOT_FEASIBLE survey;
/// D-09 customers see selling prices and totals, never catalog reference prices or diagnostics. Prices are SYNTHETIC fixture values.
/// </summary>
public sealed class ApprovedQuotationPolicyTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<(InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey, QuotationCatalog Catalog)> ReadyAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = 0m;
        var survey = await ctx.ConfirmedSurveyAsync();
        return (ctx, survey, await AutomaticQuotationFixture.SeedCatalogAsync(ctx));
    }

    /// <summary>Automatic quotation issued from the standard bill, sent back by the customer, and a revision draft (v2) created by Sales.</summary>
    private static async Task<(QuotationHandlers Q, QuotationView Draft, Guid Manager)> RevisionDraftAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s,
        QuotationCatalog catalog, decimal panels = 2m)
    {
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill(panels));
        Assert.Equal(AutomaticQuotationOutcomes.Issued, completed.Value!.AutomaticQuotationOutcome);
        var v1 = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
        var q = ctx.Quotations();
        Assert.True((await q.RequestRevisionAsync(s.Request.Customer.Id, v1.Id, "Can you do better on the price?", None)).Succeeded);
        var draft = (await q.CreateRevisionAsync(s.Sales.Id, v1.Id, None)).Value!;
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Pricing manager");
        return (q, draft, manager.Id);
    }

    private static QuotationHeaderInput WithDiscount(QuotationView v, decimal discount, string? notes = null)
        => new(v.LaborCost, v.TransportCost, v.OtherServiceCost, discount, v.TaxRatePercent, v.ValidUntil, v.Terms, notes ?? v.Notes);

    // ------------------------------------------------------------------ D-01

    [Fact]
    public async Task Catalog_prices_without_a_discount_need_no_approval_and_the_automatic_quotation_never_discounts()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, _) = await RevisionDraftAsync(ctx, s, catalog);

        Assert.Equal(PricingApprovalStatuses.NotRequired, draft.PricingApproval!.Status);
        Assert.Empty(draft.PricingApproval.Adjustments);
        Assert.Equal(QuotationErrorCodes.PricingApprovalNotRequired,
            (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, draft.Revision, "Just in case", None)).ErrorCode);
        var v1 = (await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id)).Single(x => x.Version == 1);
        Assert.Equal(0m, v1.DiscountAmount);
        Assert.True((await q.IssueAsync(s.Sales.Id, draft.QuotationId, draft.Revision, None)).Succeeded);
    }

    [Fact]
    public async Task A_discount_is_issued_only_after_a_manager_approves_exactly_that_pricing_with_a_recorded_reason()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, manager) = await RevisionDraftAsync(ctx, s, catalog);
        var discounted = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, WithDiscount(draft, 500_000m), None)).Value!;

        // Sales cannot issue an unapproved discount.
        var refused = await q.IssueAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, None);
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, refused.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(refused.ErrorCode!));
        Assert.Contains(PriceAdjustmentKinds.Discount, JsonSerializer.Serialize(refused.Details));
        Assert.Equal(PricingApprovalStatuses.Required, discounted.PricingApproval!.Status);

        // Request: reason required, then PENDING; a duplicate request for the same pricing is refused.
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, " ", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await q.RequestPricingApprovalAsync(s.Technician.Id, draft.QuotationId, discounted.Revision, "x", None)).ErrorCode);
        var pending = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, "Loyal customer, second site", None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Pending, pending.PricingApproval!.Status);
        Assert.Equal(QuotationErrorCodes.PricingApprovalPending,
            (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, pending.Revision, "Again", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, pending.Revision, None)).ErrorCode);
        Assert.Contains(await q.ListPendingPricingApprovalsAsync(None), v => v.QuotationId == draft.QuotationId);

        // Decision: reason required, never by the Sales owner, never on a stale revision.
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, true, "", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.SelfApprovalNotAllowed,
            (await q.DecidePricingApprovalAsync(s.Sales.Id, draft.QuotationId, pending.Revision, true, "Self", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.StaleRevision,
            (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision - 1, true, "Old", None)).ErrorCode);
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(5);
        var approved = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, true, "Within the regional promotion budget", None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Approved, approved.PricingApproval!.Status);
        Assert.Equal((manager, "Within the regional promotion budget", ctx.Clock.Now),
            (approved.PricingApproval.Decision!.ActorUserId, approved.PricingApproval.Decision.Reason, approved.PricingApproval.Decision.At));
        Assert.Equal((s.Sales.Id, "Loyal customer, second site"), (approved.PricingApproval.Request!.ActorUserId, approved.PricingApproval.Request.Reason));
        Assert.Equal(QuotationErrorCodes.PricingApprovalNotPending,
            (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, approved.Revision, true, "Twice", None)).ErrorCode);
        Assert.DoesNotContain(await q.ListPendingPricingApprovalsAsync(None), v => v.QuotationId == draft.QuotationId);

        var issued = (await q.IssueAsync(s.Sales.Id, draft.QuotationId, approved.Revision, None)).Value!;
        Assert.Equal((QuotationStatuses.Issued, 500_000m), (issued.Status, issued.DiscountAmount));
        Assert.Equal(PricingApprovalStatuses.Approved, issued.PricingApproval!.Status);
    }

    [Fact]
    public async Task Changing_the_approved_pricing_invalidates_the_approval_but_a_note_change_does_not()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, manager) = await RevisionDraftAsync(ctx, s, catalog);
        var discounted = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, WithDiscount(draft, 500_000m), None)).Value!;
        var pending = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, "Promotion", None)).Value!;
        var approved = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, true, "OK", None)).Value!;

        // A wording change keeps the approval (the pricing fingerprint is unchanged).
        var renamed = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, approved.Revision, WithDiscount(approved, 500_000m, "Thank you for your loyalty"), None)).Value!;
        Assert.Equal((PricingApprovalStatuses.Approved, approved.PricingApproval!.Fingerprint), (renamed.PricingApproval!.Status, renamed.PricingApproval.Fingerprint));

        // A bigger discount is new pricing: the old approval no longer counts.
        var bigger = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, renamed.Revision, WithDiscount(renamed, 900_000m), None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Required, bigger.PricingApproval!.Status);
        Assert.NotEqual(approved.PricingApproval.Fingerprint, bigger.PricingApproval.Fingerprint);
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, bigger.Revision, None)).ErrorCode);

        // Back to the approved values: the approval of exactly that pricing applies again.
        var back = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, bigger.Revision, WithDiscount(bigger, 500_000m), None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Approved, back.PricingApproval!.Status);
    }

    [Fact]
    public async Task A_rejected_pricing_cannot_be_issued_and_a_new_request_is_decided_again()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, manager) = await RevisionDraftAsync(ctx, s, catalog);
        var discounted = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, WithDiscount(draft, 2_000_000m), None)).Value!;
        var pending = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, "Competitor offer", None)).Value!;

        // Same clock instant for request, rejection and re-request: the sequence number keeps their order.
        var rejected = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, false, "Below the margin floor", None)).Value!;
        Assert.Equal((PricingApprovalStatuses.Rejected, "Below the margin floor"), (rejected.PricingApproval!.Status, rejected.PricingApproval.Decision!.Reason));
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, rejected.Revision, None)).ErrorCode);

        var again = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, rejected.Revision, "Customer signs two sites", None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Pending, again.PricingApproval!.Status);
        var approved = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, again.Revision, true, "Two-site deal", None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Approved, approved.PricingApproval!.Status);
        // The full history is kept, in order: "fp:{fingerprint}:{sequence}; reason".
        var steps = (await ctx.Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == draft.QuotationId && e.EventType.StartsWith("PRICING_")).ToListAsync())
            .OrderBy(e => int.Parse(e.Detail!.Split(';')[0].Split(':')[2])).Select(e => e.EventType);
        Assert.Equal(new[] { QuotationEventTypes.PricingApprovalRequested, QuotationEventTypes.PricingRejected, QuotationEventTypes.PricingApprovalRequested,
            QuotationEventTypes.PricingApproved }, steps);
    }

    [Fact]
    public async Task Reduced_and_zero_prices_need_approval_and_a_missing_catalog_price_is_never_free()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, manager) = await RevisionDraftAsync(ctx, s, catalog);
        var panelLine = draft.Lines.Single(l => l.ProductId == catalog.Panel.Id);

        // A unit price below the catalog reference.
        var reduced = (await q.UpdateLineAsync(s.Sales.Id, draft.QuotationId, panelLine.LineId, draft.Revision, panelLine.Quantity, panelLine.UnitPrice - 100_000m, null, None)).Value!;
        var reduction = Assert.Single(reduced.PricingApproval!.Adjustments);
        Assert.Equal((PriceAdjustmentKinds.ReducedUnitPrice, panelLine.CatalogUnitPrice, panelLine.UnitPrice - 100_000m, 200_000m),
            (reduction.Kind, reduction.CatalogUnitPrice, reduction.UnitPrice, reduction.Amount));

        // An intentionally free custom item is a zero price: it needs the approval too.
        var free = (await q.AddLineAsync(s.Sales.Id, draft.QuotationId, reduced.Revision, new QuotationLineInput(null, "Free monitoring stick", "PCS", 1m, 0m, null), None)).Value!;
        Assert.Equal(new[] { PriceAdjustmentKinds.ReducedUnitPrice, PriceAdjustmentKinds.ZeroPrice }, free.PricingApproval!.Adjustments.Select(a => a.Kind));
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, (await q.IssueAsync(s.Sales.Id, draft.QuotationId, free.Revision, None)).ErrorCode);

        // A catalog product without a catalog price cannot be added without an explicit price.
        var unpriced = await ctx.ProductAsync("PNL-NO-PRICE", 0m);
        var missing = await q.AddLineAsync(s.Sales.Id, draft.QuotationId, free.Revision, new QuotationLineInput(unpriced.Id, null, null, 1m, null, null), None);
        Assert.Equal(QuotationErrorCodes.PriceMissing, missing.ErrorCode);
        Assert.Equal(400, OperationResultControllerBase.StatusFor(missing.ErrorCode!));

        var pending = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, free.Revision, "Bundle promotion", None)).Value!;
        var approved = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, true, "Approved bundle", None)).Value!;
        Assert.True((await q.IssueAsync(s.Sales.Id, draft.QuotationId, approved.Revision, None)).Succeeded);
    }

    [Fact]
    public async Task An_edit_racing_an_approval_makes_the_approval_stale_and_a_new_version_needs_its_own_approval()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, manager) = await RevisionDraftAsync(ctx, s, catalog);
        var discounted = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, WithDiscount(draft, 500_000m), None)).Value!;
        var pending = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, "Promotion", None)).Value!;

        // Sales edits the pricing while the Manager is still looking at the old revision: the Manager's decision is refused as stale,
        // and with the fresh revision there is nothing pending for the new pricing.
        var edited = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, pending.Revision, WithDiscount(pending, 700_000m), None)).Value!;
        Assert.Equal(QuotationErrorCodes.StaleRevision, (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, true, "OK", None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.PricingApprovalNotPending, (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, edited.Revision, true, "OK", None)).ErrorCode);

        var again = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, edited.Revision, "Promotion, final", None)).Value!;
        var approved = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, again.Revision, true, "OK", None)).Value!;
        var issued = (await q.IssueAsync(s.Sales.Id, draft.QuotationId, approved.Revision, None)).Value!;

        // The customer sends it back: the next version copies the discount, but the approval was for version 2 only.
        Assert.True((await q.RequestRevisionAsync(s.Request.Customer.Id, issued.QuotationId, "One more question", None)).Succeeded);
        var v3 = (await q.CreateRevisionAsync(s.Sales.Id, issued.QuotationId, None)).Value!;
        Assert.Equal((3, 700_000m, PricingApprovalStatuses.Required), (v3.Version, v3.DiscountAmount, v3.PricingApproval!.Status));
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, (await q.IssueAsync(s.Sales.Id, v3.QuotationId, v3.Revision, None)).ErrorCode);
    }

    // ------------------------------------------------------------------ D-09

    [Fact]
    public async Task Every_customer_quotation_response_shows_selling_prices_and_totals_but_no_catalog_reference_or_approval_data()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var (q, draft, manager) = await RevisionDraftAsync(ctx, s, catalog);
        var discounted = (await q.UpdateHeaderAsync(s.Sales.Id, draft.QuotationId, draft.Revision, WithDiscount(draft, 500_000m), None)).Value!;
        var pending = (await q.RequestPricingApprovalAsync(s.Sales.Id, draft.QuotationId, discounted.Revision, "Promotion", None)).Value!;
        var approved = (await q.DecidePricingApprovalAsync(manager, draft.QuotationId, pending.Revision, true, "OK", None)).Value!;
        var issued = (await q.IssueAsync(s.Sales.Id, draft.QuotationId, approved.Revision, None)).Value!;
        Assert.NotNull(issued.Lines[0].CatalogUnitPrice);   // staff keep the reference for review
        var customer = s.Request.Customer.Id;

        var list = await q.ListForCustomerAsync(customer, None);
        var detail = (await q.GetForCustomerAsync(customer, issued.QuotationId, None)).Value!;
        var accepted = (await q.AcceptAsync(customer, issued.QuotationId, None)).Value!;
        var acceptedAgain = (await q.AcceptAsync(customer, issued.QuotationId, None)).Value!;
        var older = list.Single(v => v.Version == 1);
        foreach (var view in list.Append(detail).Append(accepted).Append(acceptedAgain))
        {
            Assert.All(view.Lines, l => Assert.Null(l.CatalogUnitPrice));
            Assert.Null(view.PricingApproval);
            Assert.Null(view.PricingBasis);
            Assert.Null(view.PricingReview);
            Assert.Null(view.Events);
        }
        Assert.Equal((500_000m, issued.TaxAmount, issued.TotalAmount), (detail.DiscountAmount, detail.TaxAmount, detail.TotalAmount));
        Assert.All(detail.Lines, l => Assert.True(l.UnitPrice > 0 && l.LineTotal == QuotationPricing.LineTotal(l.Quantity, l.UnitPrice)));
        Assert.Equal(QuotationStatuses.Superseded, older.Status);

        // The revision-request response path too.
        var (ctx2, s2, catalog2) = await ReadyAsync();
        await using var __ = ctx2;
        await AutomaticQuotationFixture.CompleteAsync(ctx2, s2, catalog2.StandardBill());
        var v1 = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx2, s2.Request.Request.Id));
        var sentBack = (await ctx2.Quotations().RequestRevisionAsync(s2.Request.Customer.Id, v1.Id, "Cheaper inverter?", None)).Value!;
        Assert.All(sentBack.Lines, l => Assert.Null(l.CatalogUnitPrice));
        Assert.Null(sentBack.PricingApproval);
    }

    // ------------------------------------------------------------------ D-02

    /// <summary>The technician amends the completed survey's bill of materials.</summary>
    private static async Task<OperationResult<OnSiteSurveyView>> AmendBillAsync(InstallationWorkflowTestContext ctx, ConfirmedSurvey s, BillOfMaterialsInput bom)
    {
        var onSite = ctx.OnSiteWithAutomaticQuotation();
        var report = (await onSite.GetForTechnicianAsync(s.Technician.Id, s.AppointmentId, None)).Value!;
        return await onSite.AmendAsync(new AmendOnSiteSurveyCommand(s.Technician.Id, s.AppointmentId, "Roof re-measured: 20 panels fit",
            new OnSiteSurveyData(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, bom), null, report.Revision), None);
    }

    [Fact]
    public async Task Confirming_survey_v2_with_20_panels_while_keeping_the_2_panels_of_v1_is_refused_until_the_lines_match()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill(panels: 2));
        Assert.Equal(AutomaticQuotationOutcomes.Issued, completed.Value!.AutomaticQuotationOutcome);
        var v1 = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));
        Assert.True((await AmendBillAsync(ctx, s, catalog.StandardBill(panels: 20))).Succeeded);
        var q = ctx.Quotations();
        var v2 = (await q.CreateRevisionAsync(s.Sales.Id, v1.Id, None)).Value!;
        Assert.Equal(2m, v2.Lines.Single(l => l.ProductId == catalog.Panel.Id).Quantity);

        // Sales tries to confirm v2 while the draft still quotes the 2 panels of v1.
        var relabel = await q.ConfirmSurveyVersionAsync(s.Sales.Id, v2.QuotationId, v2.Revision, "Checked", None);
        Assert.Equal(QuotationErrorCodes.BomMismatch, relabel.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(relabel.ErrorCode!));
        var mismatch = Assert.Single(JsonDocument.Parse(JsonSerializer.Serialize(relabel.Details)).RootElement.GetProperty("mismatches").EnumerateArray());
        Assert.Equal((catalog.Panel.Id, 20m, 2m), (mismatch.GetProperty("ProductId").GetGuid(), mismatch.GetProperty("SurveyQuantity").GetDecimal(),
            mismatch.GetProperty("QuotedQuantity").GetDecimal()));
        var unchanged = (await q.GetForSalesAsync(s.Sales.Id, false, v2.QuotationId, None)).Value!;
        Assert.Equal((1, true), (unchanged.OnSiteSurveyVersion, unchanged.SurveyChanged));

        // An inverter that is not in the survey cannot slip in either.
        var extraInverter = await QuotationTests.InverterAsync(ctx, "EXTRA-INV", 9_000_000m);
        var withExtra = (await q.AddLineAsync(s.Sales.Id, v2.QuotationId, v2.Revision, new QuotationLineInput(extraInverter.Id, null, null, 1m, null, null), None)).Value!;
        var withPanels = (await q.UpdateLineAsync(s.Sales.Id, v2.QuotationId, withExtra.Lines.Single(l => l.ProductId == catalog.Panel.Id).LineId, withExtra.Revision,
            20m, null, null, None)).Value!;
        Assert.Equal(QuotationErrorCodes.BomMismatch, (await q.ConfirmSurveyVersionAsync(s.Sales.Id, v2.QuotationId, withPanels.Revision, "Checked", None)).ErrorCode);
        var corrected = (await q.RemoveLineAsync(s.Sales.Id, v2.QuotationId, withPanels.Lines.Single(l => l.ProductId == extraInverter.Id).LineId, withPanels.Revision, None)).Value!;

        // With the lines matching the 20-panel survey, the confirmation is recorded and the revision is priced at catalog prices.
        var confirmed = (await q.ConfirmSurveyVersionAsync(s.Sales.Id, v2.QuotationId, corrected.Revision, "Lines match the re-measured roof", None)).Value!;
        Assert.Equal((2, false), (confirmed.OnSiteSurveyVersion, confirmed.SurveyChanged));

        // Changing the quantity back after the confirmation is caught again at issue.
        var tampered = (await q.UpdateLineAsync(s.Sales.Id, v2.QuotationId, confirmed.Lines.Single(l => l.ProductId == catalog.Panel.Id).LineId, confirmed.Revision,
            2m, null, null, None)).Value!;
        Assert.Equal(QuotationErrorCodes.BomMismatch, (await q.IssueAsync(s.Sales.Id, v2.QuotationId, tampered.Revision, None)).ErrorCode);
        var restored = (await q.UpdateLineAsync(s.Sales.Id, v2.QuotationId, tampered.Lines.Single(l => l.ProductId == catalog.Panel.Id).LineId, tampered.Revision,
            20m, null, null, None)).Value!;
        var issued = (await q.IssueAsync(s.Sales.Id, v2.QuotationId, restored.Revision, None)).Value!;

        // The customer must accept the revised offer; the original stays in the history.
        Assert.True((await q.AcceptAsync(s.Request.Customer.Id, issued.QuotationId, None)).Succeeded);
        var stored = await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id);
        Assert.Equal(new[] { (1, QuotationStatuses.Superseded, 1, 2m), (2, QuotationStatuses.Accepted, 2, 20m) },
            stored.Select(x => (x.Version, x.Status, x.OnSiteSurveyVersion,
                ctx.Db.QuotationLines.AsNoTracking().Single(l => l.QuotationId == x.Id && l.ProductId == catalog.Panel.Id).Quantity)));
    }

    // ------------------------------------------------------------------ D-03

    [Fact]
    public async Task A_not_feasible_survey_gets_no_quotation_and_an_expired_one_cannot_be_accepted()
    {
        var (ctx, s, catalog) = await ReadyAsync();
        await using var _ = ctx;
        var completed = await AutomaticQuotationFixture.CompleteAsync(ctx, s, catalog.StandardBill(), feasibility: "NOT_FEASIBLE");
        Assert.Equal(AutomaticQuotationOutcomes.NotFeasible, completed.Value!.AutomaticQuotationOutcome);
        var manual = await ctx.Quotations().CreateAsync(s.Sales.Id, s.Request.Request.Id, new QuotationHeaderInput(1m, 0m, 0m, 0m, 0m, ctx.Clock.Now.AddDays(10), null, null), None);
        Assert.Equal(QuotationErrorCodes.SurveyNotFeasible, manual.ErrorCode);
        Assert.Equal(409, OperationResultControllerBase.StatusFor(manual.ErrorCode!));
        Assert.Empty(await AutomaticQuotationFixture.QuotationsAsync(ctx, s.Request.Request.Id));

        var (ctx2, s2, catalog2) = await ReadyAsync();
        await using var __ = ctx2;
        await AutomaticQuotationFixture.CompleteAsync(ctx2, s2, catalog2.StandardBill());
        var issued = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(ctx2, s2.Request.Request.Id));
        ctx2.Clock.Now = issued.ValidUntil!.Value.AddMinutes(1);
        var view = (await ctx2.Quotations().GetForCustomerAsync(s2.Request.Customer.Id, issued.Id, None)).Value!;
        Assert.Equal((true, false), (view.IsExpired, view.CanAccept));
        Assert.Equal(QuotationErrorCodes.Expired, (await ctx2.Quotations().AcceptAsync(s2.Request.Customer.Id, issued.Id, None)).ErrorCode);
        Assert.Equal(QuotationStatuses.Issued, (await ctx2.Db.Quotations.AsNoTracking().SingleAsync(x => x.Id == issued.Id)).Status);
    }
}
