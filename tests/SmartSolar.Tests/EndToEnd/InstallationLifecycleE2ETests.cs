using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.PreSurvey.ClaimSurveyRequest;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.Surface;
using SmartSolar.Modules.PreSurvey.SubmitPreSurvey;
using SmartSolar.Modules.Quotations;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.CreateSimulation;
using SmartSolar.Modules.SolarSimulation.Installation;
using SmartSolar.Modules.SolarSimulation.Models;
using SmartSolar.Modules.SolarSimulation.SelectSimulation;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.EndToEnd;

/// <summary>
/// Multi-module end-to-end scenarios E2E-01..E2E-10: the real production handlers of every module run in sequence on ONE database
/// (SQLite), and each step asserts the persisted state. Only provider boundaries are fakes: PV/climate providers, S3 object
/// verification and the PayOS server (no real payment). The HTTP layer (authentication, roles) is covered separately by the
/// endpoint tests; these scenarios call the handlers directly. Catalog prices and people are SYNTHETIC fixture values.
/// </summary>
public sealed class InstallationLifecycleE2ETests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateOnly Wed = Day.AddDays(1);

    private sealed record Journey(InstallationWorkflowTestContext Ctx, ConfirmedSurvey Survey, QuotationCatalog Catalog, SimulationDetail Simulation)
    {
        public Guid CustomerId => Survey.Request.Customer.Id;
        public Guid RequestId => Survey.Request.Request.Id;
    }

    /// <summary>
    /// Customer side up to a confirmed survey: site with a saved 12 × 8 m surface in Binh Tan, a simulation with the catalog panel,
    /// explicit selection, submit; the technician self-accepts the 10:00 booking and Sales claims the request.
    /// </summary>
    private static async Task<Journey> ConfirmedJourneyAsync(decimal taxRatePercent = 0m, decimal laborPrice = 2_000_000m)
    {
        var ctx = InstallationWorkflowTestContext.Create();
        ctx.QuotationOptions.DefaultTaxRatePercent = taxRatePercent;
        var scheduling = ctx.Scheduling;
        var pre = scheduling.Inner;
        var catalog = await AutomaticQuotationFixture.SeedCatalogAsync(ctx, laborPrice);
        var technician = await scheduling.SeedTechnicianAsync("Survey technician", BinhTan);

        // Profile, site and draft pre-survey (seeded as the customer would save them).
        var user = await pre.SeedUserAsync("E2E customer");
        var customer = await pre.SeedCustomerAsync(user.Id);
        await scheduling.EnsureCatalogAsync();
        var site = PreSurveyTestContext.NewPropertySite(customer.Id);
        (site.Latitude, site.Longitude, site.ProvinceCode, site.CommuneCode) = (BinhTan.Latitude, BinhTan.Longitude, Province, CommuneCodeOf(BinhTan));
        var draft = PreSurveyTestContext.NewPreSurvey(site.Id);
        (draft.SurfaceLengthM, draft.SurfaceWidthM, draft.TiltDegree, draft.AzimuthDegree) = (12m, 8m, 15m, 180m);
        draft.Obstacles = SurfaceObstacleSerializer.Serialize([]);
        (draft.GeometryVersion, draft.Revision) = (1, 1);
        ctx.Db.AddRange(site, draft);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        // Simulation with the catalog panel, explicitly selected, then submit.
        var created = await pre.CreateSimulationHandler().HandleAsync(new CreateSimulationCommand(user.Id, draft.Id, 1, catalog.Panel.Id, MountingTypes.Flush,
            null, null, new InstallationRequest(null, null, null, null, null), null), None);
        Assert.Equal(CreateSimulationOutcome.Created, created.Outcome);
        Assert.Equal(SelectSimulationResultOutcome.Selected, (await pre.SelectSimulationAsync(user.Id, draft.Id, created.Simulation!.SimulationId)).Outcome);
        var submitted = await pre.SubmitPreSurveyHandler().HandleAsync(new SubmitPreSurveyCommand(user.Id, draft.Id), None);
        Assert.Equal(SubmitPreSurveyOutcome.Submitted, submitted.Outcome);
        var requestId = submitted.SurveyRequestId!.Value;

        // Booking, technician self-acceptance, Sales claim.
        var booked = await scheduling.Book().HandleAsync(new BookSurveyAppointmentCommand(user.Id, requestId, At(10), null), None);
        Assert.True(booked.Succeeded, booked.Message);
        Assert.True((await scheduling.AcceptAsync(technician.Id, booked.Value!.AppointmentId)).Succeeded);
        var sales = await scheduling.SeedSalesAsync();
        Assert.Equal(ClaimSurveyRequestOutcome.Claimed, (await pre.ClaimSurveyRequestHandler().HandleAsync(new ClaimSurveyRequestCommand(requestId, sales.Id), None)).Outcome);

        var request = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        var survey = new ConfirmedSurvey(new SeededSurveyRequest(user, site, draft, request), technician, sales, booked.Value.AppointmentId);
        return new Journey(ctx, survey, catalog, created.Simulation);
    }

    /// <summary>Technician completes the survey with the bill of materials; asserts the outcome and returns the stored quotation.</summary>
    private static async Task<Quotation> CompleteSurveyAsync(Journey j, BillOfMaterialsInput bom, string expectedOutcome, DateTimeOffset? at = null)
    {
        var completed = await AutomaticQuotationFixture.CompleteAsync(j.Ctx, j.Survey, bom, at: at);
        Assert.True(completed.Succeeded, completed.Message);
        Assert.Equal(expectedOutcome, completed.Value!.AutomaticQuotationOutcome);
        return (await AutomaticQuotationFixture.QuotationsAsync(j.Ctx, j.RequestId)).Last();
    }

    /// <summary>Customer accepts the quotation; Sales prepares and publishes the contract (AWAITING_SIGNATURE).</summary>
    private static async Task<(ContractTests.Ready Ready, ContractView Contract)> AcceptAndPublishAsync(Journey j, Guid quotationId)
    {
        var accepted = await j.Ctx.Quotations().AcceptAsync(j.CustomerId, quotationId, None);
        Assert.True(accepted.Succeeded, accepted.Message);
        await j.Ctx.EnsureInstallationWarrantyOptionAsync();
        var ready = new ContractTests.Ready(j.Survey, quotationId, accepted.Value!.TotalAmount);
        return (ready, await ContractTests.PublishedAsync(j.Ctx, ready));
    }

    /// <summary>Physical signing: the customer uploads the signed scan and Sales approves it (SIGNED).</summary>
    private static async Task<ContractView> SignAsync(Journey j, ContractTests.Ready ready, ContractView published)
    {
        var under = await ContractTests.SubmitSignedAsync(j.Ctx, ready, published);
        var signed = await j.Ctx.Contracts().ReviewAsync(j.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Approve, null, None);
        Assert.True(signed.Succeeded, signed.Message);
        return signed.Value!;
    }

    /// <summary>One PayOS attempt for the full amount, settled by the signed webhook.</summary>
    private static async Task<PaymentAttemptView> PayAsync(Journey j, Guid contractId)
    {
        var attempt = (await j.Ctx.Payments().CreateOrGetAttemptAsync(j.CustomerId, contractId, None)).Value!;
        Assert.Equal(WebhookOutcomes.Applied, (await j.Ctx.Payments().HandleWebhookAsync(j.Ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, $"FT-{attempt.OrderCode}"), None)).Outcome);
        return attempt;
    }

    /// <summary>
    /// Manager creates and assigns the project (Wed 08:00–17:00), the installer accepts; a required grid-approval checklist item is
    /// satisfied with a verified project document; start, evidence, assets, completion report; optional customer rework; acceptance.
    /// </summary>
    private static async Task<(InstallationStaffView Project, HandoverView Handover)> InstallAndHandOverAsync(Journey j, Guid contractId, bool reworkFirst = false)
    {
        var ctx = j.Ctx;
        var installs = ctx.Installations();
        var manager = await ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var installer = await ctx.Scheduling.SeedTechnicianAsync("Installer", BinhTan);

        var project = (await installs.CreateProjectAsync(manager.Id, contractId, None)).Value!;
        var assigned = await installs.AssignAsync(manager.Id, project.ProjectId,
            new AssignTechnicianInput(installer.Id, At(8, 0, Wed), At(17, 0, Wed), null, project.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        Assert.True((await installs.AcceptAssignmentAsync(installer.Id, project.ProjectId, null, None)).Succeeded);

        project = (await installs.GetForManagerAsync(project.ProjectId, None)).Value!;
        var grid = project.Checklist.Single(i => i.Code == "GRID_CONNECTION_APPROVAL");
        var permit = await ctx.VerifiedFileAsync(manager.Id, FilePurposes.ProjectDocument, "pdf");
        var satisfied = await installs.UpdateChecklistItemAsync(manager.Id, project.ProjectId, grid.ItemId, project.Revision,
            new ChecklistUpdate(true, "SATISFIED", permit, "EVN approval letter"), None);
        Assert.True(satisfied.Succeeded, satisfied.Message);

        await InstallationTests.StartAsync(ctx, project.ProjectId, installer.Id);
        await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, installer.Id);
        Assert.True((await InstallationTests.RegisterPanelAndInverterAsync(ctx, project.ProjectId, installer.Id)).Succeeded);
        Assert.True((await installs.ReportCompletionAsync(installer.Id, project.ProjectId, "Array and inverter commissioned.", null, None)).Succeeded);

        if (reworkFirst)
        {
            var rework = (await installs.DecideAcceptanceAsync(j.CustomerId, project.ProjectId, false, "Cable tray loose on the east side", None)).Value!;
            Assert.Equal((InstallationStatuses.ReworkRequired, 2), (rework.Status, rework.CurrentRound));
            // Round-1 evidence does not count for round 2.
            Assert.Equal(InstallationErrorCodes.EvidenceRequired, (await installs.ReportCompletionAsync(installer.Id, project.ProjectId, "Fixed", null, None)).ErrorCode);
            await InstallationTests.AddCompletionEvidenceAsync(ctx, project.ProjectId, installer.Id);
            Assert.True((await installs.ReportCompletionAsync(installer.Id, project.ProjectId, "Cable tray fixed.", null, None)).Succeeded);
        }

        var completed = (await installs.DecideAcceptanceAsync(j.CustomerId, project.ProjectId, true, null, None)).Value!;
        Assert.Equal(InstallationStatuses.Completed, completed.Status);
        var handover = await installs.GetHandoverAsync(j.CustomerId, false, project.ProjectId, None);
        Assert.True(handover.Succeeded, handover.Message);
        return ((await installs.GetForManagerAsync(project.ProjectId, None)).Value!, handover.Value!);
    }

    private static async Task<List<string>> QuotationEventsAsync(InstallationWorkflowTestContext ctx, Guid quotationId)
        => await ctx.Db.QuotationEvents.AsNoTracking().Where(e => e.QuotationId == quotationId).OrderBy(e => e.CreatedAt).Select(e => e.EventType).ToListAsync();

    // ------------------------------------------------------------------ E2E-01

    [Fact]
    public async Task E2E_01_standard_installation_runs_from_pre_survey_to_handover_without_a_manual_quotation()
    {
        // Arrange: synthetic 8 % tax, standard bill (2 panels, inverter, labor, transport).
        var j = await ConfirmedJourneyAsync(taxRatePercent: 8m);
        await using var _ = j.Ctx;

        // Act
        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);
        var (ready, published) = await AcceptAndPublishAsync(j, quotation.Id);
        var signed = await SignAsync(j, ready, published);
        var attempt = await PayAsync(j, signed.ContractId);
        var (project, handover) = await InstallAndHandOverAsync(j, signed.ContractId);

        // Assert: amounts — subtotal 18,500,000; tax 8 % = 1,480,000; total 19,980,000 VND.
        Assert.Equal((18_500_000m, 1_480_000m, 19_980_000m), (quotation.Subtotal, quotation.TaxAmount, quotation.TotalAmount));
        Assert.Equal(quotation.TotalAmount, signed.TotalAmount);
        Assert.Equal(quotation.TotalAmount, attempt.Amount);

        // Assert: persisted relationships across modules.
        var db = j.Ctx.Db;
        var request = await db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == j.RequestId);
        var preSurvey = await db.PreSurveys.AsNoTracking().SingleAsync(p => p.Id == request.PreSurveyId);
        var report = await db.OnSiteSurveyReports.AsNoTracking().SingleAsync(r => r.SurveyRequestId == j.RequestId);
        var stored = await db.Quotations.AsNoTracking().SingleAsync(q => q.SurveyRequestId == j.RequestId);
        var contract = await db.CustomerContracts.AsNoTracking().SingleAsync(c => c.QuotationId == stored.Id);
        var payment = await db.PaymentAttempts.AsNoTracking().SingleAsync(p => p.ContractId == contract.Id);
        var storedProject = await db.InstallationProjects.AsNoTracking().SingleAsync(p => p.ContractId == contract.Id);

        Assert.Equal(PreSurveyStatus.Submitted, preSurvey.Status);
        Assert.Equal(j.Simulation.SimulationId, preSurvey.SelectedSimulationId);
        Assert.Equal((j.Survey.Sales.Id, OnSiteSurveyStatuses.Completed), (request.AssignedSaleId!.Value, report.Status));
        Assert.Equal((report.Id, 1, QuotationOrigins.Automatic, QuotationStatuses.Accepted),
            (stored.OnSiteSurveyReportId, stored.OnSiteSurveyVersion, stored.Origin, stored.Status));
        Assert.Equal((ContractStatuses.Paid, stored.TotalAmount), (contract.Status, contract.TotalAmount));
        Assert.Equal(PaymentStatuses.Paid, payment.Status);
        Assert.Equal((j.RequestId, InstallationStatuses.Completed), (storedProject.SurveyRequestId, storedProject.Status));
        Assert.Equal(project.ProjectId, handover.ProjectId);
        Assert.Equal(3, handover.Assets.Count);
        // Generated and issued are written in the same transaction (same timestamp), so compare without order.
        Assert.Equal(new[] { QuotationEventTypes.Accepted, QuotationEventTypes.AutomaticallyGenerated, QuotationEventTypes.Issued },
            (await QuotationEventsAsync(j.Ctx, stored.Id)).Order());

        // Monitoring: the customer sees the expected production of the simulation they selected, clearly labelled as simulated.
        var monitoring = new EnergyMonitoringHandlers(new SmartSolar.Infrastructure.Persistence.InstallationWorkflow.InstallationUnitOfWork(db));
        var own = (await monitoring.GetAsync(j.CustomerId, false, project.ProjectId, None)).Value!;
        Assert.Equal((EnergyMonitoringValues.Simulated, false, EnergyMonitoringValues.NoMeasuredData, EnergyMonitoringValues.NotConfigured),
            (own.DataSource, own.MeasuredDataAvailable, own.Status, own.AlertsStatus));
        Assert.Equal((j.Simulation.SimulationId, j.Simulation.Layout.InstalledCapacityKwp), (own.Simulated!.SimulationId, own.Simulated.InstalledCapacityKwp));
        Assert.Equal(j.Simulation.Energy.AnnualEnergyKwh, own.Simulated.AnnualEnergyKwh);
        Assert.Empty(own.Alerts);
        Assert.Equal(InstallationErrorCodes.ProjectNotFound, (await monitoring.GetAsync(j.Survey.Technician.Id, false, project.ProjectId, None)).ErrorCode);
        Assert.True((await monitoring.GetAsync(j.Survey.Sales.Id, true, project.ProjectId, None)).Succeeded);
    }

    // ------------------------------------------------------------------ E2E-02

    [Fact]
    public async Task E2E_02_standard_quotation_needs_no_sales_action_and_every_retry_is_idempotent()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);

        // Repeated completion, repeated generation, and a stray manual Issue never create or change anything.
        var again = await j.Ctx.OnSiteWithAutomaticQuotation().CompleteAsync(new CompleteOnSiteSurveyCommand(j.Survey.Technician.Id, j.Survey.AppointmentId, 0), None);
        var generated = await j.Ctx.Quotations().GenerateForStaffAsync(j.Survey.Sales.Id, false, j.RequestId, None);
        var manualIssue = await j.Ctx.Quotations().IssueAsync(j.Survey.Sales.Id, quotation.Id, quotation.Revision, None);

        Assert.Equal(AutomaticQuotationOutcomes.AlreadyGenerated, again.Value!.AutomaticQuotationOutcome);
        Assert.Equal((AutomaticQuotationOutcomes.AlreadyGenerated, quotation.Id), (generated.Value!.Outcome, generated.Value.Quotation!.QuotationId));
        Assert.Equal(QuotationErrorCodes.NotDraft, manualIssue.ErrorCode);
        var stored = Assert.Single(await AutomaticQuotationFixture.QuotationsAsync(j.Ctx, j.RequestId));
        Assert.Equal((QuotationStatuses.Issued, quotation.Revision, 18_500_000m), (stored.Status, stored.Revision, stored.TotalAmount));
        Assert.Single(await QuotationEventsAsync(j.Ctx, stored.Id), e => e == QuotationEventTypes.Issued);
        Assert.Empty(await j.Ctx.Quotations().ListPendingReviewAsync(j.Survey.Sales.Id, false, None));
    }

    // ------------------------------------------------------------------ E2E-03

    [Fact]
    public async Task E2E_03_a_missing_catalog_price_goes_to_sales_review_and_continues_after_sales_issues_it()
    {
        var j = await ConfirmedJourneyAsync(laborPrice: 0m);
        await using var _ = j.Ctx;
        var draft = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.PendingSalesReview);

        // The customer never sees a draft; Sales finds it in the review queue with the reason.
        Assert.Equal(QuotationStatuses.Draft, draft.Status);
        Assert.Equal(QuotationErrorCodes.NotFound, (await j.Ctx.Quotations().GetForCustomerAsync(j.CustomerId, draft.Id, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await j.Ctx.Quotations().AcceptAsync(j.CustomerId, draft.Id, None)).ErrorCode);
        var queued = Assert.Single(await j.Ctx.Quotations().ListPendingReviewAsync(j.Survey.Sales.Id, false, None));
        Assert.Contains(queued.PricingBasis!.Issues, i => i.Code == PricingIssueCodes.PriceMissing && i.ProductId == j.Catalog.Labor.Id);
        Assert.Equal(ContractErrorCodes.QuotationNotAccepted,
            (await j.Ctx.Contracts().CreateAsync(j.Survey.Sales.Id, draft.Id, "Standard terms", None)).ErrorCode);

        // Sales enters the agreed labor cost and issues; the flow continues normally.
        var header = new QuotationHeaderInput(2_000_000m, 500_000m, 0m, 0m, 0m, queued.ValidUntil, queued.Terms, "Labor priced by Sales");
        var updated = await j.Ctx.Quotations().UpdateHeaderAsync(j.Survey.Sales.Id, draft.Id, queued.Revision, header, None);
        Assert.True(updated.Succeeded, updated.Message);
        var issued = await j.Ctx.Quotations().IssueAsync(j.Survey.Sales.Id, draft.Id, updated.Value!.Revision, None);
        Assert.True(issued.Succeeded, issued.Message);
        var (ready, published) = await AcceptAndPublishAsync(j, draft.Id);
        var signed = await SignAsync(j, ready, published);

        Assert.Equal((18_500_000m, 18_500_000m), (issued.Value!.TotalAmount, signed.TotalAmount));
        Assert.Empty(await j.Ctx.Quotations().ListPendingReviewAsync(j.Survey.Sales.Id, false, None));
        Assert.Equal(QuotationOrigins.Automatic, (await j.Ctx.Db.Quotations.AsNoTracking().SingleAsync(q => q.Id == draft.Id)).Origin);
    }

    // ------------------------------------------------------------------ E2E-04

    [Fact]
    public async Task E2E_04_a_customer_revision_with_an_authorized_discount_supersedes_the_old_version_and_drives_the_contract()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var v1 = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);
        var quotations = j.Ctx.Quotations();
        var otherSales = await j.Ctx.Scheduling.SeedSalesAsync("Other sales");

        Assert.True((await quotations.RequestRevisionAsync(j.CustomerId, v1.Id, "Please apply the 500,000 VND promotion", None)).Succeeded);
        var v2 = (await quotations.CreateRevisionAsync(j.Survey.Sales.Id, v1.Id, None)).Value!;
        var discounted = new QuotationHeaderInput(v2.LaborCost, v2.TransportCost, v2.OtherServiceCost, 500_000m, 0m, v2.ValidUntil, v2.Terms, "Promotion");

        // Only the owning Sales edits; the technician and another Sales get 404; a stale revision is refused.
        Assert.Equal(QuotationErrorCodes.NotFound, (await quotations.UpdateHeaderAsync(otherSales.Id, v2.QuotationId, v2.Revision, discounted, None)).ErrorCode);
        Assert.Equal(QuotationErrorCodes.NotFound, (await quotations.UpdateHeaderAsync(j.Survey.Technician.Id, v2.QuotationId, v2.Revision, discounted, None)).ErrorCode);
        var updated = (await quotations.UpdateHeaderAsync(j.Survey.Sales.Id, v2.QuotationId, v2.Revision, discounted, None)).Value!;
        Assert.Equal(QuotationErrorCodes.StaleRevision, (await quotations.UpdateHeaderAsync(j.Survey.Sales.Id, v2.QuotationId, v2.Revision, discounted, None)).ErrorCode);

        // D-01: the discount needs a Manager approval of exactly this pricing before Sales can issue.
        Assert.Equal(QuotationErrorCodes.PricingApprovalRequired, (await quotations.IssueAsync(j.Survey.Sales.Id, v2.QuotationId, updated.Revision, None)).ErrorCode);
        var requested = (await quotations.RequestPricingApprovalAsync(j.Survey.Sales.Id, v2.QuotationId, updated.Revision, "Customer promotion campaign", None)).Value!;
        var manager = await j.Ctx.Scheduling.Inner.SeedUserAsync("Pricing manager");
        var approved = (await quotations.DecidePricingApprovalAsync(manager.Id, v2.QuotationId, requested.Revision, true, "Promotion within the campaign budget", None)).Value!;
        Assert.Equal(PricingApprovalStatuses.Approved, approved.PricingApproval!.Status);
        var issued = (await quotations.IssueAsync(j.Survey.Sales.Id, v2.QuotationId, approved.Revision, None)).Value!;

        // The old version can no longer be accepted; the contract is built from version 2.
        Assert.Equal(QuotationErrorCodes.NotIssued, (await quotations.AcceptAsync(j.CustomerId, v1.Id, None)).ErrorCode);
        var (ready, published) = await AcceptAndPublishAsync(j, issued.QuotationId);

        Assert.Equal((2, 18_500_000m, 500_000m, 18_000_000m), (issued.Version, issued.Subtotal, issued.DiscountAmount, issued.TotalAmount));
        var all = await AutomaticQuotationFixture.QuotationsAsync(j.Ctx, j.RequestId);
        Assert.Equal(new[] { (1, QuotationStatuses.Superseded), (2, QuotationStatuses.Accepted) }, all.Select(q => (q.Version, q.Status)));
        Assert.Equal((issued.QuotationId, 2, 18_000_000m), (published.QuotationId, published.QuotationVersion, published.TotalAmount));
        Assert.Equal(ContractErrorCodes.QuotationNotAccepted, (await j.Ctx.Contracts().CreateAsync(j.Survey.Sales.Id, v1.Id, "Standard terms", None)).ErrorCode);
        Assert.Equal(issued.QuotationId, ready.QuotationId);
    }

    // ------------------------------------------------------------------ E2E-05

    [Fact]
    public async Task E2E_05_a_rejected_signature_is_resubmitted_and_payment_stays_closed_until_approval()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);
        var (ready, published) = await AcceptAndPublishAsync(j, quotation.Id);
        var payments = j.Ctx.Payments();

        Assert.Equal(PaymentErrorCodes.ContractNotSigned, (await payments.CreateOrGetAttemptAsync(j.CustomerId, published.ContractId, None)).ErrorCode);
        var under = await ContractTests.SubmitSignedAsync(j.Ctx, ready, published);
        Assert.Equal(PaymentErrorCodes.ContractNotSigned, (await payments.CreateOrGetAttemptAsync(j.CustomerId, under.ContractId, None)).ErrorCode);
        var rejected = (await j.Ctx.Contracts().ReviewAsync(j.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Reject, "Page 2 not signed", None)).Value!;
        Assert.Equal(ContractStatuses.SignatureRejected, rejected.Status);
        Assert.Equal(PaymentErrorCodes.ContractNotSigned, (await payments.CreateOrGetAttemptAsync(j.CustomerId, rejected.ContractId, None)).ErrorCode);

        var resubmitted = await ContractTests.SubmitSignedAsync(j.Ctx, ready, rejected);
        var signed = (await j.Ctx.Contracts().ReviewAsync(j.Survey.Sales.Id, false, resubmitted.ContractId, resubmitted.Revision, ReviewDecision.Approve, null, None)).Value!;
        var attempt = await PayAsync(j, signed.ContractId);

        Assert.Equal(2, resubmitted.Submissions!.Count);
        Assert.Equal(ContractStatuses.Signed, signed.Status);
        Assert.Equal(PaymentStatuses.Paid, (await j.Ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(p => p.Id == attempt.PaymentAttemptId)).Status);
        Assert.Equal(1, await j.Ctx.Db.PaymentAttempts.CountAsync(p => p.ContractId == signed.ContractId));
        Assert.Equal(ContractStatuses.Paid, (await j.Ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == signed.ContractId)).Status);
    }

    // ------------------------------------------------------------------ E2E-06

    [Fact]
    public async Task E2E_06_a_lost_webhook_is_recovered_by_reconciliation_and_the_late_webhook_settles_nothing_twice()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);
        var (ready, published) = await AcceptAndPublishAsync(j, quotation.Id);
        var signed = await SignAsync(j, ready, published);
        var payments = j.Ctx.Payments();
        var attempt = (await payments.CreateOrGetAttemptAsync(j.CustomerId, signed.ContractId, None)).Value!;

        // PayOS has the money but the webhook never arrived.
        j.Ctx.PayOs.Links[attempt.OrderCode] = ("PAID", (long)attempt.Amount, "FT-LOST");
        j.Ctx.Clock.Now = j.Ctx.Clock.Now.AddMinutes(1);
        var reconciled = await payments.ReconcileAsync(attempt.PaymentAttemptId, true, None);
        var late = await payments.HandleWebhookAsync(j.Ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, "FT-LOST"), None);
        var manager = await j.Ctx.Scheduling.Inner.SeedUserAsync("Manager");
        var project = await j.Ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None);
        var duplicate = await j.Ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None);

        Assert.Equal(PaymentStatuses.Paid, reconciled!.Status);
        Assert.Equal(WebhookOutcomes.Applied, late.Outcome);   // acknowledged, idempotent
        var view = (await payments.GetForStaffAsync(signed.ContractId, None)).Value!;
        Assert.Equal(ContractStatuses.Paid, view.ContractStatus);
        Assert.Single(view.History!, e => e.EventType == "PAID");
        Assert.Single(view.Attempts, a => a.Status == PaymentStatuses.Paid);
        Assert.True(project.Succeeded, project.Message);
        Assert.Equal(InstallationErrorCodes.ProjectExists, duplicate.ErrorCode);
        Assert.Equal(1, await j.Ctx.Db.InstallationProjects.CountAsync(p => p.ContractId == signed.ContractId));
    }

    // ------------------------------------------------------------------ E2E-07

    [Fact]
    public async Task E2E_07_an_expired_payment_is_retried_and_the_project_waits_for_the_verified_payment()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);
        var (ready, published) = await AcceptAndPublishAsync(j, quotation.Id);
        var signed = await SignAsync(j, ready, published);
        var payments = j.Ctx.Payments();
        var manager = await j.Ctx.Scheduling.Inner.SeedUserAsync("Manager");

        var first = (await payments.CreateOrGetAttemptAsync(j.CustomerId, signed.ContractId, None)).Value!;
        j.Ctx.PayOs.Links[first.OrderCode] = ("EXPIRED", 0, null);
        j.Ctx.Clock.Now = first.ExpiresAt.AddMinutes(1);
        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await j.Ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None)).ErrorCode);

        var second = (await payments.CreateOrGetAttemptAsync(j.CustomerId, signed.ContractId, None)).Value!;
        Assert.NotEqual(first.PaymentAttemptId, second.PaymentAttemptId);
        Assert.Equal(InstallationErrorCodes.ContractNotPaid, (await j.Ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None)).ErrorCode);
        Assert.Equal(WebhookOutcomes.Applied,
            (await payments.HandleWebhookAsync(j.Ctx.PayOs.Webhook(second.OrderCode, (long)second.Amount, "FT-RETRY"), None)).Outcome);
        var project = await j.Ctx.Installations().CreateProjectAsync(manager.Id, signed.ContractId, None);

        var view = (await payments.GetForStaffAsync(signed.ContractId, None)).Value!;
        Assert.Equal(PaymentStatuses.Expired, view.Attempts.Single(a => a.PaymentAttemptId == first.PaymentAttemptId).Status);
        Assert.Equal(PaymentStatuses.Paid, view.Attempts.Single(a => a.PaymentAttemptId == second.PaymentAttemptId).Status);
        Assert.Equal((ContractStatuses.Paid, quotation.TotalAmount), (view.ContractStatus, second.Amount));
        Assert.True(project.Succeeded, project.Message);
    }

    // ------------------------------------------------------------------ E2E-08

    [Fact]
    public async Task E2E_08_customer_rework_opens_a_second_round_before_acceptance_and_handover()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued);
        var (ready, published) = await AcceptAndPublishAsync(j, quotation.Id);
        var signed = await SignAsync(j, ready, published);
        await PayAsync(j, signed.ContractId);

        var (project, handover) = await InstallAndHandOverAsync(j, signed.ContractId, reworkFirst: true);

        Assert.Equal((InstallationStatuses.Completed, 2), (project.Status, project.CurrentRound));   // BL-17: the last round, not round 3
        Assert.Equal(new[] { AcceptanceDecisions.ReworkRequested, AcceptanceDecisions.Accepted }, handover.Acceptances.Select(a => a.Decision));
        Assert.Equal(new[] { 1, 2 }, await j.Ctx.Db.InstallationEvidence.Where(e => e.ProjectId == project.ProjectId).OrderBy(e => e.Round).Select(e => e.Round).ToListAsync());
        Assert.Equal(2, handover.Evidence.Count);
        Assert.Equal(2, await j.Ctx.Db.InstallationAcceptances.CountAsync(a => a.ProjectId == project.ProjectId));
        Assert.Single(await j.Ctx.Db.InstallationEvents.Where(e => e.ProjectId == project.ProjectId && e.EventType == InstallationEventTypes.CustomerAccepted).ToListAsync());
    }

    // ------------------------------------------------------------------ E2E-09

    [Fact]
    public async Task E2E_09_an_overdue_survey_is_recovered_by_sales_and_the_completed_survey_still_issues_the_quotation()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var scheduling = j.Ctx.Scheduling;
        var original = await scheduling.AppointmentAsync(j.Survey.AppointmentId);
        j.Ctx.Clock.Now = original.EndsAt.AddMinutes(1).ToUniversalTime();   // the technician never came

        Assert.Equal(SchedulingErrorCodes.RecoveryReasonRequired, (await scheduling.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(j.Survey.Sales.Id, j.RequestId, At(10, 0, Wed), " ", null), None)).ErrorCode);
        var recovered = await scheduling.SalesReschedule().HandleAsync(
            new SalesRescheduleCommand(j.Survey.Sales.Id, j.RequestId, At(10, 0, Wed), "Customer was not at home", null), None);
        Assert.True(recovered.Succeeded, $"{recovered.ErrorCode}: {recovered.Message}");
        var newAppointmentId = recovered.Value!.AppointmentId;
        if (recovered.Value.AppointmentStatus != AppointmentStatuses.Accepted)
            Assert.True((await scheduling.AcceptAsync(j.Survey.Technician.Id, newAppointmentId)).Succeeded);

        var rescheduled = j with { Survey = j.Survey with { AppointmentId = newAppointmentId } };
        var quotation = await CompleteSurveyAsync(rescheduled, j.Catalog.StandardBill(), AutomaticQuotationOutcomes.Issued, at: At(10, 0, Wed));

        Assert.Equal(AppointmentStatuses.Cancelled, (await scheduling.AppointmentAsync(j.Survey.AppointmentId)).Status);
        var report = await j.Ctx.Db.OnSiteSurveyReports.AsNoTracking().SingleAsync(r => r.SurveyRequestId == j.RequestId);
        Assert.Equal((newAppointmentId, OnSiteSurveyStatuses.Completed), (report.AppointmentId, report.Status));
        Assert.Equal((QuotationStatuses.Issued, report.Id), (quotation.Status, quotation.OnSiteSurveyReportId));
    }

    // ------------------------------------------------------------------ E2E-10

    [Fact]
    public async Task E2E_10_the_quotation_references_the_selected_simulation_and_prices_only_the_confirmed_bill()
    {
        var j = await ConfirmedJourneyAsync();
        await using var _ = j.Ctx;
        var panels = j.Simulation.Layout.PanelCount;
        Assert.True(panels > 0);

        var quotation = await CompleteSurveyAsync(j, j.Catalog.StandardBill(panels), AutomaticQuotationOutcomes.Issued);
        Assert.Null((await j.Ctx.Quotations().GetForCustomerAsync(j.CustomerId, quotation.Id, None)).Value!.PricingBasis);
        var view = (await j.Ctx.Quotations().GetForSalesAsync(j.Survey.Sales.Id, false, quotation.Id, None)).Value!;

        // The simulation is a reference (capacity, link); the price comes from catalog units × the technician's quantities, never from kWh.
        var basis = view.PricingBasis!;
        Assert.Equal(j.Simulation.SimulationId, basis.SelectedSimulationId);
        Assert.Equal(panels * 550m / 1000m, basis.InstalledCapacityKwp);
        Assert.Equal(j.Simulation.Layout.InstalledCapacityKwp, basis.InstalledCapacityKwp!.Value, 2);
        Assert.Equal(panels * 3_000_000m + 10_000_000m, view.EquipmentSubtotal);
        Assert.Equal(view.EquipmentSubtotal + 2_000_000m + 500_000m, view.TotalAmount);
        Assert.Equal(panels, view.Lines.Single(l => l.ProductId == j.Catalog.Panel.Id).Quantity);
    }
}
