using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Flow 1 self-acceptance, hold re-homing, release, escalation and the staff fallbacks. Geography is SYNTHETIC.</summary>
public sealed class SelfAcceptanceTests
{
    [Fact]
    public async Task A_technician_of_the_area_accepts_and_the_request_becomes_scheduled()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        var open = await ctx.Jobs().ListOpenAsync(tech.Id, CancellationToken.None);
        var offered = Assert.Single(open);
        Assert.True(offered.CanAccept);
        Assert.Equal("Z-BINH-TAN", offered.Zone!.Code);

        var result = await ctx.AcceptAsync(tech.Id, appointment.Id, offered.Revision);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Value!.Changed);
        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Accepted, stored.Status);
        Assert.Equal(AssignmentSources.SelfAccepted, stored.AssignmentSource);
        Assert.Equal(tech.Id, stored.AssignedTechnicianUserId);
        Assert.Equal(tech.Id, stored.ReservedTechnicianUserId);
        Assert.NotNull(stored.AcceptedAt);
        Assert.Null(stored.AssignedBySaleUserId);
        var surveyRequest = await ctx.RequestAsync(request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Scheduled, surveyRequest.Status);
        Assert.Equal(stored.StartsAt, surveyRequest.ScheduledAt);
        Assert.Null(surveyRequest.AssignedSaleId);                                     // ownership is independent

        var customer = (await ctx.CustomerQueries().ListAsync(request.Customer.Id, CancellationToken.None)).Single().Appointment!;
        Assert.Equal(AcceptanceStatuses.Confirmed, customer.AcceptanceStatus);
        Assert.Equal("Tech A", customer.AssignedTechnicianName);
        Assert.Contains(await ctx.Db.TechnicianAppointmentEvents.Select(e => e.EventType).ToListAsync(), t => t == AppointmentEventTypes.Accepted);
        Assert.Empty(await ctx.Jobs().ListOpenAsync(tech.Id, CancellationToken.None));
        Assert.Single(await ctx.Jobs().ListAsync(tech.Id, CancellationToken.None));
    }

    [Fact]
    public async Task A_technician_in_several_areas_sees_and_accepts_jobs_of_each()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianInZonesAsync("Two areas", ["Z-D1", "Z-BINH-TAN"]);
        var inD1 = await ctx.SeedSurveyRequestAsync(District1, "D1");
        var inBinhTan = await ctx.SeedSurveyRequestAsync(BinhTan, "Binh Tan");
        var a = await ctx.BookAsync(inD1, At(8));
        var b = await ctx.BookAsync(inBinhTan, At(13));

        Assert.Equal(2, (await ctx.Jobs().ListOpenAsync(tech.Id, CancellationToken.None)).Count);
        Assert.True((await ctx.AcceptAsync(tech.Id, a.Id)).Succeeded);
        Assert.True((await ctx.AcceptAsync(tech.Id, b.Id)).Succeeded);
    }

    [Fact]
    public async Task Every_commune_of_a_zone_is_served_by_its_members_and_the_zone_is_recorded()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, communeCode: BinhTriDong);   // the zone's second commune

        var appointment = await ctx.BookAsync(request, At(9));

        Assert.Equal((await ctx.EnsureZoneAsync("Z-BINH-TAN")).Id, appointment.OperationalZoneId);
        Assert.Equal(tech.Id, appointment.ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Technicians_cannot_accept_or_see_jobs_outside_their_areas()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var outsider = await ctx.SeedTechnicianAsync("District 7 tech", District7);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        Assert.Empty(await ctx.Jobs().ListOpenAsync(outsider.Id, CancellationToken.None));
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetOpenAsync(outsider.Id, appointment.Id, CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().GetAsync(outsider.Id, appointment.Id, CancellationToken.None)).ErrorCode);
        // Out-of-area acceptance is "not found" (no existence or state disclosure), before any other check.
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.AcceptAsync(outsider.Id, appointment.Id)).ErrorCode);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(appointment.Id)).Status);
    }

    [Fact]
    public async Task Suspended_disabled_or_absent_technicians_cannot_accept()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var suspended = await ctx.SeedTechnicianAsync("Suspended", BinhTan);
        var disabled = await ctx.SeedTechnicianAsync("Disabled", BinhTan, active: false);
        var onLeave = await ctx.SeedTechnicianAsync("On leave", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        await ctx.Db.UserAccounts.Where(u => u.Id == suspended.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Suspended));
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), onLeave.Id, At(8), At(12), "Leave"), CancellationToken.None)).Succeeded);

        Assert.Equal(SchedulingErrorCodes.TechnicianInactive, (await ctx.AcceptAsync(suspended.Id, appointment.Id)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianSchedulingDisabled, (await ctx.AcceptAsync(disabled.Id, appointment.Id)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianOnLeave, (await ctx.AcceptAsync(onLeave.Id, appointment.Id)).ErrorCode);
        var open = Assert.Single(await ctx.Jobs().ListOpenAsync(onLeave.Id, CancellationToken.None));
        Assert.False(open.CanAccept);
        Assert.Equal(SchedulingErrorCodes.TechnicianOnLeave, open.Reason);
    }

    [Fact]
    public async Task Repeating_the_acceptance_is_idempotent_and_a_second_technician_gets_already_taken()
    {
        await using var ctx = Create();
        var first = await ctx.SeedTechnicianAsync("First", BinhTan);
        var second = await ctx.SeedTechnicianAsync("Second", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        var won = await ctx.AcceptAsync(first.Id, appointment.Id);
        var repeat = await ctx.AcceptAsync(first.Id, appointment.Id);
        var lost = await ctx.AcceptAsync(second.Id, appointment.Id);

        Assert.True(won.Succeeded);
        Assert.True(repeat.Succeeded);
        Assert.False(repeat.Value!.Changed);
        Assert.Equal(won.Value!.Revision, repeat.Value.Revision);
        Assert.Equal(SchedulingErrorCodes.AppointmentAlreadyTaken, lost.ErrorCode);
        Assert.Equal(first.Id, (await ctx.AppointmentAsync(appointment.Id)).AssignedTechnicianUserId);
        Assert.Single(await ctx.Db.TechnicianAppointmentEvents.Where(e => e.EventType == AppointmentEventTypes.Accepted).ToListAsync());
    }

    [Fact]
    public async Task A_stale_expected_revision_is_refused()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));

        Assert.Equal(SchedulingErrorCodes.ConcurrencyConflict, (await ctx.AcceptAsync(tech.Id, appointment.Id, appointment.Revision + 5)).ErrorCode);
    }

    [Fact]
    public async Task A_confirmed_survey_too_close_blocks_acceptance()
    {
        await using var ctx = Create();
        var busy = await ctx.SeedTechnicianAsync("Busy", BinhTan);
        await ctx.SeedTechnicianAsync("Free", BinhTan);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        var a = await ctx.BookAsync(first, At(9));
        Assert.True((await ctx.AcceptAsync(busy.Id, a.Id)).Succeeded);
        var b = await ctx.BookAsync(second, At(10));                       // held by "Free"

        Assert.Equal(SchedulingErrorCodes.TechnicianScheduleConflict, (await ctx.AcceptAsync(busy.Id, b.Id)).ErrorCode);
    }

    [Fact]
    public async Task Accepting_hands_the_technicians_conflicting_hold_to_the_freed_holder()
    {
        await using var ctx = Create();
        var x = await ctx.SeedTechnicianAsync("X", BinhTan);
        var y = await ctx.SeedTechnicianAsync("Y", BinhTan);
        var r1 = await ctx.SeedSurveyRequestAsync(BinhTan, "One");
        var r2 = await ctx.SeedSurveyRequestAsync(BinhTan, "Two");
        var held = await ctx.BookAsync(r1, At(9));
        var holderOfFirst = held.ReservedTechnicianUserId!.Value;
        var other = holderOfFirst == x.Id ? y.Id : x.Id;
        var target = await ctx.BookAsync(r2, At(9, 30));
        Assert.Equal(other, target.ReservedTechnicianUserId);

        // The holder of the 09:00 job accepts the 09:30 job: its 09:00 hold must move to the other technician.
        var result = await ctx.AcceptAsync(holderOfFirst, target.Id);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(holderOfFirst, (await ctx.AppointmentAsync(target.Id)).AssignedTechnicianUserId);
        var moved = await ctx.AppointmentAsync(held.Id);
        Assert.Equal(other, moved.ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, moved.Status);
        Assert.Contains(await ctx.Db.TechnicianAppointmentEvents.ToListAsync(), e => e.EventType == AppointmentEventTypes.HoldMoved && e.AppointmentId == held.Id);
    }

    [Fact]
    public async Task Acceptance_fails_and_changes_nothing_when_a_conflicting_hold_cannot_be_rehomed()
    {
        await using var ctx = Create();
        var x = await ctx.SeedTechnicianInZonesAsync("X", ["Z-D1", "Z-BINH-TAN"]);   // the only District 1 technician
        await ctx.SeedTechnicianAsync("Y", BinhTan);
        var d1 = await ctx.SeedSurveyRequestAsync(District1, "D1");
        var binhTan = await ctx.SeedSurveyRequestAsync(BinhTan, "Binh Tan");
        var held = await ctx.BookAsync(d1, At(9));
        Assert.Equal(x.Id, held.ReservedTechnicianUserId);
        var target = await ctx.BookAsync(binhTan, At(9, 30));

        var result = await ctx.AcceptAsync(x.Id, target.Id);

        Assert.Equal(SchedulingErrorCodes.TechnicianScheduleConflict, result.ErrorCode);
        Assert.Equal(x.Id, (await ctx.AppointmentAsync(held.Id)).ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(target.Id)).Status);
    }

    [Fact]
    public async Task A_cancelled_appointment_cannot_be_accepted()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, null), CancellationToken.None)).Succeeded);

        var result = await ctx.AcceptAsync(tech.Id, appointment.Id);

        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, result.ErrorCode);
        Assert.Equal(409, Api.Controllers.SchedulingControllerBase.StatusFor(result.ErrorCode!));
    }

    [Fact]
    public async Task Past_the_deadline_the_query_path_and_the_worker_escalate_and_technicians_may_still_accept()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);
        ctx.Clock.Now = appointment.AcceptanceDeadlineAt!.Value.AddMinutes(1);

        // Worker-independent: the staff queue already shows it.
        var queued = Assert.Single(await ctx.Escalated().HandleAsync(owner.Id, CancellationToken.None));
        Assert.Equal(AppointmentStatuses.PendingAcceptance, queued.Status);
        Assert.True(queued.OwnedByMe);
        Assert.True(queued.HasEligibleTechnician);

        Assert.Equal(1, await ctx.Escalation().EscalateDueAsync(10, CancellationToken.None));
        Assert.Equal(0, await ctx.Escalation().EscalateDueAsync(10, CancellationToken.None));          // conditional: once
        var escalated = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Escalated, escalated.Status);
        Assert.NotNull(escalated.EscalatedAt);
        Assert.Contains(await ctx.Db.SchedulingNotifications.ToListAsync(), n => n.RecipientUserId == owner.Id && n.Type == AppointmentEventTypes.Escalated);
        Assert.Equal(AppointmentStatuses.Escalated, Assert.Single(await ctx.Escalated().HandleAsync(owner.Id, CancellationToken.None)).Status);

        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);
        Assert.Empty(await ctx.Escalated().HandleAsync(owner.Id, CancellationToken.None));
    }

    [Fact]
    public async Task The_sales_owner_assigns_while_pending_and_other_sales_are_refused()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);
        var stranger = await ctx.SeedSalesAsync("Stranger");

        var refused = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(stranger.Id, request.Request.Id, tech.Id, "Want it"), CancellationToken.None);
        var noReason = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, tech.Id, " "), CancellationToken.None);
        var assigned = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(owner.Id, request.Request.Id, tech.Id, "Customer called", appointment.Revision), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotOwned, refused.ErrorCode);
        Assert.Equal(403, Api.Controllers.SchedulingControllerBase.StatusFor(refused.ErrorCode!));
        Assert.Equal(SchedulingErrorCodes.AssignmentReasonRequired, noReason.ErrorCode);
        Assert.True(assigned.Succeeded, assigned.Message);
        Assert.Equal(AppointmentStatuses.Assigned, assigned.Value!.AppointmentStatus);
        Assert.Equal(AssignmentSources.SalesAssigned, assigned.Value.AssignmentSource);
        Assert.Equal("SCHEDULED", assigned.Value.SurveyRequestStatus);
        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(owner.Id, stored.AssignedBySaleUserId);
        Assert.Equal("Customer called", stored.AssignmentReason);
        // The assigned technician "accepting" again changes nothing.
        var again = await ctx.AcceptAsync(tech.Id, appointment.Id);
        Assert.True(again.Succeeded);
        Assert.False(again.Value!.Changed);
        Assert.Equal(AppointmentStatuses.Assigned, (await ctx.AppointmentAsync(appointment.Id)).Status);
    }

    [Fact]
    public async Task A_manager_override_replaces_an_accepted_technician_and_records_the_reassignment()
    {
        await using var ctx = Create();
        var first = await ctx.SeedTechnicianAsync("First", BinhTan);
        var second = await ctx.SeedTechnicianAsync("Second", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(first.Id, appointment.Id)).Succeeded);
        var manager = await ctx.Inner.SeedUserAsync("Manager");

        var result = await ctx.Assign().OverrideAsync(new OverrideTechnicianCommand(manager.Id, appointment.Id, second.Id, "First is sick"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Value!.Reassigned);
        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AssignmentSources.ManagerOverride, stored.AssignmentSource);
        Assert.Equal(second.Id, stored.AssignedTechnicianUserId);
        Assert.Equal(second.Id, stored.ReservedTechnicianUserId);
        var reassigned = await ctx.Db.TechnicianAppointmentEvents.SingleAsync(e => e.EventType == AppointmentEventTypes.Reassigned);
        Assert.Equal(first.Id, reassigned.PreviousTechnicianUserId);
        Assert.Equal(second.Id, reassigned.NewTechnicianUserId);
        Assert.Contains(await ctx.Db.SchedulingNotifications.ToListAsync(), n => n.RecipientUserId == first.Id);
        // The first technician lost the job and its capacity: another booking at 09:00 can now use them.
        Assert.Empty(await ctx.Jobs().ListAsync(first.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Staff_cannot_assign_started_or_cancelled_appointments()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        var manager = await ctx.Inner.SeedUserAsync("Manager");
        ctx.Clock.Now = At(9, 15).ToUniversalTime();

        Assert.Equal(SchedulingErrorCodes.InvalidBookingState,
            (await ctx.Assign().OverrideAsync(new OverrideTechnicianCommand(manager.Id, appointment.Id, tech.Id, "Late"), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Release_moves_the_hold_to_another_technician_or_escalates()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var b = await ctx.SeedTechnicianAsync("B", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(a.Id, appointment.Id)).Succeeded);

        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound,
            (await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(b.Id, appointment.Id, "Not mine"), CancellationToken.None)).ErrorCode);
        var released = await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(a.Id, appointment.Id, "Motorbike broke down"), CancellationToken.None);

        Assert.True(released.Succeeded, released.Message);
        var stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, stored.Status);   // deadline not passed yet
        Assert.Equal(b.Id, stored.ReservedTechnicianUserId);
        Assert.Null(stored.AssignedTechnicianUserId);
        Assert.Null(stored.AssignmentSource);
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(request.Request.Id)).Status);
        Assert.Contains(await ctx.Db.TechnicianAppointmentEvents.ToListAsync(), e => e.EventType == AppointmentEventTypes.Released && e.Reason == "Motorbike broke down");

        // B accepts, then releases with nobody else available: escalated without a holder.
        Assert.True((await ctx.AcceptAsync(b.Id, appointment.Id)).Succeeded);
        await ctx.Db.TechnicianProfiles.Where(p => p.UserId == a.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsSchedulingActive, false));
        Assert.True((await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(b.Id, appointment.Id, "Family emergency"), CancellationToken.None)).Succeeded);
        stored = await ctx.AppointmentAsync(appointment.Id);
        Assert.Equal(AppointmentStatuses.Escalated, stored.Status);
        Assert.Null(stored.ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Availability_lists_eligible_technicians_and_reasons_for_the_others()
    {
        await using var ctx = Create();
        var holder = await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var disabled = await ctx.SeedTechnicianAsync("Disabled", BinhTan, active: false);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);
        var stranger = await ctx.SeedSalesAsync("Stranger");

        var view = (await ctx.Availability().HandleAsync(new TechnicianAvailabilityQuery(owner.Id, false, request.Request.Id), CancellationToken.None)).Value!;
        var asManager = await ctx.Availability().HandleAsync(new TechnicianAvailabilityQuery(stranger.Id, true, request.Request.Id), CancellationToken.None);
        var refused = await ctx.Availability().HandleAsync(new TechnicianAvailabilityQuery(stranger.Id, false, request.Request.Id), CancellationToken.None);

        var eligible = Assert.Single(view.Technicians);
        Assert.Equal(holder.Id, eligible.TechnicianUserId);
        Assert.True(eligible.IsCapacityHolder);
        var ineligible = Assert.Single(view.Ineligible);
        Assert.Equal(disabled.Id, ineligible.TechnicianUserId);
        Assert.Equal(SchedulingErrorCodes.TechnicianSchedulingDisabled, ineligible.Reason);
        Assert.True(asManager.Succeeded);
        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotOwned, refused.ErrorCode);
    }

    [Fact]
    public async Task Sales_reschedule_can_assign_a_technician_at_the_new_time()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = await ctx.BookAsync(request, At(9));
        var owner = await ctx.ClaimAsync(request);

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner.Id, request.Request.Id, At(14), "Customer prefers afternoon", tech.Id), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(original.Id, result.Value!.PreviousAppointmentId);
        Assert.Equal(AppointmentStatuses.Assigned, result.Value.AppointmentStatus);
        Assert.Equal(CancellationReasons.RescheduledBySales, (await ctx.AppointmentAsync(original.Id)).CancellationReason);
        Assert.Equal("SCHEDULED", result.Value.SurveyRequestStatus);
    }

    [Fact]
    public async Task Managers_move_commercial_ownership_with_an_audit_entry()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var first = await ctx.ClaimAsync(request);
        var second = await ctx.SeedSalesAsync("Second");
        var notSales = await ctx.Inner.SeedUserAsync("Not sales");
        var manager = await ctx.Inner.SeedUserAsync("Manager");

        Assert.Equal(SchedulingErrorCodes.SalesUserInvalid,
            (await ctx.SalesOwner().HandleAsync(new SetSalesOwnerCommand(manager.Id, request.Request.Id, notSales.Id, "Move"), CancellationToken.None)).ErrorCode);
        var moved = await ctx.SalesOwner().HandleAsync(new SetSalesOwnerCommand(manager.Id, request.Request.Id, second.Id, "First is on holiday"), CancellationToken.None);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal(first.Id, moved.Value!.PreviousSaleUserId);
        Assert.Equal(second.Id, (await ctx.RequestAsync(request.Request.Id)).AssignedSaleId);
        var audit = await ctx.Db.SurveyRequestEvents.SingleAsync();
        Assert.Equal(manager.Id, audit.ActorUserId);
        Assert.Equal("First is on holiday", audit.Reason);
    }
}
