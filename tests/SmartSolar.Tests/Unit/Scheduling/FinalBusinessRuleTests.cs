using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Dispatch;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Final approved business rules over SQLite: Admin-only zone structure and Manager-scoped technician memberships (enforced in the
/// handlers against the actor's current roles), released dispatch-assigned jobs returning to the dispatch queue, temporarily
/// unserviceable zones accepting requests through dispatch, and out-of-scope locations being refused.
/// </summary>
public sealed class FinalBusinessRuleTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ------------------------------------------------------------------ zone authorization

    [Fact]
    public async Task Only_an_admin_can_create_update_deactivate_or_remap_a_zone_even_past_the_controller()
    {
        await using var ctx = Create();
        await ctx.EnsureCatalogAsync();
        var admin = (await ctx.SeedStaffAsync("Admin", RoleCodes.Admin)).Id;
        var manager = (await ctx.SeedStaffAsync("Manager", RoleCodes.Manager)).Id;
        var zones = ctx.Zones();
        var zone = (await zones.CreateAsync(new CreateZoneCommand(admin, "KHU-A", "Khu A", true, true, manager), None)).Value!;

        Assert.Equal(SchedulingErrorCodes.ZoneManagementForbidden,
            (await zones.CreateAsync(new CreateZoneCommand(manager, "KHU-B", "Khu B", true, true, null), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ZoneManagementForbidden,
            (await zones.UpdateAsync(new UpdateZoneCommand(manager, zone.ZoneId, "Renamed", false, null, manager, 0), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ZoneManagementForbidden,
            (await zones.SetCommunesAsync(new SetZoneCommunesCommand(manager, zone.ZoneId, [BenThanh], null, 0), None)).ErrorCode);
        // Even the responsible manager of the zone cannot change its structure; nor can an account that no longer is an Admin.
        var formerAdmin = await ctx.SeedStaffAsync("Former admin", RoleCodes.Admin);
        await ctx.Db.UserRoles.Where(r => r.UserId == formerAdmin.Id).ExecuteDeleteAsync();
        Assert.Equal(SchedulingErrorCodes.ZoneManagementForbidden,
            (await zones.UpdateAsync(new UpdateZoneCommand(formerAdmin.Id, zone.ZoneId, "Renamed", false, null, manager, 0), None)).ErrorCode);

        var stored = await ctx.Db.OperationalZones.AsNoTracking().SingleAsync();
        Assert.Equal(("Khu A", true, 0), (stored.Name, stored.IsActive, stored.Revision));
        Assert.Empty(await ctx.Db.OperationalZoneCommunes.ToListAsync());
        Assert.Equal(403, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.ZoneManagementForbidden));

        var renamed = await zones.UpdateAsync(new UpdateZoneCommand(admin, zone.ZoneId, "Khu A (mới)", null, null, manager, 0), None);
        Assert.True(renamed.Succeeded, renamed.Message);
        Assert.Equal(admin, (await ctx.Db.OperationalZones.AsNoTracking().SingleAsync()).UpdatedByUserId);
    }

    [Fact]
    public async Task A_manager_changes_technician_memberships_only_in_the_zones_they_are_responsible_for()
    {
        await using var ctx = Create();
        var admin = (await ctx.SeedStaffAsync("Admin", RoleCodes.Admin)).Id;
        var manager = (await ctx.SeedStaffAsync("Manager", RoleCodes.Manager)).Id;
        var otherManager = (await ctx.SeedStaffAsync("Other manager", RoleCodes.Manager)).Id;
        var zones = ctx.Zones();
        var mine = (await zones.CreateAsync(new CreateZoneCommand(admin, "KHU-MINE", "Mine", true, true, manager), None)).Value!.ZoneId;
        var theirs = (await zones.CreateAsync(new CreateZoneCommand(admin, "KHU-THEIRS", "Theirs", true, true, otherManager), None)).Value!.ZoneId;
        var unowned = (await zones.CreateAsync(new CreateZoneCommand(admin, "KHU-NONE", "No manager", true, true, null), None)).Value!.ZoneId;
        var tech = await ctx.SeedTechnicianInZonesAsync("Tech", []);
        var handlers = ctx.Admin();

        // In scope: add to and remove from the manager's own zone.
        var added = await handlers.SetZonesAsync(new SetTechnicianZonesCommand(manager, tech.Id, [mine]), None);
        Assert.True(added.Succeeded, added.Message);
        // Out of scope: another manager's zone, a zone without a responsible manager.
        var cross = await handlers.SetZonesAsync(new SetTechnicianZonesCommand(manager, tech.Id, [mine, theirs]), None);
        Assert.Equal(SchedulingErrorCodes.ZoneScopeForbidden, cross.ErrorCode);
        Assert.Contains(theirs.ToString(), System.Text.Json.JsonSerializer.Serialize(cross.Details));
        Assert.Equal(SchedulingErrorCodes.ZoneScopeForbidden,
            (await handlers.SetZonesAsync(new SetTechnicianZonesCommand(manager, tech.Id, [mine, unowned]), None)).ErrorCode);

        // Admin has global authority; afterwards the manager may keep the foreign membership unchanged but not remove it.
        Assert.True((await handlers.SetZonesAsync(new SetTechnicianZonesCommand(admin, tech.Id, [mine, theirs]), None)).Succeeded);
        Assert.Equal(SchedulingErrorCodes.ZoneScopeForbidden,
            (await handlers.SetZonesAsync(new SetTechnicianZonesCommand(manager, tech.Id, [mine]), None)).ErrorCode);
        var keepsForeign = await handlers.SetZonesAsync(new SetTechnicianZonesCommand(manager, tech.Id, [theirs]), None);   // removes only "mine"
        Assert.True(keepsForeign.Succeeded, keepsForeign.Message);
        // Neither a technician nor a sales user can manage memberships.
        var sales = await ctx.SeedSalesAsync();
        Assert.Equal(SchedulingErrorCodes.ZoneScopeForbidden,
            (await handlers.SetZonesAsync(new SetTechnicianZonesCommand(sales.Id, tech.Id, []), None)).ErrorCode);

        var active = await ctx.Db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.TechnicianUserId == tech.Id && m.RemovedAt == null).ToListAsync();
        Assert.Equal(theirs, Assert.Single(active).ZoneId);
        var history = await ctx.Db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.ZoneId == mine).ToListAsync();
        Assert.All(history, m => Assert.NotNull(m.RemovedAt));
        Assert.Equal(manager, history.OrderBy(m => m.AssignedAt).Last().RemovedByUserId);
        Assert.Equal(403, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.ZoneScopeForbidden));
    }

    // ------------------------------------------------------------------ released cross-zone jobs

    private sealed record Released(SchedulingTestContext Ctx, SeededSurveyRequest Request, Guid Manager, Guid Admin, Guid FirstTech, Guid SecondTech,
        DispatchRequest Original, TechnicianAppointment Appointment, DispatchRequest Reopened);

    /// <summary>An uncovered request assigned cross-zone by a Manager, then released by its technician.</summary>
    private static async Task<Released> ReleasedAsync(SchedulingTestContext ctx)
    {
        var manager = (await ctx.SeedStaffAsync("Manager", RoleCodes.Manager)).Id;
        var admin = (await ctx.SeedStaffAsync("Admin", RoleCodes.Admin)).Id;
        var first = (await ctx.SeedTechnicianAsync("District 1 tech", District1)).Id;
        var second = (await ctx.SeedTechnicianAsync("District 7 tech", District7)).Id;
        var request = await ctx.SeedSurveyRequestAsync(null, "Uncovered customer", communeCode: BenThanh);
        var created = await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(request.Customer.Id, request.Request.Id, [], "Gate code 1234"), None);
        Assert.True(created.Succeeded, created.Message);
        var assigned = await ctx.Dispatch().AssignAsync(new DispatchTechnicianCommand(manager, created.Value!.DispatchRequestId, first, At(10), true,
            "Nearest installer", created.Value.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        var appointmentId = assigned.Value!.AppointmentId!.Value;

        var released = await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(first, appointmentId, "Vehicle broke down"), None);
        Assert.True(released.Succeeded, released.Message);
        Assert.Equal(AppointmentStatuses.Cancelled, released.Value!.AppointmentStatus);
        ctx.Db.ChangeTracker.Clear();
        var original = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == created.Value.DispatchRequestId);
        var appointment = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == appointmentId);
        var reopened = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Status == DispatchStatuses.Open);
        return new Released(ctx, request, manager, admin, first, second, original, appointment, reopened);
    }

    [Fact]
    public async Task A_released_cross_zone_job_returns_to_the_dispatch_queue_keeping_the_request_and_its_history()
    {
        await using var ctx = Create();
        var r = await ReleasedAsync(ctx);

        // The customer's survey request is the same one, Pending again (no new request, no cancellation by the customer).
        var survey = await ctx.RequestAsync(r.Request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Pending, survey.Status);
        Assert.Single(await ctx.Db.SurveyRequests.AsNoTracking().ToListAsync());
        // The released appointment is kept, cancelled with the release reason and its full history.
        Assert.Equal((AppointmentStatuses.Cancelled, CancellationReasons.ReleasedToDispatch), (r.Appointment.Status, r.Appointment.CancellationReason));
        Assert.NotNull(r.Appointment.CrossZoneAuthorizationId);
        var events = await ctx.Db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.AppointmentId == r.Appointment.Id).ToListAsync();
        var release = Assert.Single(events, e => e.EventType == AppointmentEventTypes.Released);
        Assert.Equal(("Vehicle broke down", r.FirstTech), (release.Reason, release.PreviousTechnicianUserId!.Value));
        Assert.Contains(events, e => e.EventType == AppointmentEventTypes.Assigned);
        Assert.Contains(events, e => e.EventType == AppointmentEventTypes.Cancelled);
        // The original dispatch request stays ASSIGNED as history and records the release; a new OPEN one is in the queue.
        Assert.Equal((DispatchStatuses.Assigned, r.Appointment.Id), (r.Original.Status, r.Original.AppointmentId!.Value));
        Assert.Contains(await ctx.Db.Set<DispatchEvent>().AsNoTracking().Where(e => e.DispatchRequestId == r.Original.Id).ToListAsync(),
            e => e.EventType == DispatchEventTypes.AssignmentReleased);
        Assert.Equal((DispatchReasons.AssignmentReleased, r.Request.Request.Id, r.Appointment.Id, "Gate code 1234", 240),
            (r.Reopened.Reason, r.Reopened.SurveyRequestId, r.Reopened.ReleasedAppointmentId!.Value, r.Reopened.CustomerNote, r.Reopened.ReviewSlaMinutes));
        Assert.Equal(ctx.Clock.Now.AddMinutes(240), r.Reopened.ReviewDueAt);
        var window = Assert.Single(await ctx.Db.Set<DispatchPreferredWindow>().AsNoTracking().Where(w => w.DispatchRequestId == r.Reopened.Id).ToListAsync());
        Assert.Equal((r.Appointment.StartsAt, r.Appointment.EndsAt), (window.StartsAt, window.EndsAt));
        var reopenedEvent = Assert.Single(await ctx.Db.Set<DispatchEvent>().AsNoTracking().Where(e => e.DispatchRequestId == r.Reopened.Id).ToListAsync());
        Assert.Equal((DispatchEventTypes.AssignmentReleased, r.FirstTech), (reopenedEvent.EventType, reopenedEvent.ActorUserId!.Value));
        Assert.Contains("Vehicle broke down", reopenedEvent.Detail);
        var notified = await ctx.Db.Set<DispatchNotification>().AsNoTracking().Where(n => n.EventId == reopenedEvent.Id).Select(n => n.RecipientUserId).ToListAsync();
        Assert.Contains(r.Manager, notified);
        Assert.Contains(r.Request.Customer.Id, notified);
        // Nothing holds capacity; the customer sees the work back with staff, not a confirmed appointment.
        Assert.Null(await ctx.Db.TechnicianAppointments.AsNoTracking().FirstOrDefaultAsync(a => a.Status != AppointmentStatuses.Cancelled));
        var customerView = (await ctx.Dispatch().GetForCustomerAsync(r.Request.Customer.Id, r.Request.Request.Id, None)).Value!;
        Assert.Equal((DispatchStatuses.Open, DispatchReasons.AssignmentReleased), (customerView.Status, customerView.Reason));
        var staffView = (await ctx.Dispatch().GetAsync(r.Reopened.Id, None)).Value!;
        Assert.Equal(r.Appointment.Id, staffView.ReleasedAppointmentId);
    }

    [Fact]
    public async Task Staff_reassign_the_released_job_to_another_technician_with_explicit_cross_zone_approval_and_full_rechecks()
    {
        await using var ctx = Create();
        var r = await ReleasedAsync(ctx);
        var service = ctx.Dispatch();

        // Cross-zone approval is required again, with a reason.
        Assert.Equal(DispatchErrorCodes.CrossZoneAuthorizationRequired, (await service.AssignAsync(new DispatchTechnicianCommand(r.Manager, r.Reopened.Id,
            r.SecondTech, r.Appointment.StartsAt, false, "Covering for District 1", r.Reopened.Revision), None)).ErrorCode);
        // Every scheduling rule is re-checked: the second technician is busy at that time in their own zone.
        var local = await ctx.SeedSurveyRequestAsync(District7, "District 7 customer");
        var busy = await ctx.BookAsync(local, At(10));
        Assert.True((await ctx.AcceptAsync(r.SecondTech, busy.Id)).Succeeded);
        Assert.Equal(EligibilityReasonsOf.ScheduleConflict, (await service.AssignAsync(new DispatchTechnicianCommand(r.Manager, r.Reopened.Id,
            r.SecondTech, r.Appointment.StartsAt, true, "Covering for District 1", r.Reopened.Revision), None)).ErrorCode);

        // The same slot with an available technician (the Admin approves this time).
        var third = (await ctx.SeedTechnicianAsync("Third tech", District7)).Id;
        var assigned = await service.AssignAsync(new DispatchTechnicianCommand(r.Admin, r.Reopened.Id, third, r.Appointment.StartsAt, true,
            "Covering for District 1", r.Reopened.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);

        var appointments = await ctx.Db.TechnicianAppointments.AsNoTracking().Where(a => a.SurveyRequestId == r.Request.Request.Id).ToListAsync();
        Assert.Equal(2, appointments.Count);                                                    // the released one is preserved
        var active = Assert.Single(appointments, a => a.Status != AppointmentStatuses.Cancelled);
        Assert.Equal((AppointmentStatuses.Assigned, AssignmentSources.ManagerDispatch, third, r.Appointment.StartsAt),
            (active.Status, active.AssignmentSource, active.AssignedTechnicianUserId!.Value, active.StartsAt));
        var authorizations = await ctx.Db.Set<CrossZoneAuthorization>().AsNoTracking().OrderBy(a => a.AuthorizedAt).ToListAsync();
        Assert.Equal(2, authorizations.Count);                                                  // the original and the new approval
        var latest = authorizations.Single(a => a.Id == active.CrossZoneAuthorizationId);
        Assert.Equal((r.Admin, third, "Covering for District 1", r.Reopened.Id), (latest.AuthorizedByUserId, latest.TechnicianUserId, latest.Reason, latest.DispatchRequestId!.Value));
        Assert.Equal(DispatchStatuses.Assigned, (await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == r.Reopened.Id)).Status);
        Assert.Equal(SurveyRequestStatus.Scheduled, (await ctx.RequestAsync(r.Request.Request.Id)).Status);
        // A second release of the new assignment goes back to the queue again: never stuck.
        Assert.True((await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(third, active.Id, "Sick"), None)).Succeeded);
        Assert.Single(await ctx.Db.Set<DispatchRequest>().AsNoTracking().Where(d => d.Status == DispatchStatuses.Open).ToListAsync());
    }

    [Fact]
    public async Task A_released_job_whose_time_must_change_goes_through_a_proposal_without_holding_capacity()
    {
        await using var ctx = Create();
        var r = await ReleasedAsync(ctx);
        var newStart = At(14, date: Day.AddDays(2));

        var proposed = await ctx.Dispatch().ProposeAsync(new DispatchTechnicianCommand(r.Manager, r.Reopened.Id, r.SecondTech, newStart, true,
            "New time, other technician", r.Reopened.Revision), None);
        Assert.True(proposed.Succeeded, proposed.Message);
        var proposal = await ctx.Db.Set<DispatchProposal>().AsNoTracking().SingleAsync(p => p.DispatchRequestId == r.Reopened.Id);
        Assert.Equal((1440, ctx.Clock.Now.AddMinutes(1440)), (proposal.ResponseWindowMinutes, proposal.ExpiresAt));
        Assert.Null(await ctx.Db.TechnicianAppointments.AsNoTracking().FirstOrDefaultAsync(a => a.Status != AppointmentStatuses.Cancelled));   // no hold

        var accepted = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(r.Request.Customer.Id, r.Request.Request.Id, proposal.Id, null, null), None);
        Assert.True(accepted.Succeeded, accepted.Message);
        var active = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Status != AppointmentStatuses.Cancelled);
        Assert.Equal((r.SecondTech, newStart.ToUniversalTime()), (active.AssignedTechnicianUserId!.Value, active.StartsAt));
    }

    [Fact]
    public async Task A_zone_appointment_released_by_its_technician_still_moves_within_the_zone_not_to_dispatch()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        await ctx.SeedTechnicianAsync("Binh Tan colleague", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, "Covered customer");
        var booked = await ctx.BookAsync(request, At(10));
        var holder = booked.ReservedTechnicianUserId!.Value;
        Assert.True((await ctx.AcceptAsync(holder, booked.Id)).Succeeded);

        var released = await ctx.Jobs().ReleaseAsync(new ReleaseAppointmentCommand(holder, booked.Id, "Clash"), None);

        Assert.True(released.Succeeded, released.Message);
        Assert.NotEqual(AppointmentStatuses.Cancelled, released.Value!.AppointmentStatus);
        Assert.Empty(await ctx.Db.Set<DispatchRequest>().ToListAsync());
        _ = tech;
    }

    // ------------------------------------------------------------------ temporarily unserviceable zones, out-of-scope locations

    [Fact]
    public async Task An_unserviceable_zone_accepts_the_request_through_dispatch_without_any_confirmation_and_needs_explicit_approval()
    {
        await using var ctx = Create();
        var manager = (await ctx.SeedStaffAsync("Manager", RoleCodes.Manager)).Id;
        await ctx.SeedStaffAsync("Admin", RoleCodes.Admin);
        var member = (await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan)).Id;
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, "Customer in a paused zone");
        await ctx.Db.OperationalZones.Where(z => z.Code == ZoneCodeOf(BinhTan)).ExecuteUpdateAsync(x => x.SetProperty(z => z.IsServiceable, false));

        // Slots and booking: the precise code, dispatch available, 409 (not 422), no appointment.
        var slots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), None)).Value!;
        Assert.Equal((CoverageStatuses.ZoneNotServiceable, true), (slots.CoverageStatus, slots.DispatchAvailable));
        var booking = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(9), null), None);
        Assert.Equal(CoverageStatuses.ZoneNotServiceable, booking.ErrorCode);
        Assert.Equal(409, Api.Controllers.SchedulingControllerBase.StatusFor(booking.ErrorCode!));
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());

        // The request is accepted: an actionable dispatch request with the 4-hour review SLA.
        var created = await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(request.Customer.Id, request.Request.Id, [], null), None);
        Assert.True(created.Succeeded, created.Message);
        var stored = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync();
        Assert.Equal((DispatchStatuses.Open, CoverageStatuses.ZoneNotServiceable, 240), (stored.Status, stored.Reason, stored.ReviewSlaMinutes));
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(request.Request.Id)).Status);
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());                     // never an automatic confirmation

        // Even a member of the paused zone needs the explicit approval; with it the normal checks apply and it books.
        var candidates = (await ctx.Dispatch().CandidatesAsync(stored.Id, At(10), None)).Value!;
        Assert.True(candidates.Candidates.Single(c => c.TechnicianUserId == member).CrossZone);
        Assert.Equal(DispatchErrorCodes.CrossZoneAuthorizationRequired, (await ctx.Dispatch().AssignAsync(new DispatchTechnicianCommand(manager, stored.Id, member,
            At(10), false, "Zone paused", stored.Revision), None)).ErrorCode);
        var assigned = await ctx.Dispatch().AssignAsync(new DispatchTechnicianCommand(manager, stored.Id, member, At(10), true, "Zone paused, approved exception",
            stored.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        Assert.Single(await ctx.Db.Set<CrossZoneAuthorization>().ToListAsync());
    }

    [Fact]
    public async Task A_paused_zone_dispatch_request_escalates_once_and_never_expires()
    {
        await using var ctx = Create();
        var admin = (await ctx.SeedStaffAsync("Admin", RoleCodes.Admin)).Id;
        await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, "Customer in a paused zone");
        await ctx.Db.OperationalZones.Where(z => z.Code == ZoneCodeOf(BinhTan)).ExecuteUpdateAsync(x => x.SetProperty(z => z.IsServiceable, false));
        var created = (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(request.Customer.Id, request.Request.Id, [], null), None)).Value!;
        var start = ctx.Clock.Now;

        for (var day = 1; day <= 30; day++)
        {
            ctx.Clock.Now = start.AddDays(day);
            await ctx.DispatchSweep().RunOnceAsync(50, None);
        }

        var stored = await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == created.DispatchRequestId);
        Assert.Equal(DispatchStatuses.Open, stored.Status);
        Assert.NotNull(stored.EscalatedAt);
        Assert.Equal(1, await ctx.Db.Set<DispatchEvent>().CountAsync(e => e.DispatchRequestId == stored.Id && e.EventType == DispatchEventTypes.Escalated));
        Assert.Equal(1, await ctx.Db.Set<DispatchNotification>().CountAsync(n => n.RecipientUserId == admin && n.Type == "DISPATCH_ESCALATED"));
    }

    [Fact]
    public async Task Invalid_or_out_of_scope_locations_are_refused_and_never_dispatched()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, "Customer");
        var zones = ctx.Zones();
        Task<SchedulingResult<SiteLocationView>> Set(string? province, string? commune)
            => zones.SetSiteLocationAsync(new SetSiteLocationCommand(request.Customer.Id, request.Site.Id, province, commune), None);

        var outside = await Set("01", "00004");                                                 // Hà Nội: a real province, not served
        Assert.Equal(SchedulingErrorCodes.LocationOutOfServiceArea, outside.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.LocationUnknown, (await Set("79", "00001")).ErrorCode);
        Assert.All(new[] { SchedulingErrorCodes.LocationOutOfServiceArea, SchedulingErrorCodes.LocationUnknown, SchedulingErrorCodes.SiteLocationRequired },
            code => Assert.Equal(422, Api.Controllers.SchedulingControllerBase.StatusFor(code)));
        Assert.False(CoverageStatuses.IsDispatchable(CoverageStatuses.SiteLocationRequired));

        // A site without a valid location: no booking and no dispatch request (the customer fixes the location first).
        var noLocation = await ctx.SeedSurveyRequestAsync(BinhTan, "No location", withCodes: false);
        Assert.Equal(CoverageStatuses.SiteLocationRequired,
            (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(noLocation.Customer.Id, noLocation.Request.Id, [], null), None)).ErrorCode);
        Assert.Empty(await ctx.Db.Set<DispatchRequest>().ToListAsync());
    }
}
