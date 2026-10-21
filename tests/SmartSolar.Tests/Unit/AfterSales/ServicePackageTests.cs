using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;
using static SmartSolar.Tests.TestSupport.AfterSalesTestContext;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// Maintenance and disaster-protection packages: combined checkout with the contract, term start at handover, price snapshot, one term
/// per kind with prepaid renewal, maintenance booking rules and consumption, waiting period, support caps and reservation.
/// </summary>
public sealed class ServicePackageTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Contract_and_packages_are_paid_in_one_itemized_checkout_and_terms_start_at_handover()
    {
        await using var ctx = Create();
        var admin = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Admin");
        var care = await ctx.PackageAsync(admin.Id, ServicePackageKinds.Maintenance, price: 2_000_000m);
        var shield = await ctx.PackageAsync(admin.Id, ServicePackageKinds.DisasterProtection, price: 1_500_000m);
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        var customer = ready.Survey.Request.Customer.Id;

        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Packages().PurchaseAsync(customer, new PurchaseInput(contract.ContractId, care.Id, null, false), None)).ErrorCode);
        var bought = (await ctx.Packages().PurchaseAsync(customer, new PurchaseInput(contract.ContractId, care.Id, shield.Id, true), None)).Value!;
        Assert.Equal(contract.TotalAmount + 3_500_000m, bought.Checkout.Amount);
        var lines = await ctx.Db.PaymentAttemptItems.AsNoTracking().Where(i => i.PaymentAttemptId == bought.Checkout.PaymentAttemptId).ToListAsync();
        Assert.Equal(new[] { PaymentItemTypes.Contract, PaymentItemTypes.DisasterPackage, PaymentItemTypes.MaintenancePackage }, lines.Select(l => l.ItemType).Order());
        Assert.Equal(contract.TotalAmount, lines.Single(l => l.ItemType == PaymentItemTypes.Contract).Amount);   // the contract total is unchanged

        // A later catalog change does not touch the purchased snapshot.
        Assert.True((await ctx.Packages().UpdatePackageAsync(admin.Id, care.Id,
            new PackageInput(care.Kind, care.Name, null, 9_000_000m, 12, [3], null, null, null, null, true), care.Revision, None)).Succeeded);

        Assert.Equal(PaymentStatuses.Paid, (await ctx.PayAsync(bought.Checkout))!.Status);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == contract.ContractId)).Status);
        var paid = await ctx.Db.ServiceSubscriptions.AsNoTracking().Where(s => s.ContractId == contract.ContractId).ToListAsync();
        Assert.All(paid, s => Assert.Equal((SubscriptionStatuses.Paid, (DateTimeOffset?)null), (s.Status, s.StartsAt)));   // not before handover
        Assert.Equal((2_000_000m, "6,12"), (paid.Single(s => s.Kind == ServicePackageKinds.Maintenance).Price, paid.Single(s => s.Kind == ServicePackageKinds.Maintenance).VisitMonths));

        var manager = await ctx.Installation.Scheduling.Inner.SeedUserAsync("Manager");
        var h = await ctx.HandOverAsync(new InstallationTests.Paid(ctx.Installation, contract.ContractId, customer, manager.Id), admin.Id);
        Assert.Equal(2, (await ctx.Sweep().RunOnceAsync(None)).StartedOrActivated);
        var terms = await ctx.Db.ServiceSubscriptions.AsNoTracking().Where(s => s.ContractId == contract.ContractId).ToListAsync();
        Assert.All(terms, s => Assert.Equal((SubscriptionStatuses.Active, h.HandoverAt), (s.Status, s.StartsAt!.Value)));
        var disaster = terms.Single(s => s.Kind == ServicePackageKinds.DisasterProtection);
        Assert.Equal(h.HandoverAt.AddDays(7), disaster.CoverageStartsAt);   // waiting period
        var maintenanceId = terms.Single(s => s.Kind == ServicePackageKinds.Maintenance).Id;
        var visits = await ctx.Db.MaintenanceEntitlements.AsNoTracking().Where(e => e.SubscriptionId == maintenanceId).ToListAsync();
        Assert.Equal(new[] { 6, 12 }, visits.Select(v => v.PlanMonth).Order());
    }

    [Fact]
    public async Task One_term_per_kind_with_one_prepaid_renewal_that_starts_when_the_current_term_ends()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var shield = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection);
        var first = await ctx.ActiveTermAsync(h, shield);
        Assert.Equal(SubscriptionStatuses.Active, first.Status);

        var renewal = await ctx.ActiveTermAsync(h, shield);
        Assert.Equal((SubscriptionStatuses.Paid, first.EndsAt), (renewal.Status, renewal.StartsAt));
        Assert.Equal(first.Id, renewal.RenewsSubscriptionId);
        Assert.Equal(renewal.StartsAt, renewal.CoverageStartsAt);   // continuous renewal: no new waiting period
        Assert.Equal(AfterSalesErrorCodes.TermOverlap, (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, null, shield.Id, false), None)).ErrorCode);
    }

    [Fact]
    public async Task Package_cancellation_before_activation_is_decided_by_a_manager_with_a_manual_refund()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var care = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance);
        await ctx.ActiveTermAsync(h, care);
        var renewal = await ctx.ActiveTermAsync(h, care);   // paid, starts later

        var unpaid = (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, null,
            (await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection)).Id, false), None)).Value!;
        Assert.Equal(SubscriptionStatuses.Cancelled, (await ctx.Packages().RequestCancellationAsync(h.CustomerId, unpaid.SubscriptionIds[0], "No need", None)).Value!.Status);

        Assert.Equal(SubscriptionStatuses.CancellationRequested, (await ctx.Packages().RequestCancellationAsync(h.CustomerId, renewal.Id, "Moving house", None)).Value!.Status);
        Assert.Equal(AfterSalesErrorCodes.ValidationFailed, (await ctx.Packages().DecideCancellationAsync(h.ManagerId, renewal.Id, true, 3_000_000m, null, None)).ErrorCode);
        Assert.Equal(SubscriptionStatuses.Cancelled, (await ctx.Packages().DecideCancellationAsync(h.ManagerId, renewal.Id, true, null, null, None)).Value!.Status);
        var refund = Assert.Single(await ctx.Refunds().ListAsync(RefundStatuses.Approved, None));
        Assert.Equal((renewal.Id, 2_000_000m), (refund.SubscriptionId!.Value, refund.ApprovedAmount!.Value));
    }

    [Fact]
    public async Task Cancelling_one_unpaid_package_of_a_shared_checkout_frees_the_other_package_for_a_new_purchase()
    {
        // Arrange: both packages in one PayOS checkout, not paid.
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var care = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance);
        var shield = await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection);
        var bought = (await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, care.Id, shield.Id, false), None)).Value!;
        var (maintenanceId, disasterId) = (bought.SubscriptionIds[0], bought.SubscriptionIds[1]);

        // Act: the customer cancels the maintenance package; the shared link is cancelled with it.
        var cancelled = await ctx.Packages().RequestCancellationAsync(h.CustomerId, maintenanceId, "Only storm cover", None);

        // Assert
        Assert.Equal(SubscriptionStatuses.Cancelled, cancelled.Value!.Status);
        Assert.Equal(SubscriptionStatuses.CheckoutExpired, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == disasterId)).Status);
        Assert.Equal(PaymentStatuses.Cancelled, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == bought.Checkout.PaymentAttemptId)).Status);
        Assert.True((await ctx.Packages().PurchaseAsync(h.CustomerId, new PurchaseInput(h.ContractId, null, shield.Id, false), None)).Succeeded);
    }

    [Fact]
    public async Task Maintenance_booking_respects_lead_time_and_period_and_absence_consumes_nothing()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, visitMonths: "1,7"));
        var month1 = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id && e.PlanMonth == 1);
        var month7 = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id && e.PlanMonth == 7);
        var customer = ctx.Customer();

        Assert.Equal(AfterSalesErrorCodes.BookingTooSoon, (await customer.BookMaintenanceAsync(h.CustomerId, month1.Id, Later(5), null, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.OutsidePeriod, (await customer.BookMaintenanceAsync(h.CustomerId, month7.Id, Later(10), null, None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.EntitlementNotFound, (await customer.BookMaintenanceAsync(h.SalesId, month1.Id, Later(10), null, None)).ErrorCode);
        var booking = (await customer.BookMaintenanceAsync(h.CustomerId, month1.Id, Later(10), null, None)).Value!;
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await customer.BookMaintenanceAsync(h.CustomerId, month1.Id, Later(11), null, None)).ErrorCode);

        // Within 24 hours of the visit the appointment can no longer be changed online.
        var request = (await ctx.Manager().GetAsync(booking.RequestId, None)).Value!;
        var scheduled = (await ctx.Manager().ScheduleVisitAsync(h.ManagerId, booking.RequestId, new ScheduleVisitInput(h.TechnicianId, Later(10), null, null, request.Revision), None)).Value!;
        var visit = scheduled.Visits.Single().VisitId;
        Assert.True((await ctx.Technician().RespondAsync(h.TechnicianId, visit, true, null, None)).Succeeded);
        ctx.Clock.Now = Later(10).AddHours(-20).ToUniversalTime();
        Assert.Equal(AfterSalesErrorCodes.ChangeTooLate, (await customer.RescheduleMaintenanceAsync(h.CustomerId, booking.RequestId, Later(18), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ChangeTooLate, (await customer.CancelAsync(h.CustomerId, booking.RequestId, "busy", None)).ErrorCode);

        ctx.Clock.Now = Later(10).ToUniversalTime();
        Assert.True((await ctx.Technician().ReportAbsenceAsync(h.TechnicianId, visit, "Nobody home", None)).Succeeded);
        Assert.Equal(ServiceRequestStatuses.NotCompleted, (await customer.GetAsync(h.CustomerId, booking.RequestId, None)).Value!.Status);
        Assert.Equal(EntitlementStatuses.Available, (await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.Id == month1.Id)).Status);

        // Booked again; the visit counts as used only when the customer accepts the result.
        var again = (await customer.BookMaintenanceAsync(h.CustomerId, month1.Id, Later(18), null, None)).Value!;
        var started = await ctx.VisitStartedAsync(h, again.RequestId, Later(18));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, started, "Cleaned panels", [], [], "Hot spot on R1-C2", None)).Succeeded);
        Assert.Equal(EntitlementStatuses.Booked, (await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.Id == month1.Id)).Status);
        Assert.Equal(ServiceRequestStatuses.Completed, (await customer.DecideCompletionAsync(h.CustomerId, again.RequestId, true, null, null, None)).Value!.Status);
        Assert.Equal(EntitlementStatuses.Consumed, (await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.Id == month1.Id)).Status);

        // The finding becomes a separate fault request linked to the maintenance visit.
        var fault = (await customer.ReportFaultFromMaintenanceAsync(h.CustomerId, again.RequestId, null, None)).Value!;
        Assert.Equal((ServiceRequestKinds.Fault, again.RequestId), (fault.Kind, fault.OriginRequestId!.Value));
        Assert.Contains("Hot spot", fault.Description);
    }

    [Fact]
    public async Task Notices_and_sales_follow_ups_are_opened_by_the_sweep_and_an_unreachable_customer_does_not_consume_the_visit()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, visitMonths: "2"));
        var visit = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id);

        ctx.Clock.Now = visit.PeriodStartsAt.AddDays(-14).ToUniversalTime();
        Assert.Equal(1, (await ctx.Sweep().RunOnceAsync(None)).MaintenanceNotices);
        Assert.Contains(await ctx.Customer().NotificationsAsync(h.CustomerId, None), n => n.Type == "MAINTENANCE_DUE");
        Assert.Empty(await ctx.Tasks().ListOpenAsync(None));

        ctx.Clock.Now = ctx.Clock.GetUtcNow().AddHours(24);
        await ctx.Sweep().RunOnceAsync(None);
        var task = Assert.Single(await ctx.Tasks().ListOpenAsync(None));
        Assert.Equal((SalesTaskTypes.MaintenanceFollowUp, visit.Id), (task.TaskType, task.EntitlementId!.Value));
        Assert.True((await ctx.Tasks().ClaimAsync(h.SalesId, task.TaskId, None)).Succeeded);
        Assert.True((await ctx.Tasks().RecordFollowUpAsync(h.SalesId, task.TaskId, new FollowUpInput(ctx.Clock.GetUtcNow(), "No answer", null, null), None)).Succeeded);

        ctx.Clock.Now = ctx.Clock.GetUtcNow().AddDays(7);
        await ctx.Sweep().RunOnceAsync(None);
        Assert.Equal((SalesTaskStatuses.Done, FollowUpOutcomes.Unreachable), ((await ctx.Db.SalesTasks.AsNoTracking().SingleAsync()).Status, (await ctx.Db.SalesTasks.AsNoTracking().SingleAsync()).Outcome));
        Assert.Equal(EntitlementStatuses.NotCompleted, (await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.Id == visit.Id)).Status);
        // Still bookable while the term is active.
        Assert.True((await ctx.Customer().BookMaintenanceAsync(h.CustomerId, visit.Id, ctx.Clock.GetUtcNow().AddDays(8), null, None)).Succeeded);
    }

    [Fact]
    public async Task A_company_caused_delay_extends_the_booking_and_an_unfulfilled_visit_expires_as_unfulfilled()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance, visitMonths: "12"));
        var visit = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.SubscriptionId == term.Id);
        var lastWeek = term.EndsAt!.Value.AddDays(-5);
        ctx.Clock.Now = lastWeek.AddDays(-8).ToUniversalTime();
        var booking = (await ctx.Customer().BookMaintenanceAsync(h.CustomerId, visit.Id, lastWeek, null, None)).Value!;

        var afterEnd = term.EndsAt.Value.AddDays(10);
        var request = (await ctx.Manager().GetAsync(booking.RequestId, None)).Value!;
        Assert.Equal(AfterSalesErrorCodes.OutsidePeriod, (await ctx.Manager().ScheduleVisitAsync(h.ManagerId, booking.RequestId,
            new ScheduleVisitInput(h.TechnicianId, afterEnd, null, null, request.Revision), None)).ErrorCode);
        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await ctx.Manager().ScheduleVisitAsync(h.ManagerId, booking.RequestId,
            new ScheduleVisitInput(h.TechnicianId, afterEnd, null, null, request.Revision, CompanyCaused: true), None)).ErrorCode);
        Assert.True((await ctx.Manager().ScheduleVisitAsync(h.ManagerId, booking.RequestId,
            new ScheduleVisitInput(h.TechnicianId, afterEnd, null, "Technician shortage", request.Revision, CompanyCaused: true), None)).Succeeded);
        var extended = await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.Id == visit.Id);
        Assert.Equal(term.EndsAt.Value.AddDays(30), extended.ExtendedUntil);

        ctx.Clock.Now = term.EndsAt.Value.AddDays(31).ToUniversalTime();
        await ctx.Sweep().RunOnceAsync(None);
        Assert.Equal(EntitlementStatuses.ExpiredUnfulfilled, (await ctx.Db.MaintenanceEntitlements.AsNoTracking().SingleAsync(e => e.Id == visit.Id)).Status);
        Assert.Equal(ServiceRequestStatuses.NotCompleted, (await ctx.Customer().GetAsync(h.CustomerId, booking.RequestId, None)).Value!.Status);
        Assert.Equal(SubscriptionStatuses.Expired, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).Status);
    }

    [Fact]
    public async Task Disaster_claims_respect_the_waiting_period_and_one_reconsideration()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection));

        // Inside the term but during the 7-day waiting period: tied to the term, not eligible.
        ctx.Clock.Now = term.StartsAt!.Value.AddDays(3).ToUniversalTime();
        var early = await ctx.FaultAsync(h, "Storm bent the rails", disasterType: DisasterTypes.Storm, incidentAt: term.StartsAt.Value.AddDays(2));
        Assert.Equal((ClaimStatuses.NotEligible, (Guid?)term.Id), (early.Claim!.Status, early.Claim.SubscriptionId));

        ctx.Clock.Now = term.CoverageStartsAt!.Value.AddDays(2).ToUniversalTime();
        var storm = await ctx.FaultAsync(h, "Storm broke two panels", disasterType: DisasterTypes.Storm, incidentAt: ctx.Clock.GetUtcNow().AddDays(-1));
        Assert.Equal(ClaimStatuses.Submitted, storm.Claim!.Status);
        var manager = ctx.Manager();
        Assert.Equal(AfterSalesErrorCodes.ReasonRequired, (await manager.DecideClaimAsync(h.ManagerId, storm.RequestId, false, null, None)).ErrorCode);
        Assert.True((await manager.DecideClaimAsync(h.ManagerId, storm.RequestId, false, "No storm damage visible", None)).Succeeded);
        Assert.True((await ctx.Customer().RequestReconsiderationAsync(h.CustomerId, storm.RequestId, "Photos of the debris", null, None)).Succeeded);
        var approved = (await manager.DecideClaimAsync(h.ManagerId, storm.RequestId, true, "Debris confirms the storm", None)).Value!;
        Assert.Equal(ClaimStatuses.Approved, approved.Claim!.Status);
        Assert.Equal(AfterSalesErrorCodes.InvalidState, (await ctx.Customer().RequestReconsiderationAsync(h.CustomerId, storm.RequestId, "again", null, None)).ErrorCode);
    }

    [Fact]
    public async Task Disaster_support_is_capped_reserved_at_quotation_released_on_rejection_and_consumed_on_acceptance()
    {
        await using var ctx = Create();
        var h = await ctx.HandedOverAsync();
        // 70 %, at most 5,000,000 per incident and 8,000,000 per term.
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.DisasterProtection));
        ctx.Clock.Now = term.CoverageStartsAt!.Value.AddDays(1).ToUniversalTime();
        var storm = await ctx.FaultAsync(h, "Storm broke panels", disasterType: DisasterTypes.Storm, incidentAt: ctx.Clock.GetUtcNow().AddHours(-2));
        Assert.True((await ctx.Manager().DecideClaimAsync(h.ManagerId, storm.RequestId, true, "confirmed", None)).Succeeded);
        var items = await ctx.InspectedAsync(h, storm.RequestId, ctx.Clock.GetUtcNow().AddDays(2), new DiagnosedItemInput(null, "Panels R1", "cracked", null));
        await ctx.DecideAsync(h, storm.RequestId, (items[0], ItemCoverages.DisasterProtection));
        await ctx.ClaimQuotationTaskAsync(h, storm.RequestId);

        var v1 = await ctx.IssueAsync(h, storm.RequestId, new QuotationLineInput(items[0], null, 9_000_000, 1_000_000));
        Assert.Equal((10_000_000m, 5_000_000m, 5_000_000m), (v1.TotalAmount, v1.DisasterSupportAmount, v1.CustomerAmount));   // per-incident cap beats 70 %
        Assert.Equal(5_000_000m, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).SupportReserved);

        // A new version releases the old reservation and reserves its own share.
        var v2 = await ctx.IssueAsync(h, storm.RequestId, new QuotationLineInput(items[0], null, 3_000_000, 1_000_000));
        Assert.Equal((2_800_000m, 1_200_000m), (v2.DisasterSupportAmount, v2.CustomerAmount));
        Assert.Equal(2_800_000m, (await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).SupportReserved);

        Assert.True((await ctx.Quotations().AcceptAsync(h.CustomerId, v2.QuotationId, None)).Succeeded);
        var checkout = (await ctx.Quotations().CheckoutAsync(h.CustomerId, v2.QuotationId, None)).Value!;
        Assert.Equal(1_200_000m, checkout.Amount);   // the customer pays only their part
        await ctx.PayAsync(checkout);
        var repair = await ctx.VisitStartedAsync(h, storm.RequestId, ctx.Clock.GetUtcNow().AddDays(2));
        Assert.True((await ctx.Technician().ReportWorkAsync(h.TechnicianId, repair, "Panels replaced", [new ItemResultInput(items[0], "ok", null)], [], null, None)).Succeeded);
        Assert.True((await ctx.Customer().DecideCompletionAsync(h.CustomerId, storm.RequestId, true, null, null, None)).Succeeded);

        var consumed = await ctx.Db.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id);
        Assert.Equal((0m, 2_800_000m, 5_200_000m), (consumed.SupportReserved, consumed.SupportConsumed, consumed.SupportAvailable));
    }
}
