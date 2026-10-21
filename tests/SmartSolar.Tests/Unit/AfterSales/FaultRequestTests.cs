using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// Flow 3 fault requests: warranty snapshot, duplicate warning, free inspection, coverage per item, versioned paid quotations with
/// PayOS before work, repair warranty, final acceptance / rework, cancellation and asset replacement.
/// </summary>
public sealed class FaultRequestTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static DiagnosedItemInput Item(string description, Guid? assetId = null) => new(assetId, description, $"{description}: diagnosis", null);

    [Fact]
    public async Task Warranty_runs_from_handover_for_the_months_snapshotted_in_the_contract()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();

        // An Admin change after signing does not touch the contract snapshot.
        Assert.True((await ctx.Packages().SaveWarrantyOptionAsync(h.AdminId, WarrantyOptionKinds.Installation, 24, true, true, None)).Succeeded);
        var warranty = (await ctx.Customer().GetWarrantyAsync(h.CustomerId, h.ProjectId, None)).Value!;
        Assert.Equal(InstallationWarrantyMonths, warranty.WarrantyMonths);
        Assert.Equal(h.HandoverAt.AddMonths(InstallationWarrantyMonths), warranty.WarrantyEndsAt);
        Assert.True(warranty.UnderWarranty);
        Assert.Equal(3, warranty.Assets.Count);
        Assert.Equal(AfterSalesErrorCodes.ProjectNotFound, (await ctx.Customer().GetWarrantyAsync(h.TechnicianId, h.ProjectId, None)).ErrorCode);
    }

    [Fact]
    public async Task Fault_requests_validate_ownership_and_warn_about_duplicates_without_blocking()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var customer = ctx.Customer();

        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await customer.CreateFaultAsync(h.CustomerId, new CreateFaultInput(h.ProjectId, "bad", null, null, null, null, null), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ProjectNotFound, (await customer.CreateFaultAsync(h.SalesId, new CreateFaultInput(h.ProjectId, "Inverter is off", null, null, null, null, null), None)).ErrorCode);

        var first = await ctx.FaultAsync(h, assetId: h.InverterId);
        Assert.Equal(ServiceRequestStatuses.Submitted, first.Status);
        Assert.True(first.WithinInstallationWarranty);
        Assert.Empty(first.PossibleDuplicates);
        var second = await ctx.FaultAsync(h, "Inverter still beeps");
        Assert.Equal(first.RequestId, Assert.Single(second.PossibleDuplicates).RequestId);
        Assert.Equal(2, (await customer.ListAsync(h.CustomerId, None)).Count);

        // The Manager may close the duplicate; it keeps the link to the original.
        var closed = (await ctx.Manager().MarkDuplicateAsync(h.ManagerId, second.RequestId, first.RequestId, "same inverter", None)).Value!;
        Assert.Equal(ServiceRequestStatuses.ClosedDuplicate, closed.Status);
        Assert.Equal(first.RequestId, closed.DuplicateOfRequestId);
        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await customer.GetAsync(h.SalesId, first.RequestId, None)).ErrorCode);
    }

    [Fact]
    public async Task No_fault_found_is_free_and_closes_on_the_customer_acceptance()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var visit = await ctx.VisitStartedAsync(h, request.RequestId, Later(3));

        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await ctx.Technician().ReportInspectionAsync(h.TechnicianId, visit, "ok", true, [Item("x")], stayOnSite: false, None)).ErrorCode);
        Assert.True((await ctx.Technician().ReportInspectionAsync(h.TechnicianId, visit, "All strings within range", true, [], stayOnSite: false, None)).Succeeded);
        var pending = (await ctx.Customer().GetAsync(h.CustomerId, request.RequestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.PendingCustomerAcceptance, pending.Status);
        Assert.Equal(ServiceOutcomes.NoFaultFound, pending.Outcome);

        var done = (await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, true, null, null, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.Completed, done.Status);
        Assert.Empty(done.Quotations);
        Assert.False(await ctx.Db.PaymentAttempts.AnyAsync(a => a.ContractId == null));   // nothing was charged
    }

    [Fact]
    public async Task Warranty_items_are_authorized_at_once_while_installation_warranty_is_refused_after_it_ended()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Inverter fan", h.InverterId));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.InstallationWarranty));
        var view = (await ctx.Manager().GetAsync(request.RequestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, view.Status);
        Assert.Equal(ItemStatuses.Authorized, Assert.Single(view.Items).Status);
        Assert.Empty(await ctx.Tasks().ListOpenAsync(None));   // no quotation for free work

        // Eligibility is frozen at submission: a fault reported after the warranty ended cannot be covered by it.
        ctx.Clock.Now = h.HandoverAt.AddMonths(InstallationWarrantyMonths).AddDays(1).ToUniversalTime();
        var late = await ctx.FaultAsync(h, "Panel cracked");
        Assert.False(late.WithinInstallationWarranty);
        var lateItems = await ctx.InspectedAsync(h, late.RequestId, ctx.Clock.GetUtcNow().AddDays(3), Item("Panel R1-C1"));
        var refused = await ctx.Manager().DecideCoverageAsync(h.ManagerId, late.RequestId, [new CoverageDecision(lateItems[0], ItemCoverages.InstallationWarranty, null)], None);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, refused.ErrorCode);
    }

    [Fact]
    public async Task Paid_repair_needs_a_claimed_task_a_complete_versioned_quotation_and_payment_before_work()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Cable"), Item("Breaker"));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.Chargeable), (items[1], ItemCoverages.Chargeable));

        var line1 = new QuotationLineInput(items[0], null, 300_000, 200_000);
        var line2 = new QuotationLineInput(items[1], null, 400_000, 100_000);
        Assert.Equal(CommonErrorCodes.Forbidden, (await ctx.Quotations().IssueAsync(h.SalesId, request.RequestId, new IssueQuotationInput([line1, line2], null, null), None)).ErrorCode);
        await ctx.ClaimQuotationTaskAsync(h, request.RequestId);
        // The customer accepts the whole chargeable part: every item must be priced exactly once.
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await ctx.Quotations().IssueAsync(h.SalesId, request.RequestId, new IssueQuotationInput([line1], null, null), None)).ErrorCode);

        var v1 = await ctx.IssueAsync(h, request.RequestId, line1, line2);
        var v2 = await ctx.IssueAsync(h, request.RequestId, line1, line2 with { LaborAmount = 50_000 });
        Assert.Equal((1, 2), (v1.Version, v2.Version));
        Assert.Equal(950_000m, v2.CustomerAmount);
        Assert.Equal(RepairWarrantyMonths, v2.RepairWarrantyMonths);   // the Admin default repair warranty
        var v1Stored = await ctx.Db.ServiceQuotations.AsNoTracking().SingleAsync(q => q.Id == v1.QuotationId);
        Assert.Equal((QuotationStatuses.Superseded, 1_000_000m), (v1Stored.Status, v1Stored.TotalAmount));   // the old version is kept unchanged
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().AcceptAsync(h.CustomerId, v1.QuotationId, None)).ErrorCode);

        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Quotations().CheckoutAsync(h.CustomerId, v2.QuotationId, None)).ErrorCode);
        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, v2.QuotationId, None)).Succeeded);
        var checkout = (await ctx.Quotations().CheckoutAsync(h.CustomerId, v2.QuotationId, None)).Value!;
        Assert.Equal(950_000m, checkout.Amount);
        Assert.Equal(checkout.OrderCode, (await ctx.Quotations().CheckoutAsync(h.CustomerId, v2.QuotationId, None)).Value!.OrderCode);   // one live link

        // A repair visit cannot start before the payment: no authorized item yet.
        var ready = (await ctx.Manager().GetAsync(request.RequestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.AwaitingCustomer, ready.Status);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Manager().ScheduleVisitAsync(h.ManagerId, request.RequestId,
            new ScheduleVisitInput(h.TechnicianId, Later(6), null, null, ready.Revision), None)).ErrorCode);

        var attempt = await ctx.PayAsync(checkout);
        Assert.Equal(PaymentStatuses.Paid, attempt!.Status);
        Assert.Equal(PaymentItemStatuses.Settled, (await ctx.Db.PaymentAttemptItems.AsNoTracking().SingleAsync(i => i.PaymentAttemptId == attempt.Id)).Status);
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, (await ctx.Manager().GetAsync(request.RequestId, None)).Value!.Status);

        var repair = await ctx.VisitStartedAsync(h, request.RequestId, Later(6));
        var reported = await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Replaced cable and breaker",
            [new ItemResultInput(items[0], "replaced", "4mm cable"), new ItemResultInput(items[1], "replaced", "C16 breaker")], [], null, None);
        Assert.True(reported.Succeeded, reported.Message);
        var pending = (await ctx.Customer().GetAsync(h.CustomerId, request.RequestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.PendingCustomerAcceptance, pending.Status);
        // The repair warranty starts at the completion report.
        Assert.Equal(ctx.Clock.GetUtcNow(), pending.RepairWarrantyStartsAt);
        Assert.Equal(ctx.Clock.GetUtcNow().AddMonths(RepairWarrantyMonths), pending.RepairWarrantyEndsAt);
    }

    [Fact]
    public async Task Quotations_expire_after_the_validity_period_and_go_back_to_the_sales_queue()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Cable"));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.Chargeable));
        await ctx.ClaimQuotationTaskAsync(h, request.RequestId);
        var quotation = await ctx.IssueAsync(h, request.RequestId, new QuotationLineInput(items[0], null, 100_000, 0));
        Assert.Equal(ctx.Clock.GetUtcNow().AddDays(7), quotation.ExpiresAt);

        ctx.Clock.Now = quotation.ExpiresAt.ToUniversalTime().AddMinutes(1);
        Assert.Equal(AfterSalesErrorCodes.QuotationExpired, (await ctx.Quotations().AcceptAsync(h.CustomerId, quotation.QuotationId, None)).ErrorCode);
        Assert.Equal(1, (await ctx.Sweep().RunOnceAsync(None)).ExpiredQuotations);
        var view = (await ctx.Manager().GetAsync(request.RequestId, None)).Value!;
        Assert.Equal(QuotationStatuses.Expired, Assert.Single(view.Quotations).Status);
        Assert.Equal(ServiceRequestStatuses.AwaitingQuotation, view.Status);
        Assert.Single(await ctx.Tasks().ListOpenAsync(None));
    }

    [Fact]
    public async Task Rejecting_the_only_quotation_closes_the_request_without_charging()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Cable"));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.Chargeable));
        await ctx.ClaimQuotationTaskAsync(h, request.RequestId);
        var quotation = await ctx.IssueAsync(h, request.RequestId, new QuotationLineInput(items[0], null, 100_000, 0));

        var rejected = (await ctx.Quotations().RejectAsync(h.CustomerId, quotation.QuotationId, "Too expensive", None)).Value!;
        Assert.Equal(ServiceRequestStatuses.Cancelled, rejected.Status);
        Assert.Equal(ItemStatuses.Declined, Assert.Single(rejected.Items).Status);
        Assert.Equal("Too expensive", rejected.CancellationReason);
    }

    [Fact]
    public async Task Rejected_completion_goes_to_the_manager_and_rework_restarts_the_repair_warranty()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Cable"));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.Chargeable));
        await ctx.ClaimQuotationTaskAsync(h, request.RequestId);
        var quotation = await ctx.IssueAsync(h, request.RequestId, new QuotationLineInput(items[0], null, 100_000, 0));
        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, quotation.QuotationId, None)).Succeeded);
        await ctx.PayAsync((await ctx.Quotations().CheckoutAsync(h.CustomerId, quotation.QuotationId, None)).Value!);
        var repair = await ctx.VisitStartedAsync(h, request.RequestId, Later(6));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Fixed", [new ItemResultInput(items[0], "ok", null)], [], null, None)).Succeeded);
        var firstStart = (await ctx.Manager().GetAsync(request.RequestId, None)).Value!.RepairWarrantyStartsAt;

        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, false, " ", null, None)).ErrorCode);
        Assert.Equal(ServiceRequestStatuses.Disputed, (await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, false, "Still trips", null, None)).Value!.Status);
        Assert.Equal(ServiceRequestStatuses.ReworkRequired, (await ctx.Manager().ReviewDisputeAsync(h.ManagerId, request.RequestId, true, "Valid complaint", None)).Value!.Status);

        var rework = await ctx.VisitStartedAsync(h, request.RequestId, Later(9));
        Assert.Equal(VisitPurposes.Rework, (await ctx.Technician().GetAsync(h.TechnicianId, rework, None)).Value!.Purpose);
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, rework, "Re-terminated cable", [], [], null, None)).Succeeded);
        var reworked = (await ctx.Manager().GetAsync(request.RequestId, None)).Value!;
        Assert.True(reworked.RepairWarrantyStartsAt > firstStart);
        Assert.Equal(ServiceRequestStatuses.Completed, (await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, true, null, null, None)).Value!.Status);

        // A fault within the paid repair's warranty may be covered by it.
        var again = await ctx.Customer().CreateFaultAsync(h.CustomerId, new CreateFaultInput(h.ProjectId, "Same cable again", null, null, request.RequestId, null, null), None);
        Assert.True(again.Value!.WithinRepairWarranty);
    }

    [Fact]
    public async Task Free_work_is_cancelled_at_once_but_paid_work_needs_a_manager_and_a_manual_refund()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var free = await ctx.FaultAsync(h);
        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await ctx.Customer().CancelAsync(h.CustomerId, free.RequestId, null, None)).ErrorCode);
        Assert.Equal(ServiceRequestStatuses.Cancelled, (await ctx.Customer().CancelAsync(h.CustomerId, free.RequestId, "Works again", None)).Value!.Status);

        var paid = await ctx.FaultAsync(h, "Breaker trips");
        var items = await ctx.InspectedAsync(h, paid.RequestId, Later(3), Item("Breaker"));
        await ctx.DecideAsync(h, paid.RequestId, (items[0], ItemCoverages.Chargeable));
        await ctx.ClaimQuotationTaskAsync(h, paid.RequestId);
        var quotation = await ctx.IssueAsync(h, paid.RequestId, new QuotationLineInput(items[0], null, 500_000, 0));
        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, quotation.QuotationId, None)).Succeeded);
        await ctx.PayAsync((await ctx.Quotations().CheckoutAsync(h.CustomerId, quotation.QuotationId, None)).Value!);

        var requested = (await ctx.Customer().CancelAsync(h.CustomerId, paid.RequestId, "Changed my mind", None)).Value!;
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, requested.Status);
        Assert.NotNull(requested.CancellationRequestedAt);
        var cancelled = (await ctx.Manager().DecideCancellationAsync(h.ManagerId, paid.RequestId, true, 100_000, "Part ordered", None)).Value!;
        Assert.Equal(ServiceRequestStatuses.Cancelled, cancelled.Status);
        var refund = Assert.Single(await ctx.Refunds().ListAsync(null, None));
        Assert.Equal((RefundStatuses.Approved, 400_000m), (refund.Status, refund.ApprovedAmount));

        // Admin records the money that was returned outside the system (never a PayOS call).
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await ctx.Refunds().RecordAsync(h.AdminId, refund.RefundId, ctx.Clock.GetUtcNow().AddDays(1), "TX", None)).ErrorCode);
        Assert.Equal(RefundStatuses.Refunded, (await ctx.Refunds().RecordAsync(h.AdminId, refund.RefundId, ctx.Clock.GetUtcNow(), "TX-1", None)).Value!.Status);
    }

    [Fact]
    public async Task Replacement_equipment_becomes_active_on_acceptance_and_corrections_are_audited()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h, assetId: h.InverterId);
        var items = await ctx.InspectedAsync(h, request.RequestId, Later(3), Item("Inverter board", h.InverterId));
        await ctx.DecideAsync(h, request.RequestId, (items[0], ItemCoverages.InstallationWarranty));
        var repair = await ctx.VisitStartedAsync(h, request.RequestId, Later(6));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Inverter swapped", [new ItemResultInput(items[0], "replaced", null)],
            [new ReplacementInput(h.InverterId, "Huawei", "SUN2000-5KTL", "HW-INV-NEW", "5 kW, 2 MPPT")], null, None)).Succeeded);

        var pending = await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.ReplacesAssetId == h.InverterId);
        Assert.Equal((InstalledAssetStatuses.PendingAcceptance, request.RequestId), (pending.Status, pending.ServiceRequestId));
        Assert.Equal(InstalledAssetStatuses.Active, (await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.Id == h.InverterId)).Status);

        Assert.True((await ctx.Customer().DecideCompletionAsync(h.CustomerId, request.RequestId, true, null, null, None)).Succeeded);
        ctx.Db.ChangeTracker.Clear();
        Assert.Equal(InstalledAssetStatuses.Active, (await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.Id == pending.Id)).Status);
        Assert.Equal(InstalledAssetStatuses.Replaced, (await ctx.Db.InstalledAssets.AsNoTracking().SingleAsync(a => a.Id == h.InverterId)).Status);

        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await ctx.Manager().CorrectAssetAsync(h.ManagerId, pending.Id, new AssetCorrection(null, null, "HW-INV-NEW2", null, null, " "), None)).ErrorCode);
        var corrected = (await ctx.Manager().CorrectAssetAsync(h.ManagerId, pending.Id, new AssetCorrection(null, null, "HW-INV-NEW2", null, null, "Typo in serial"), None)).Value!;
        Assert.Equal("HW-INV-NEW2", corrected.SerialNumber);
        var audit = await ctx.Db.AfterSalesEvents.AsNoTracking().SingleAsync(e => e.SubjectId == pending.Id && e.EventType == "ASSET_CORRECTED");
        Assert.Contains("HW-INV-NEW |", audit.Detail);
        Assert.Contains("HW-INV-NEW2", audit.Detail);
    }

    [Fact]
    public async Task Visit_photos_must_be_the_technicians_own_verified_files()
    {
        // Arrange: an open visit; one photo still pending verification, one verified photo uploaded by the customer.
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var visitId = await ctx.VisitStartedAsync(h, request.RequestId, Later(3));
        var pending = await ctx.Installation.UnverifiedFileAsync(h.TechnicianId, FilePurposes.ServiceEvidence);
        var customersPhoto = await ctx.Installation.VerifiedFileAsync(h.CustomerId, FilePurposes.ServiceEvidence);

        // Act
        var withPending = await ctx.Technician().AddEvidenceAsync(h.TechnicianId, visitId, pending, null, None);
        var withForeign = await ctx.Technician().AddEvidenceAsync(h.TechnicianId, visitId, customersPhoto, null, None);

        // Assert
        Assert.Equal(FileErrorCodes.NotVerified, withPending.ErrorCode);
        Assert.Equal(FileErrorCodes.NotOwned, withForeign.ErrorCode);
    }

    [Fact]
    public async Task Evidence_photos_are_viewable_by_the_customer_the_visit_technician_and_managers_only()
    {
        // Arrange: VisitStartedAsync adds one verified technician photo; the technician's visit view lists it.
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var request = await ctx.FaultAsync(h);
        var visitId = await ctx.VisitStartedAsync(h, request.RequestId, Later(3));
        var evidenceId = Assert.Single((await ctx.Technician().GetAsync(h.TechnicianId, visitId, None)).Value!.Evidence).EvidenceId;
        var readUrls = new FileReadUrlService(new FakeStorageUrlSigner(ctx.Clock), ctx.Installation.Storage, ctx.Installation.StorageOptions, ctx.Clock,
            NullLogger<FileReadUrlService>.Instance);
        var urls = new AfterSalesEvidenceUrlHandlers(ctx.Data(), new FileStore(ctx.Db), readUrls);
        var inline = SmartSolar.Modules.Files.StorageUrlDisposition.Inline;

        // Act
        var customer = await urls.ForCustomerAsync(h.CustomerId, request.RequestId, evidenceId, inline, None);
        var technician = await urls.ForTechnicianAsync(h.TechnicianId, visitId, evidenceId, inline, None);
        var manager = await urls.ForStaffAsync(request.RequestId, evidenceId, inline, None);

        // Assert: allowed callers get a URL for that photo; other users and other ids get "not found".
        Assert.All(new[] { customer, technician, manager }, r => Assert.True(r.Succeeded, r.Message));
        Assert.Equal(evidenceId, customer.Value!.Id);
        Assert.StartsWith(FakeStorageUrlSigner.Host, customer.Value.Url);
        Assert.Equal(AfterSalesErrorCodes.RequestNotFound, (await urls.ForCustomerAsync(Guid.NewGuid(), request.RequestId, evidenceId, inline, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.VisitNotFound, (await urls.ForTechnicianAsync(Guid.NewGuid(), visitId, evidenceId, inline, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.EvidenceNotFound, (await urls.ForStaffAsync(request.RequestId, Guid.NewGuid(), inline, None)).ErrorCode);
    }
}
