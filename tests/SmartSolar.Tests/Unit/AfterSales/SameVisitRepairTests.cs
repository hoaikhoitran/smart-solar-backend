using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// Inspection and repair during the same physical visit: the technician stays on site after the diagnosis and continues as the repair
/// only when the work is authorized (Manager decision, quotation accepted and paid), the parts are there and the work fits in the
/// interval already reserved. Otherwise the visit ends and a Manager schedules a separate repair visit.
/// </summary>
public sealed class SameVisitRepairTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Inspection visit of 09:00–12:00 three days after handover; the technician reports one diagnosed item and stays on site.</summary>
    private static async Task<(Guid RequestId, Guid VisitId, Guid ItemId)> WaitingOnSiteAsync(AfterSalesTestContext ctx, HandedOver h)
    {
        var request = await ctx.FaultAsync(h);
        var visit = await ctx.VisitStartedAsync(h, request.RequestId, Later(3, 9), endsAt: Later(3, 12));
        var reported = await ctx.Technician().ReportInspectionAsync(h.TechnicianId, visit, "Breaker worn out", false,
            [new DiagnosedItemInput(null, "AC breaker", "contacts burnt", "C16 breaker")], stayOnSite: true, None);
        Assert.True(reported.Succeeded, reported.Message);
        Assert.True(reported.Value!.WaitingOnSite);
        var item = (await ctx.Data().ListItemsAsync(request.RequestId, false, None)).Single().Id;
        return (request.RequestId, visit, item);
    }

    [Fact]
    public async Task Paid_work_is_repaired_in_the_same_visit_without_a_second_appointment()
    {
        // Arrange: diagnosis on site, chargeable decision, quotation accepted and paid through PayOS.
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var (requestId, visitId, itemId) = await WaitingOnSiteAsync(ctx, h);
        await ctx.DecideAsync(h, requestId, (itemId, ItemCoverages.Chargeable));
        await ctx.ClaimQuotationTaskAsync(h, requestId);
        var quotation = await ctx.IssueAsync(h, requestId, new QuotationLineInput(itemId, null, 300_000, 100_000));

        // Before payment the technician may not continue.
        Assert.Equal(AfterSalesErrorCodes.PaymentRequired, (await ctx.Technician().ContinueWithRepairAsync(h.TechnicianId, visitId, 60, true, None)).ErrorCode);
        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, quotation.QuotationId, None)).Succeeded);
        await ctx.PayAsync((await ctx.Quotations().CheckoutAsync(h.CustomerId, quotation.QuotationId, None)).Value!);

        // Act
        var continued = await ctx.Technician().ContinueWithRepairAsync(h.TechnicianId, visitId, 60, partsAvailable: true, None);
        var reported = await ctx.Technician().ReportWorkAsync(h.TechnicianId, visitId, "Breaker replaced", [new ItemResultInput(itemId, "replaced", "C16 breaker")],
            [], null, None);

        // Assert: one visit and one technician interval did both the inspection and the repair.
        Assert.True(continued.Succeeded, continued.Message);
        Assert.Equal(VisitPurposes.Repair, continued.Value!.Purpose);
        Assert.True(reported.Succeeded, reported.Message);
        var view = (await ctx.Customer().GetAsync(h.CustomerId, requestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.PendingCustomerAcceptance, view.Status);
        var visit = Assert.Single(view.Visits);
        Assert.Contains("Breaker worn out", visit.Report);   // the inspection report is kept
        Assert.Contains("Breaker replaced", visit.Report);
        Assert.Single(await ctx.Db.InstallationAssignments.AsNoTracking().Where(a => a.ServiceVisitId != null).ToListAsync());
        Assert.NotNull(view.RepairWarrantyStartsAt);
    }

    [Fact]
    public async Task Without_parts_or_time_the_visit_ends_and_the_manager_schedules_a_separate_repair()
    {
        // Arrange: free warranty work, authorized at once by the Manager.
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var (requestId, visitId, itemId) = await WaitingOnSiteAsync(ctx, h);
        await ctx.DecideAsync(h, requestId, (itemId, ItemCoverages.InstallationWarranty));
        var tech = ctx.Technician();

        // Act / Assert: the conditions are checked, and the Manager cannot book another visit while the technician waits.
        Assert.Equal(AfterSalesErrorCodes.PartsUnavailable, (await tech.ContinueWithRepairAsync(h.TechnicianId, visitId, 60, partsAvailable: false, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.RepairTimeInsufficient, (await tech.ContinueWithRepairAsync(h.TechnicianId, visitId, 240, partsAvailable: true, None)).ErrorCode);
        var waiting = (await ctx.Manager().GetAsync(requestId, None)).Value!;
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Manager().ScheduleVisitAsync(h.ManagerId, requestId,
            new ScheduleVisitInput(h.TechnicianId, Later(6), null, null, waiting.Revision), None)).ErrorCode);

        Assert.True((await tech.FinishOnSiteWaitAsync(h.TechnicianId, visitId, "breaker not in the van", None)).Succeeded);
        var ready = (await ctx.Manager().GetAsync(requestId, None)).Value!;
        Assert.Equal(ServiceRequestStatuses.ReadyForWork, ready.Status);
        Assert.Equal(VisitStatuses.Reported, Assert.Single(ready.Visits).Status);
        var repair = await ctx.Manager().ScheduleVisitAsync(h.ManagerId, requestId, new ScheduleVisitInput(h.TechnicianId, Later(6), null, null, ready.Revision), None);
        Assert.True(repair.Succeeded, repair.Message);
        Assert.Equal(VisitPurposes.Repair, repair.Value!.Visits.Single(v => VisitStatuses.IsOpen(v.Status)).Purpose);
    }

    [Fact]
    public async Task The_sweep_ends_an_on_site_wait_when_the_reserved_time_is_over()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var (requestId, visitId, _) = await WaitingOnSiteAsync(ctx, h);

        ctx.Clock.Now = Later(3, 12).ToUniversalTime();
        var result = await ctx.Sweep().RunOnceAsync(None);

        Assert.Equal(1, result.EndedOnSiteWaits);
        var visit = (await ctx.Technician().GetAsync(h.TechnicianId, visitId, None)).Value!;
        Assert.Equal((VisitStatuses.Reported, false), (visit.Status, visit.WaitingOnSite));
        Assert.Equal(AssignmentStatuses.Completed, (await ctx.Db.InstallationAssignments.AsNoTracking().SingleAsync(a => a.ServiceVisitId == visitId)).Status);
        Assert.Equal(ServiceRequestStatuses.AwaitingDecision, (await ctx.Manager().GetAsync(requestId, None)).Value!.Status);
    }
}
