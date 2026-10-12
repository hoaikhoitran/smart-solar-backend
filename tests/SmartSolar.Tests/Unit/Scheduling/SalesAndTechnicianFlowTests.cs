using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Travel;
using SmartSolar.Modules.SolarSimulation.Access;
using SmartSolar.Modules.SolarSimulation.GetSimulation;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Sales assignment, technician job access and acknowledgment, dashboard schedule. SYNTHETIC travel.</summary>
public sealed class SalesAndTechnicianFlowTests
{
    private static async Task<(SchedulingTestContext Ctx, Modules.Identity.Entities.UserAccount TechA, SeededSurveyRequest Request, Modules.Identity.Entities.UserAccount Sales)> BookedAndClaimedAsync()
    {
        var ctx = Create();
        var techA = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), CancellationToken.None)).Succeeded);
        var sales = await ctx.ClaimAsync(request);
        return (ctx, techA, request, sales);
    }

    [Fact]
    public async Task Sales_assigns_the_reserved_technician_and_the_request_becomes_scheduled()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;

        var result = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techA.Id), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Value!.Reassigned);
        Assert.Equal(AppointmentStatuses.Assigned, result.Value.AppointmentStatus);
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(techA.Id, appointment.AssignedTechnicianUserId);
        Assert.Equal(sales.Id, appointment.AssignedBySaleUserId);
        Assert.NotNull(appointment.AssignedAt);
        var surveyRequest = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Scheduled, surveyRequest.Status);
        Assert.Equal(appointment.StartsAt, surveyRequest.ScheduledAt);

        var detail = await ctx.Inner.UnitOfWork.GetSurveyRequestDetailAsync(request.Request.Id, CancellationToken.None);
        Assert.Equal(appointment.Id, detail!.Appointment!.AppointmentId);
        Assert.Equal("Tech A", detail.Appointment.AssignedTechnicianName);
    }

    [Fact]
    public async Task Availability_lists_feasible_technicians_with_the_current_reservation_first()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        var onLeave = await ctx.SeedTechnicianAsync("On leave", ThuDuc);
        await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), onLeave.Id, At(0), At(23, 59), "Leave"), CancellationToken.None);

        var result = await ctx.Availability().HandleAsync(new TechnicianAvailabilityQuery(sales.Id, request.Request.Id, null, null), CancellationToken.None);

        var view = result.Value!;
        Assert.True(view.MatchesBookedAppointment);
        Assert.Equal(new[] { techA.Id, techB.Id }.Order(), view.Technicians.Select(t => t.TechnicianUserId).Order());
        Assert.True(view.Technicians[0].IsCurrentReservation);
        Assert.Equal(techA.Id, view.Technicians[0].TechnicianUserId);
        Assert.Equal("BASE", view.Technicians[0].Inbound.OriginKind);
        Assert.Equal("motorcycle", view.Technicians[0].Inbound.Vehicle);
    }

    [Fact]
    public async Task Sales_can_transfer_the_reservation_to_another_eligible_technician()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);

        var result = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techB.Id), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Value!.Reassigned);
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(techB.Id, appointment.ReservedTechnicianUserId);
        Assert.Equal(techB.Id, appointment.AssignedTechnicianUserId);
        Assert.Single(await ctx.Db.TechnicianAppointments.AsNoTracking().ToListAsync());
        Assert.Contains(await ctx.Db.TechnicianTravelEstimates.AsNoTracking().ToListAsync(),
            e => e.AppointmentId == appointment.Id && e.TechnicianUserId == techB.Id && e.Context == "ASSIGNMENT");

        // Tech A's capacity is free again: another customer can take A at the same time.
        var other = await ctx.SeedSurveyRequestAsync(District1, "Other");
        await ctx.Db.TechnicianProfiles.Where(p => p.UserId == techB.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsSchedulingActive, false));
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(other.Customer.Id, other.Request.Id, At(9), null), CancellationToken.None)).Succeeded);
        Assert.Equal(techA.Id, (await ctx.ActiveAppointmentAsync(other.Request.Id)).ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Failed_reassignment_leaves_the_original_reservation_unchanged()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var busy = await ctx.SeedTechnicianAsync("Busy tech", District7);
        var busyJob = await ctx.SeedSurveyRequestAsync(District7, "Busy job");
        await ctx.Db.TechnicianProfiles.Where(p => p.UserId == techA.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsSchedulingActive, false));
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(busyJob.Customer.Id, busyJob.Request.Id, At(9), null), CancellationToken.None)).Succeeded);
        await ctx.Db.TechnicianProfiles.Where(p => p.UserId == techA.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsSchedulingActive, true));
        var before = await ctx.ActiveAppointmentAsync(request.Request.Id);

        var result = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, busy.Id), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TechnicianNotAvailable, result.ErrorCode);
        var after = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(before.ReservedTechnicianUserId, after.ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.Booked, after.Status);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(SurveyRequestStatus.Assigned, (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id)).Status);
    }

    [Fact]
    public async Task Reassignment_to_a_technician_who_cannot_verify_travel_is_refused()
    {
        var (ctx, _, request, sales) = await BookedAndClaimedAsync();
        await using var __ = ctx;
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        ctx.Travel.FailWith = RouteCalculationStatuses.QuotaExceeded;

        var result = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techB.Id), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TravelProviderQuotaExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task Assignment_rules_ownership_booking_and_technician_validity()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var otherSales = await ctx.Inner.SeedSalesUserAsync("Other sales");
        var unbooked = await ctx.SeedSurveyRequestAsync(BinhTan, "Unbooked");
        var unbookedSales = await ctx.ClaimAsync(unbooked);
        var notTechnician = await ctx.Inner.SeedUserAsync("Not a technician");
        var noBase = await ctx.SeedTechnicianAsync("No base", null);

        Assert.Equal(PreSurveyErrorCodes.SurveyRequestNotAssigned,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(otherSales.Id, request.Request.Id, techA.Id), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.BookingNotFound,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(unbookedSales.Id, unbooked.Request.Id, techA.Id), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianNotFound,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, notTechnician.Id), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianNotAvailable,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, noBase.Id), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotFound,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, Guid.NewGuid(), techA.Id), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Reserved_technician_newly_on_leave_cannot_be_confirmed()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        // The admin API refuses leave over an existing booking...
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments,
            (await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), techA.Id, At(8), At(12), "Sick"), CancellationToken.None)).ErrorCode);
        // ...but confirmation still re-checks leave, e.g. for rows written before that check existed.
        ctx.Db.TechnicianUnavailablePeriods.Add(new TechnicianUnavailablePeriod
        {
            Id = Guid.NewGuid(), TechnicianUserId = techA.Id, StartsAt = At(8).ToUniversalTime(), EndsAt = At(12).ToUniversalTime(), CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var result = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techA.Id), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TechnicianNotAvailable, result.ErrorCode);
        Assert.Equal(AppointmentStatuses.Booked, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Status);
    }

    [Fact]
    public async Task Technician_sees_only_their_assigned_job_with_customer_and_survey_details()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);

        // Before assignment the reserved technician sees nothing.
        Assert.Empty(await ctx.Jobs().ListAsync(techA.Id, CancellationToken.None));
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetAsync(techA.Id, appointment.Id, CancellationToken.None)).ErrorCode);

        await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techA.Id), CancellationToken.None);

        var list = await ctx.Jobs().ListAsync(techA.Id, CancellationToken.None);
        var job = (await ctx.Jobs().GetAsync(techA.Id, appointment.Id, CancellationToken.None)).Value!;
        Assert.Single(list);
        Assert.Equal(request.Customer.FullName, job.Customer.FullName);
        Assert.Equal(request.Customer.Email, job.Customer.Email);
        Assert.Equal(BinhTan.Latitude, job.Property.Latitude);
        Assert.Equal(120m, job.PreSurvey.TotalAreaM2);
        Assert.NotNull(job.InboundTravelEstimate);
        Assert.Equal("HISTORICAL_ESTIMATE", job.InboundTravelEstimate!.Kind);
        Assert.Empty(await ctx.Jobs().ListAsync(techB.Id, CancellationToken.None));
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetAsync(techB.Id, appointment.Id, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Acknowledgment_is_recorded_once_and_repeats_are_idempotent()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().AcknowledgeAsync(techA.Id, appointment.Id, CancellationToken.None)).ErrorCode);
        await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techA.Id), CancellationToken.None);

        var first = await ctx.Jobs().AcknowledgeAsync(techA.Id, appointment.Id, CancellationToken.None);
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(5);
        var second = await ctx.Jobs().AcknowledgeAsync(techA.Id, appointment.Id, CancellationToken.None);
        var stranger = await ctx.Jobs().AcknowledgeAsync(techB.Id, appointment.Id, CancellationToken.None);

        Assert.False(first.Value!.AlreadyAcknowledged);
        Assert.True(second.Value!.AlreadyAcknowledged);
        Assert.Equal(first.Value.AcknowledgedAt, second.Value.AcknowledgedAt);
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, stranger.ErrorCode);
        var stored = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(AppointmentStatuses.Acknowledged, stored.Status);
        // Flow 1 ends here: the request stays Scheduled (never Completed).
        Assert.Equal(SurveyRequestStatus.Scheduled, (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id)).Status);
        // Re-assigning the same technician keeps the acknowledgment (idempotent).
        var same = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techA.Id), CancellationToken.None);
        Assert.False(same.Value!.AcknowledgementRequired);
        Assert.Equal(AppointmentStatuses.Acknowledged, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Status);
    }

    [Fact]
    public async Task Assigned_technician_can_read_the_selected_simulation_others_cannot()
    {
        var (ctx, techA, request, sales) = await BookedAndClaimedAsync();
        await using var _ = ctx;
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        var simulationSource = new GetSimulationHandler(ctx.Inner.SimulationUnitOfWork);
        var asTechA = new SimulationViewer(techA.Id, false, false, true);
        var asTechB = new SimulationViewer(techB.Id, false, false, true);

        Assert.Equal(SimulationQueryOutcome.Forbidden, (await simulationSource.HandleAsync(asTechA, request.PreSurvey.Id, Guid.NewGuid(), CancellationToken.None)).Outcome);
        await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, techA.Id), CancellationToken.None);

        Assert.Equal(SimulationQueryOutcome.SimulationNotFound, (await simulationSource.HandleAsync(asTechA, request.PreSurvey.Id, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(SimulationQueryOutcome.Forbidden, (await simulationSource.HandleAsync(asTechB, request.PreSurvey.Id, Guid.NewGuid(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Dashboard_schedule_sums_stored_travel_and_flags_routing_state()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var second = await ctx.SeedSurveyRequestAsync(District7, "Second");
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(first.Customer.Id, first.Request.Id, At(8, 30), null), CancellationToken.None)).Succeeded);
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(second.Customer.Id, second.Request.Id, At(13), null), CancellationToken.None)).Succeeded);

        var schedule = await ctx.Schedule().HandleAsync(tech.Id, Day, CancellationToken.None);

        Assert.Equal(2, schedule.AppointmentCount);
        Assert.All(schedule.Appointments, a => Assert.Equal("OK", a.RoutingStatus));
        Assert.Equal("BASE", schedule.Appointments[0].InboundTravel!.OriginKind);
        Assert.Equal(schedule.Appointments[0].AppointmentId, schedule.Appointments[1].InboundTravel!.OriginAppointmentId);
        Assert.True(schedule.EstimatedTravelDistanceMeters > 0);
        Assert.True(schedule.EstimatedTravelSeconds > 0);
        Assert.Equal(0, schedule.ConflictCount);

        // Cancelling the first job recalculates the second job's inbound leg from the base (one provider call).
        var calls = ctx.Travel.Requests.Count;
        await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(first.Customer.Id, first.Request.Id, null), CancellationToken.None);
        var after = await ctx.Schedule().HandleAsync(tech.Id, Day, CancellationToken.None);
        var remaining = Assert.Single(after.Appointments);
        Assert.Equal("OK", remaining.RoutingStatus);
        Assert.Equal("BASE", remaining.InboundTravel!.OriginKind);
        Assert.Equal(0, after.RoutesNeedingReview);
        Assert.Null(after.Recalculation);
        Assert.Equal(calls + 1, ctx.Travel.Requests.Count);
    }

    [Fact]
    public async Task Booking_between_two_jobs_records_the_new_inbound_and_the_next_jobs_new_inbound()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var morning = await ctx.SeedSurveyRequestAsync(District1, "Morning");
        var afternoon = await ctx.SeedSurveyRequestAsync(District1, "Afternoon");
        var middle = await ctx.SeedSurveyRequestAsync(District7, "Middle");
        await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(morning.Customer.Id, morning.Request.Id, At(8), null), CancellationToken.None);
        await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(afternoon.Customer.Id, afternoon.Request.Id, At(15, 30), null), CancellationToken.None);

        var result = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(middle.Customer.Id, middle.Request.Id, At(13), null), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var middleId = result.Value!.AppointmentId;
        var afternoonAppointment = await ctx.ActiveAppointmentAsync(afternoon.Request.Id);
        var estimates = await ctx.Db.TechnicianTravelEstimates.AsNoTracking().ToListAsync();
        Assert.Contains(estimates, e => e.AppointmentId == middleId && e.OriginKind == "APPOINTMENT");
        Assert.Contains(estimates, e => e.AppointmentId == afternoonAppointment.Id && e.OriginAppointmentId == middleId);
        // The outbound leg (13:00–14:30 → 15:30) departs when the middle job ends.
        Assert.Contains(ctx.Travel.Requests, r => r.DepartureAtUtc == At(14, 30).ToUniversalTime() && r.Origin == District7);
    }
}
