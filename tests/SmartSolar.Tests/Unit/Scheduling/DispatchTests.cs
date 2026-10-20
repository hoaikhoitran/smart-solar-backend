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
/// Uncovered-zone dispatch (spec2 §3) over SQLite: coverage codes on slots and bookings, request creation (idempotent, never for
/// SITE_LOCATION_REQUIRED / ZONE_NOT_SERVICEABLE), the state machine, manual cross-zone assignment (bypasses only the zone rule),
/// proposals without capacity hold, acceptance re-verification, expiry, SLA marking/escalation/reminders and the absence of any
/// automatic dispatch expiry.
/// </summary>
public sealed class DispatchTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed record World(SchedulingTestContext Ctx, SeededSurveyRequest Request, Guid Manager, Guid Admin);

    /// <summary>A Manager, an Admin and a customer request on Phường Bến Thành (no zone maps it).</summary>
    private static async Task<World> UncoveredAsync(SchedulingTestContext ctx)
    {
        var manager = await ctx.SeedStaffAsync("Dispatch manager", RoleCodes.Manager);
        var admin = await ctx.SeedStaffAsync("Dispatch admin", RoleCodes.Admin);
        var request = await ctx.SeedSurveyRequestAsync(null, "Uncovered customer", communeCode: BenThanh);
        return new World(ctx, request, manager.Id, admin.Id);
    }

    private static Task<SchedulingResult<CustomerDispatchView>> CreateAsync(SchedulingTestContext ctx, SeededSurveyRequest r, params PreferredWindowInput[] windows)
        => ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(r.Customer.Id, r.Request.Id, windows, "Please call first"), None);

    private static async Task<DispatchRequest> StoredAsync(SchedulingTestContext ctx, Guid id)
        => await ctx.Db.Set<DispatchRequest>().AsNoTracking().SingleAsync(d => d.Id == id);

    private static async Task<DispatchProposal> ProposalAsync(SchedulingTestContext ctx, Guid id)
        => await ctx.Db.Set<DispatchProposal>().AsNoTracking().SingleAsync(p => p.Id == id);

    private static DispatchTechnicianCommand Command(World w, Guid dispatchId, Guid technician, DateTimeOffset start, int revision, bool crossZone = true,
        string? reason = "Nearest free installer")
        => new(w.Manager, dispatchId, technician, start, crossZone, reason, revision);

    private static async Task<(World W, CustomerDispatchView Dispatch, Guid Technician)> OpenWithOutsideTechnicianAsync(SchedulingTestContext ctx)
    {
        var w = await UncoveredAsync(ctx);
        var tech = await ctx.SeedTechnicianAsync("District 1 tech", District1);
        var created = await CreateAsync(ctx, w.Request);
        Assert.True(created.Succeeded, created.Message);
        return (w, created.Value!, tech.Id);
    }

    // ------------------------------------------------------------------ slots and booking report the coverage

    [Fact]
    public async Task Slots_report_every_coverage_status_with_dispatch_availability_and_booking_returns_the_precise_code()
    {
        await using var ctx = Create();
        await ctx.EnsureZoneAsync("Z-THU-DUC");                                          // a zone without any member
        var noTech = await ctx.SeedSurveyRequestAsync(ThuDuc, "No technician");
        var noTechSlots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(noTech.Customer.Id, noTech.Site.Id, Day), None)).Value!;
        Assert.Equal((CoverageStatuses.NoZoneTechnician, true), (noTechSlots.CoverageStatus, noTechSlots.DispatchAvailable));
        var refused = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(noTech.Customer.Id, noTech.Request.Id, At(9), null), None);
        Assert.Equal(CoverageStatuses.NoZoneTechnician, refused.ErrorCode);
        Assert.Equal(new CapacityFailures.CoverageDetails(CoverageStatuses.NoZoneTechnician, true), refused.Details);

        // A member who never works: nobody is available in the whole horizon.
        var idle = await ctx.SeedTechnicianAsync("Never works", District7);
        Assert.True((await ctx.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(idle.Id, false, []), None)).Succeeded);
        var d7 = await ctx.SeedSurveyRequestAsync(District7, "D7");
        var d7Slots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(d7.Customer.Id, d7.Site.Id, Day), None)).Value!;
        Assert.Equal((CoverageStatuses.NoAvailableTechnician, true), (d7Slots.CoverageStatus, d7Slots.DispatchAvailable));

        // A member on leave on that date but free later: a schedule conflict for this date.
        var binhTan = await ctx.SeedTechnicianAsync("On leave Tuesday", BinhTan);
        var manager = await ctx.SeedStaffAsync("Manager", RoleCodes.Manager);
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(manager.Id, binhTan.Id, At(0), At(23, 59), "Leave"), None)).Succeeded);
        var bt = await ctx.SeedSurveyRequestAsync(BinhTan, "BT");
        var btSlots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(bt.Customer.Id, bt.Site.Id, Day), None)).Value!;
        Assert.Equal((CoverageStatuses.ScheduleConflict, true, 0), (btSlots.CoverageStatus, btSlots.DispatchAvailable, btSlots.Slots.Count));
        var nextDay = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(bt.Customer.Id, bt.Site.Id, Day.AddDays(1)), None)).Value!;
        Assert.Equal((CoverageStatuses.Covered, false), (nextDay.CoverageStatus, nextDay.DispatchAvailable));
        Assert.NotEmpty(nextDay.Slots);
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());               // never a confirmation without capacity
    }

    // ------------------------------------------------------------------ creation

    [Fact]
    public async Task An_uncovered_request_creates_one_open_dispatch_request_with_a_snapshotted_review_deadline_and_notifies_managers()
    {
        await using var ctx = Create();
        var w = await UncoveredAsync(ctx);
        var window = new PreferredWindowInput(At(8), At(12));

        var created = await CreateAsync(ctx, w.Request, window);
        var again = await CreateAsync(ctx, w.Request);

        Assert.True(created.Succeeded, created.Message);
        Assert.True(created.Value!.Created);
        Assert.Equal((DispatchStatuses.Open, CoverageStatuses.NoZoneCoverage), (created.Value.Status, created.Value.Reason));
        Assert.Single(created.Value.PreferredWindows);
        Assert.True(again.Succeeded);
        Assert.False(again.Value!.Created);                                             // idempotent: the open one is returned
        Assert.Equal(created.Value.DispatchRequestId, again.Value.DispatchRequestId);

        var stored = await StoredAsync(ctx, created.Value.DispatchRequestId);
        Assert.Equal(240, stored.ReviewSlaMinutes);                                     // dispatch.reviewSlaMinutes default
        Assert.Equal(ctx.Clock.Now.AddMinutes(240), stored.ReviewDueAt);
        Assert.Equal(stored.ReviewDueAt, stored.EscalationDueAt);                       // escalation delay 0
        Assert.Equal((0, 0), (stored.ReminderIntervalMinutes, stored.SettingsVersion));
        Assert.Null(stored.OperationalZoneId);
        var created1 = Assert.Single(await ctx.Db.Set<DispatchEvent>().Where(e => e.DispatchRequestId == stored.Id).ToListAsync());
        Assert.Equal(DispatchEventTypes.Created, created1.EventType);
        var notification = Assert.Single(await ctx.Db.Set<DispatchNotification>().ToListAsync());
        Assert.Equal((w.Manager, "DISPATCH_CREATED", DispatchNotificationStatuses.NotDelivered), (notification.RecipientUserId, notification.Type, notification.DeliveryStatus));
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(w.Request.Request.Id)).Status);
    }

    [Fact]
    public async Task Missing_locations_and_bookable_sites_are_not_dispatchable()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        var noLocation = await ctx.SeedSurveyRequestAsync(BinhTan, "No location", withCodes: false);
        var covered = await ctx.SeedSurveyRequestAsync(BinhTan, "Covered");

        var location = await CreateAsync(ctx, noLocation);
        Assert.Equal(CoverageStatuses.SiteLocationRequired, location.ErrorCode);
        Assert.Equal(new CapacityFailures.CoverageDetails(CoverageStatuses.SiteLocationRequired, false), location.Details);
        Assert.Equal(DispatchErrorCodes.DispatchNotNeeded, (await CreateAsync(ctx, covered)).ErrorCode);
        Assert.Equal(DispatchErrorCodes.DispatchNotNeeded, (await CreateAsync(ctx, covered, new PreferredWindowInput(At(8), At(12)))).ErrorCode);

        Assert.Equal(409, Api.Controllers.SchedulingControllerBase.StatusFor(DispatchErrorCodes.DispatchNotNeeded));
        Assert.Equal(422, Api.Controllers.SchedulingControllerBase.StatusFor(CoverageStatuses.SiteLocationRequired));
        Assert.Empty(await ctx.Db.Set<DispatchRequest>().ToListAsync());
    }

    [Fact]
    public async Task A_covered_site_whose_preferred_windows_are_all_taken_gets_a_schedule_conflict_dispatch()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Only tech", BinhTan, maxPerDay: 1);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        await ctx.BookAsync(first, At(9));
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");

        var created = await CreateAsync(ctx, second, new PreferredWindowInput(At(8), At(17)));

        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(CoverageStatuses.ScheduleConflict, created.Value!.Reason);
        Assert.NotNull((await StoredAsync(ctx, created.Value.DispatchRequestId)).OperationalZoneId);
        _ = tech;
    }

    [Fact]
    public async Task Preferred_windows_are_validated()
    {
        await using var ctx = Create();
        var w = await UncoveredAsync(ctx);
        var tooMany = Enumerable.Range(0, 6).Select(i => new PreferredWindowInput(At(8, date: Day.AddDays(i)), At(10, date: Day.AddDays(i)))).ToArray();

        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await CreateAsync(ctx, w.Request, tooMany)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await CreateAsync(ctx, w.Request, new PreferredWindowInput(At(10), At(9)))).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await CreateAsync(ctx, w.Request, new PreferredWindowInput(At(1, date: Day.AddDays(-2)), At(2, date: Day.AddDays(-2))))).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await CreateAsync(ctx, w.Request, new PreferredWindowInput(At(8, date: Day.AddDays(40)), At(9, date: Day.AddDays(40))))).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await CreateAsync(ctx, w.Request, new PreferredWindowInput(At(8), At(9, date: Day.AddDays(2))))).ErrorCode);
        Assert.Empty(await ctx.Db.Set<DispatchRequest>().ToListAsync());
    }

    [Fact]
    public async Task Other_customers_never_see_or_touch_a_dispatch_request()
    {
        await using var ctx = Create();
        var (w, dispatch, _) = await OpenWithOutsideTechnicianAsync(ctx);
        var stranger = await ctx.SeedSurveyRequestAsync(BinhTan, "Stranger");

        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotFound,
            (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(stranger.Customer.Id, w.Request.Request.Id, [], null), None)).ErrorCode);
        Assert.Equal(DispatchErrorCodes.DispatchRequestNotFound, (await ctx.Dispatch().GetForCustomerAsync(stranger.Customer.Id, w.Request.Request.Id, None)).ErrorCode);
        Assert.Equal(DispatchErrorCodes.DispatchRequestNotFound,
            (await ctx.Dispatch().CancelAsync(new CancelDispatchCommand(stranger.Customer.Id, w.Request.Request.Id, null), None)).ErrorCode);
        Assert.Equal(DispatchStatuses.Open, (await StoredAsync(ctx, dispatch.DispatchRequestId)).Status);
        Assert.Equal(404, Api.Controllers.SchedulingControllerBase.StatusFor(DispatchErrorCodes.DispatchRequestNotFound));
    }

    [Fact]
    public async Task Booking_is_refused_while_a_dispatch_request_is_open_and_allowed_after_the_customer_cancels_it()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan, maxPerDay: 1);
        var blocker = await ctx.SeedSurveyRequestAsync(BinhTan, "Blocker");
        await ctx.BookAsync(blocker, At(9));
        var r = await ctx.SeedSurveyRequestAsync(BinhTan, "Customer");
        var created = await CreateAsync(ctx, r, new PreferredWindowInput(At(8), At(17)));
        Assert.True(created.Succeeded, created.Message);

        var refused = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, At(9, date: Day.AddDays(1)), null), None);
        Assert.Equal(SchedulingErrorCodes.DispatchRequestOpen, refused.ErrorCode);

        var cancelled = await ctx.Dispatch().CancelAsync(new CancelDispatchCommand(r.Customer.Id, r.Request.Id, "Found a slot"), None);
        Assert.True(cancelled.Succeeded, cancelled.Message);
        Assert.Equal((DispatchStatuses.Closed, DispatchResolutions.CancelledByCustomer), (cancelled.Value!.Status, cancelled.Value.Resolution));
        Assert.True((await ctx.Dispatch().CancelAsync(new CancelDispatchCommand(r.Customer.Id, r.Request.Id, null), None)).Succeeded);   // repeat: 200
        Assert.True((await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, At(9, date: Day.AddDays(1)), null), None)).Succeeded);
    }

    // ------------------------------------------------------------------ direct (cross-zone) assignment

    [Fact]
    public async Task Cross_zone_assignment_needs_explicit_authorization_and_books_an_assigned_manager_dispatch_appointment()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var service = ctx.Dispatch();

        var noFlag = await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), dispatch.Revision, crossZone: false), None);
        var noReasonProposal = await service.ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), dispatch.Revision, reason: null), None);
        var noReason = await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), dispatch.Revision, reason: null), None);
        Assert.Equal(DispatchErrorCodes.CrossZoneAuthorizationRequired, noFlag.ErrorCode);
        Assert.Equal(DispatchErrorCodes.CrossZoneAuthorizationRequired, noReasonProposal.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.AssignmentReasonRequired, noReason.ErrorCode);
        Assert.Equal(DispatchErrorCodes.StaleRevision, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), dispatch.Revision + 1), None)).ErrorCode);
        Assert.Equal(409, Api.Controllers.SchedulingControllerBase.StatusFor(DispatchErrorCodes.CrossZoneAuthorizationRequired));
        Assert.Empty(await ctx.Db.Set<CrossZoneAuthorization>().ToListAsync());

        var assigned = await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), dispatch.Revision), None);

        Assert.True(assigned.Succeeded, assigned.Message);
        Assert.Equal((DispatchStatuses.Assigned, DispatchResolutions.Assigned), (assigned.Value!.Request.Status, assigned.Value.Resolution));
        var appointment = await ctx.ActiveAppointmentAsync(w.Request.Request.Id);
        Assert.Equal((AppointmentStatuses.Assigned, AssignmentSources.ManagerDispatch, tech, tech), (appointment.Status, appointment.AssignmentSource,
            appointment.AssignedTechnicianUserId!.Value, appointment.ReservedTechnicianUserId!.Value));
        Assert.Equal(w.Manager, appointment.AssignedBySaleUserId);
        Assert.Equal(At(9).ToUniversalTime(), appointment.StartsAt);
        Assert.Equal(assigned.Value.AppointmentId, appointment.Id);
        var authorization = Assert.Single(await ctx.Db.Set<CrossZoneAuthorization>().ToListAsync());
        Assert.Equal((dispatch.DispatchRequestId, tech, "Nearest free installer", w.Manager), (authorization.DispatchRequestId!.Value, authorization.TechnicianUserId,
            authorization.Reason, authorization.AuthorizedByUserId));
        Assert.Equal(authorization.Id, appointment.CrossZoneAuthorizationId);
        var survey = await ctx.RequestAsync(w.Request.Request.Id);
        Assert.Equal((SurveyRequestStatus.Scheduled, At(9).ToUniversalTime()), (survey.Status, survey.ScheduledAt!.Value));
        var events = assigned.Value.Events.Select(e => e.EventType).ToList();
        Assert.Contains(DispatchEventTypes.CrossZoneAuthorized, events);
        Assert.Contains(DispatchEventTypes.Assigned, events);
        Assert.Contains(await ctx.Db.SchedulingNotifications.ToListAsync(), n => n.RecipientUserId == tech && n.AppointmentId == appointment.Id);

        // Terminal: no further assignment, proposal or close.
        Assert.Equal(DispatchErrorCodes.InvalidState, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(13), assigned.Value.Request.Revision), None)).ErrorCode);
        Assert.Equal(DispatchErrorCodes.InvalidState, (await service.CloseAsync(new ResolveDispatchCommand(w.Manager, dispatch.DispatchRequestId, "x", null), None)).ErrorCode);
    }

    [Fact]
    public async Task Cross_zone_authorization_bypasses_only_the_zone_rule()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var onLeave = await ctx.SeedTechnicianAsync("On leave", District7);
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(w.Manager, onLeave.Id, At(8), At(12), "Leave"), None)).Succeeded);
        var disabled = await ctx.SeedTechnicianAsync("Disabled", District7, active: false);
        var busy = await ctx.SeedTechnicianAsync("Busy", ThuDuc);
        var busyCustomer = await ctx.SeedSurveyRequestAsync(ThuDuc, "Busy customer");
        var booked = await ctx.BookAsync(busyCustomer, At(9));
        Assert.True((await ctx.AcceptAsync(busy.Id, booked.Id)).Succeeded);            // a confirmed job blocks (unlike a re-homable hold)
        var service = ctx.Dispatch();

        Assert.Equal(EligibilityReasonsOf.OnLeave, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, onLeave.Id, At(9), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(EligibilityReasonsOf.SchedulingDisabled, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, disabled.Id, At(9), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(EligibilityReasonsOf.ScheduleConflict, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, busy.Id, At(9, 30), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(EligibilityReasonsOf.OutsideWorkingHours, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(19), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(EligibilityReasonsOf.ScheduleConflict, (await service.ProposeAsync(Command(w, dispatch.DispatchRequestId, busy.Id, At(9, 30), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(5, date: Day.AddDays(-1)), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianNotFound, (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, w.Manager, At(9), dispatch.Revision), None)).ErrorCode);
        Assert.Equal(DispatchStatuses.Open, (await StoredAsync(ctx, dispatch.DispatchRequestId)).Status);
        Assert.Empty(await ctx.Db.Set<CrossZoneAuthorization>().ToListAsync());   // nothing written by refused attempts
    }

    [Fact]
    public async Task A_zone_member_is_assigned_without_cross_zone_authorization()
    {
        await using var ctx = Create();
        var manager = await ctx.SeedStaffAsync("Manager", RoleCodes.Manager);
        var tech = await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan, maxPerDay: 1);
        var blocker = await ctx.SeedSurveyRequestAsync(BinhTan, "Blocker");
        await ctx.BookAsync(blocker, At(9));
        var r = await ctx.SeedSurveyRequestAsync(BinhTan, "Customer");
        var created = (await CreateAsync(ctx, r, new PreferredWindowInput(At(8), At(17)))).Value!;

        var candidates = (await ctx.Dispatch().CandidatesAsync(created.DispatchRequestId, At(9, date: Day.AddDays(1)), None)).Value!;
        var member = candidates.Candidates.First();
        Assert.Equal((tech.Id, false, true), (member.TechnicianUserId, member.CrossZone, member.IsEligible));
        Assert.Equal("Z-BINH-TAN", candidates.SiteZone!.Code);

        var assigned = await ctx.Dispatch().AssignAsync(new DispatchTechnicianCommand(manager.Id, created.DispatchRequestId, tech.Id, At(9, date: Day.AddDays(1)), false,
            "Next morning", created.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
        Assert.Empty(await ctx.Db.Set<CrossZoneAuthorization>().ToListAsync());
        Assert.Null((await ctx.ActiveAppointmentAsync(r.Request.Id)).CrossZoneAuthorizationId);
    }

    [Fact]
    public async Task Candidates_list_zone_members_first_then_other_zones_flagged_cross_zone_with_known_schedule_facts()
    {
        await using var ctx = Create();
        var (w, dispatch, outside) = await OpenWithOutsideTechnicianAsync(ctx);
        var onLeave = await ctx.SeedTechnicianAsync("On leave", District7);
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(w.Manager, onLeave.Id, At(8), At(12), "Leave"), None)).Succeeded);

        var result = (await ctx.Dispatch().CandidatesAsync(dispatch.DispatchRequestId, At(9), None)).Value!;

        Assert.Equal(CoverageStatuses.NoZoneCoverage, result.CoverageStatus);
        Assert.All(result.Candidates, c => Assert.True(c.CrossZone));                   // no zone: everybody is cross-zone
        var free = result.Candidates.Single(c => c.TechnicianUserId == outside);
        Assert.True(free.IsEligible);
        Assert.Equal("Z-D1", Assert.Single(free.Zones).Code);
        Assert.Equal(2, free.WorkingIntervals.Count);                                   // 08–12 and 13–17
        Assert.Equal(EligibilityReasonsOf.OnLeave, result.Candidates.Single(c => c.TechnicianUserId == onLeave.Id).Reason);
        Assert.Contains("No travel time", result.Note);
        Assert.Equal(At(9).AddMinutes(90), result.EndsAt);
    }

    // ------------------------------------------------------------------ proposals

    [Fact]
    public async Task A_proposal_reserves_nothing_and_its_expiry_is_the_earlier_of_the_window_and_the_start()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var service = ctx.Dispatch();

        var proposed = await service.ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(9, date: Day.AddDays(3)), dispatch.Revision), None);
        Assert.True(proposed.Succeeded, proposed.Message);
        Assert.Equal(DispatchStatuses.ProposalPending, proposed.Value!.Request.Status);
        var proposal = await ProposalAsync(ctx, proposed.Value.Proposals.Single().ProposalId);
        Assert.Equal((ProposalStatuses.Proposed, true, 1440), (proposal.Status, proposal.CrossZone, proposal.ResponseWindowMinutes));
        Assert.Equal(ctx.Clock.Now.AddMinutes(1440), proposal.ExpiresAt);               // the window ends before the start
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());                // no capacity hold

        // One live proposal at a time; withdrawing it reopens the request.
        Assert.Equal(DispatchErrorCodes.ProposalAlreadyPending,
            (await service.ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), proposed.Value.Request.Revision), None)).ErrorCode);
        Assert.Equal(DispatchErrorCodes.ProposalAlreadyPending,
            (await service.AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(9), proposed.Value.Request.Revision), None)).ErrorCode);
        var withdrawn = await service.WithdrawProposalAsync(new WithdrawProposalCommand(w.Manager, dispatch.DispatchRequestId, proposal.Id, "Wrong day"), None);
        Assert.True(withdrawn.Succeeded, withdrawn.Message);
        Assert.Equal(DispatchStatuses.Open, withdrawn.Value!.Request.Status);
        Assert.Equal(ProposalStatuses.Withdrawn, (await ProposalAsync(ctx, proposal.Id)).Status);

        // A start sooner than the response window (today 09:00, three hours from now): the proposal expires at the start.
        var today = Day.AddDays(-1);
        var soon = await service.ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(9, date: today), withdrawn.Value.Request.Revision), None);
        Assert.True(soon.Succeeded, soon.Message);
        var soonProposal = await ProposalAsync(ctx, soon.Value!.Proposals.First(p => p.Status == ProposalStatuses.Proposed).ProposalId);
        Assert.Equal(At(9, date: today).ToUniversalTime(), soonProposal.ExpiresAt);
        Assert.Equal(2, await ctx.Db.Set<CrossZoneAuthorization>().CountAsync());       // one per proposal
    }

    [Fact]
    public async Task Accepting_a_proposal_books_it_atomically_and_a_repeat_changes_nothing()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var proposed = (await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(10), dispatch.Revision), None)).Value!;
        var proposalId = proposed.Proposals.Single().ProposalId;
        var customerView = (await ctx.Dispatch().GetForCustomerAsync(w.Request.Customer.Id, w.Request.Request.Id, None)).Value!;
        Assert.Equal((proposalId, "District 1 tech"), (customerView.LiveProposal!.ProposalId, customerView.LiveProposal.TechnicianName));

        var accepted = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, 0, null), None);
        var again = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, 0, null), None);

        Assert.True(accepted.Succeeded, accepted.Message);
        Assert.True(again.Succeeded, again.Message);
        Assert.Equal((DispatchStatuses.Assigned, DispatchResolutions.Assigned), (accepted.Value!.Status, accepted.Value.Resolution));
        var appointment = Assert.Single(await ctx.Db.TechnicianAppointments.AsNoTracking().ToListAsync());
        Assert.Equal((AppointmentStatuses.Assigned, AssignmentSources.ManagerDispatch, tech, w.Manager), (appointment.Status, appointment.AssignmentSource,
            appointment.AssignedTechnicianUserId!.Value, appointment.AssignedBySaleUserId!.Value));
        var proposal = await ProposalAsync(ctx, proposalId);
        Assert.Equal(ProposalStatuses.Accepted, proposal.Status);
        Assert.Equal(proposal.CrossZoneAuthorizationId, appointment.CrossZoneAuthorizationId);
        Assert.Equal(appointment.Id, accepted.Value.AppointmentId);
        Assert.Equal(SurveyRequestStatus.Scheduled, (await ctx.RequestAsync(w.Request.Request.Id)).Status);
        Assert.Contains(await ctx.Db.Set<DispatchNotification>().ToListAsync(), n => n.RecipientUserId == w.Manager && n.Type == "DISPATCH_PROPOSAL_ACCEPTED");
    }

    [Fact]
    public async Task An_acceptance_that_can_no_longer_be_honoured_fails_the_proposal_and_reopens_the_request()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var proposed = (await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(10), dispatch.Revision), None)).Value!;
        var proposalId = proposed.Proposals.Single().ProposalId;

        // Nothing was reserved: the technician's own zone books and confirms them at that time meanwhile.
        var local = await ctx.SeedSurveyRequestAsync(District1, "Local customer");
        var localAppointment = await ctx.BookAsync(local, At(10));
        Assert.True((await ctx.AcceptAsync(tech, localAppointment.Id)).Succeeded);

        var accepted = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, null, null), None);

        Assert.Equal(EligibilityReasonsOf.ScheduleConflict, accepted.ErrorCode);
        var stored = await StoredAsync(ctx, dispatch.DispatchRequestId);
        Assert.Equal(DispatchStatuses.Open, stored.Status);                              // actionable again
        var proposal = await ProposalAsync(ctx, proposalId);
        Assert.Equal(ProposalStatuses.Failed, proposal.Status);
        Assert.StartsWith(EligibilityReasonsOf.ScheduleConflict, proposal.FailureReason);
        Assert.Null(await ctx.Db.TechnicianAppointments.AsNoTracking().FirstOrDefaultAsync(a => a.SurveyRequestId == w.Request.Request.Id));
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(w.Request.Request.Id)).Status);

        // Another proposal can be issued.
        var other = await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(13), stored.Revision), None);
        Assert.True(other.Succeeded, other.Message);
    }

    [Fact]
    public async Task Declining_reopens_the_request_and_answers_are_idempotent()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var proposed = (await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(10), dispatch.Revision), None)).Value!;
        var proposalId = proposed.Proposals.Single().ProposalId;
        var decline = new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, null, "Too early");

        var declined = await ctx.Dispatch().DeclineAsync(decline, None);
        var repeat = await ctx.Dispatch().DeclineAsync(decline, None);
        var acceptAfter = await ctx.Dispatch().AcceptAsync(decline, None);

        Assert.True(declined.Succeeded, declined.Message);
        Assert.True(repeat.Succeeded);
        Assert.Equal((DispatchStatuses.Open, ProposalStatuses.Declined), (declined.Value!.Status, declined.Value.LatestProposal!.Status));
        Assert.Null(declined.Value.LiveProposal);
        Assert.Equal(DispatchErrorCodes.ProposalNotPending, acceptAfter.ErrorCode);
        Assert.Equal("Too early", (await ProposalAsync(ctx, proposalId)).FailureReason);
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());
    }

    [Fact]
    public async Task The_worker_expires_only_the_proposal_never_the_request_and_late_acceptance_is_refused()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var proposed = (await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(9, date: Day.AddDays(3)), dispatch.Revision), None)).Value!;
        var proposalId = proposed.Proposals.Single().ProposalId;
        var expiresAt = (await ProposalAsync(ctx, proposalId)).ExpiresAt;

        ctx.Clock.Now = expiresAt.AddSeconds(-1);
        Assert.Equal(0, (await ctx.DispatchSweep().RunOnceAsync(50, None)).ExpiredProposals);
        ctx.Clock.Now = expiresAt;
        Assert.Equal(1, (await ctx.DispatchSweep().RunOnceAsync(50, None)).ExpiredProposals);
        Assert.Equal(0, (await ctx.DispatchSweep().RunOnceAsync(50, None)).ExpiredProposals);   // idempotent

        Assert.Equal(ProposalStatuses.Expired, (await ProposalAsync(ctx, proposalId)).Status);
        Assert.Equal(DispatchStatuses.Open, (await StoredAsync(ctx, dispatch.DispatchRequestId)).Status);
        var late = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, null, null), None);
        Assert.Equal(DispatchErrorCodes.ProposalExpired, late.ErrorCode);
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());
        Assert.Single(await ctx.Db.Set<DispatchEvent>().Where(e => e.EventType == DispatchEventTypes.ProposalExpired).ToListAsync());
    }

    [Fact]
    public async Task An_acceptance_after_the_expiry_but_before_the_worker_expires_the_proposal_itself()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var proposed = (await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(9, date: Day.AddDays(3)), dispatch.Revision), None)).Value!;
        var proposalId = proposed.Proposals.Single().ProposalId;
        ctx.Clock.Now = (await ProposalAsync(ctx, proposalId)).ExpiresAt.AddMinutes(1);

        var late = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, null, null), None);

        Assert.Equal(DispatchErrorCodes.ProposalExpired, late.ErrorCode);
        Assert.Equal(ProposalStatuses.Expired, (await ProposalAsync(ctx, proposalId)).Status);
        Assert.Equal(DispatchStatuses.Open, (await StoredAsync(ctx, dispatch.DispatchRequestId)).Status);
    }

    // ------------------------------------------------------------------ SLA, escalation, reminders, no expiry

    [Fact]
    public async Task The_worker_marks_overdue_at_the_review_deadline_escalates_to_admins_once_and_reminds_at_the_snapshot_interval()
    {
        await using var ctx = Create();
        var w = await UncoveredAsync(ctx);
        await ctx.UpdateSettingsAsync(w.Admin, (SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys.DispatchEscalationDelayMinutes, 60),
            (SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys.DispatchReminderIntervalMinutes, 30));
        var created = (await CreateAsync(ctx, w.Request)).Value!;
        var stored = await StoredAsync(ctx, created.DispatchRequestId);
        Assert.Equal((stored.ReviewDueAt.AddMinutes(60), 30, 1), (stored.EscalationDueAt, stored.ReminderIntervalMinutes, stored.SettingsVersion));

        ctx.Clock.Now = stored.ReviewDueAt.AddSeconds(-1);
        Assert.Equal(0, (await ctx.DispatchSweep().RunOnceAsync(50, None)).Total);
        ctx.Clock.Now = stored.ReviewDueAt;
        var atDue = await ctx.DispatchSweep().RunOnceAsync(50, None);
        Assert.Equal((1, 0, 0), (atDue.MarkedOverdue, atDue.Escalated, atDue.Reminders));
        Assert.True((await ctx.Dispatch().ListAsync(null, null, true, 1, 20, None)).Items.Single().Sla.Overdue);

        ctx.Clock.Now = stored.ReviewDueAt.AddMinutes(30);
        Assert.Equal(1, (await ctx.DispatchSweep().RunOnceAsync(50, None)).Reminders);
        Assert.Equal(0, (await ctx.DispatchSweep().RunOnceAsync(50, None)).Reminders);   // deduplicated
        ctx.Clock.Now = stored.ReviewDueAt.AddMinutes(60);
        var atEscalation = await ctx.DispatchSweep().RunOnceAsync(50, None);
        Assert.Equal((1, 1), (atEscalation.Escalated, atEscalation.Reminders));
        Assert.Equal(0, (await ctx.DispatchSweep().RunOnceAsync(50, None)).Escalated);  // once

        var after = await StoredAsync(ctx, stored.Id);
        Assert.Equal((DispatchStatuses.Open, 2), (after.Status, after.ReminderCount));  // still actionable
        Assert.Equal(stored.Revision, after.Revision);                                   // SLA metadata never invalidates staff views
        var notifications = await ctx.Db.Set<DispatchNotification>().AsNoTracking().ToListAsync();
        Assert.Equal(w.Admin, Assert.Single(notifications, n => n.Type == "DISPATCH_ESCALATED").RecipientUserId);
        Assert.Single(notifications, n => n.Type == "DISPATCH_OVERDUE" && n.RecipientUserId == w.Manager);
        Assert.Equal(2, notifications.Count(n => n.Type == "DISPATCH_REMINDER"));
        // Overdue requests stay actionable: staff can still propose.
        var proposed = await ctx.Dispatch().ProposeAsync(Command(w, stored.Id, (await ctx.SeedTechnicianAsync("Late tech", District1)).Id, At(9, date: Day.AddDays(2)), after.Revision), None);
        Assert.True(proposed.Succeeded, proposed.Message);
    }

    [Fact]
    public async Task Acceptance_re_checks_the_current_minimum_booking_lead_and_fails_the_proposal_when_it_is_too_late()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        await ctx.UpdateSettingsAsync(w.Admin, (SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys.MinimumBookingLeadMinutes, 60));
        var revision = (await StoredAsync(ctx, dispatch.DispatchRequestId)).Revision;
        // Today 09:00, three hours away: valid when proposed; the proposal expires only at the start.
        var start = At(9, date: Day.AddDays(-1));
        var proposed = await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, start, revision), None);
        Assert.True(proposed.Succeeded, proposed.Message);
        var proposalId = proposed.Value!.Proposals.Single().ProposalId;

        ctx.Clock.Now = start.ToUniversalTime().AddMinutes(-30);                         // inside the 60-minute lead, before the expiry
        var accepted = await ctx.Dispatch().AcceptAsync(new RespondToProposalCommand(w.Request.Customer.Id, w.Request.Request.Id, proposalId, null, null), None);

        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, accepted.ErrorCode);
        Assert.Equal(ProposalStatuses.Failed, (await ProposalAsync(ctx, proposalId)).Status);
        Assert.Equal(DispatchStatuses.Open, (await StoredAsync(ctx, dispatch.DispatchRequestId)).Status);
        Assert.Empty(await ctx.Db.TechnicianAppointments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Without_managers_admins_are_notified_once_through_the_escalation_not_also_as_overdue_staff()
    {
        await using var ctx = Create();
        var admin = await ctx.SeedStaffAsync("Only admin", RoleCodes.Admin);
        var request = await ctx.SeedSurveyRequestAsync(null, "Uncovered customer", communeCode: BenThanh);
        var created = (await ctx.Dispatch().CreateAsync(new CreateDispatchRequestCommand(request.Customer.Id, request.Request.Id, [], null), None)).Value!;
        var stored = await StoredAsync(ctx, created.DispatchRequestId);
        Assert.Equal(stored.ReviewDueAt, stored.EscalationDueAt);                       // default escalation delay 0

        ctx.Clock.Now = stored.ReviewDueAt;
        var sweep = await ctx.DispatchSweep().RunOnceAsync(50, None);

        Assert.Equal((1, 1), (sweep.MarkedOverdue, sweep.Escalated));                     // escalation never skips the overdue step
        var notifications = await ctx.Db.Set<DispatchNotification>().AsNoTracking()
            .Where(n => n.RecipientUserId == admin.Id && n.DispatchRequestId == stored.Id).ToListAsync();
        Assert.Equal("DISPATCH_ESCALATED", Assert.Single(notifications, n => n.Type != "DISPATCH_CREATED").Type);
        Assert.Single(await ctx.Db.Set<DispatchEvent>().AsNoTracking().Where(e => e.DispatchRequestId == stored.Id && e.EventType == DispatchEventTypes.Overdue).ToListAsync());
    }

    [Fact]
    public async Task Reminders_reach_every_overdue_request_even_when_the_backlog_exceeds_the_batch()
    {
        await using var ctx = Create();
        var w = await UncoveredAsync(ctx);
        await ctx.UpdateSettingsAsync(w.Admin, (SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys.DispatchReminderIntervalMinutes, 30));
        var first = (await CreateAsync(ctx, w.Request)).Value!;
        var other = await ctx.SeedSurveyRequestAsync(null, "Second uncovered customer", communeCode: BenThanh);
        var second = (await CreateAsync(ctx, other)).Value!;
        var due = (await StoredAsync(ctx, second.DispatchRequestId)).ReviewDueAt;

        ctx.Clock.Now = due;
        await ctx.DispatchSweep().RunOnceAsync(50, None);                                 // both overdue
        ctx.Clock.Now = due.AddMinutes(30);
        Assert.Equal(1, (await ctx.DispatchSweep().RunOnceAsync(1, None)).Reminders);
        Assert.Equal(1, (await ctx.DispatchSweep().RunOnceAsync(1, None)).Reminders);    // the other one, not starved by the batch of 1

        Assert.Equal((1, 1), ((await StoredAsync(ctx, first.DispatchRequestId)).ReminderCount, (await StoredAsync(ctx, second.DispatchRequestId)).ReminderCount));
    }

    [Fact]
    public async Task A_dispatch_request_never_expires_even_after_many_SLA_periods()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var start = ctx.Clock.Now;

        for (var day = 1; day <= 40; day++)
        {
            ctx.Clock.Now = start.AddDays(day);
            await ctx.DispatchSweep().RunOnceAsync(50, None);
        }

        var stored = await StoredAsync(ctx, dispatch.DispatchRequestId);
        Assert.Equal(DispatchStatuses.Open, stored.Status);
        Assert.Null(stored.Resolution);
        Assert.Null(stored.ResolvedAt);
        Assert.DoesNotContain("EXPIRED", DispatchStatuses.All);
        Assert.Equal(1, await ctx.Db.Set<DispatchEvent>().CountAsync(e => e.EventType == DispatchEventTypes.Escalated));
        // Still actionable 40 days later.
        var assigned = await ctx.Dispatch().AssignAsync(Command(w, dispatch.DispatchRequestId, tech, At(9, date: Day.AddDays(41)), stored.Revision), None);
        Assert.True(assigned.Succeeded, assigned.Message);
    }

    [Fact]
    public async Task Staff_resolve_with_a_reason_and_a_live_proposal_is_withdrawn()
    {
        await using var ctx = Create();
        var (w, dispatch, tech) = await OpenWithOutsideTechnicianAsync(ctx);
        var proposed = (await ctx.Dispatch().ProposeAsync(Command(w, dispatch.DispatchRequestId, tech, At(10), dispatch.Revision), None)).Value!;

        Assert.Equal(DispatchErrorCodes.ReasonRequired,
            (await ctx.Dispatch().MarkUnserviceableAsync(new ResolveDispatchCommand(w.Manager, dispatch.DispatchRequestId, " ", null), None)).ErrorCode);
        Assert.Equal(DispatchErrorCodes.StaleRevision,
            (await ctx.Dispatch().MarkUnserviceableAsync(new ResolveDispatchCommand(w.Manager, dispatch.DispatchRequestId, "Island", 0), None)).ErrorCode);
        var resolved = await ctx.Dispatch().MarkUnserviceableAsync(new ResolveDispatchCommand(w.Manager, dispatch.DispatchRequestId, "Island not served", proposed.Request.Revision), None);

        Assert.True(resolved.Succeeded, resolved.Message);
        Assert.Equal((DispatchStatuses.Unserviceable, DispatchResolutions.Unserviceable, "Island not served"),
            (resolved.Value!.Request.Status, resolved.Value.Resolution, resolved.Value.ResolutionReason));
        Assert.Equal(ProposalStatuses.Withdrawn, resolved.Value.Proposals.Single().Status);
        Assert.Equal(400, Api.Controllers.SchedulingControllerBase.StatusFor(DispatchErrorCodes.ReasonRequired));

        // A new request can be created for the same survey request afterwards (only one ACTIVE request at a time).
        var again = await CreateAsync(ctx, w.Request);
        Assert.True(again.Succeeded, again.Message);
        Assert.True(again.Value!.Created);
        var closed = await ctx.Dispatch().CloseAsync(new ResolveDispatchCommand(w.Manager, again.Value.DispatchRequestId, "Duplicate", null), None);
        Assert.Equal((DispatchStatuses.Closed, DispatchResolutions.ClosedByStaff), (closed.Value!.Request.Status, closed.Value.Resolution));
    }
}

/// <summary>Short alias of the eligibility codes used above.</summary>
internal static class EligibilityReasonsOf
{
    public const string OnLeave = SmartSolar.Modules.Scheduling.Domain.EligibilityReasons.OnLeave;
    public const string SchedulingDisabled = SmartSolar.Modules.Scheduling.Domain.EligibilityReasons.SchedulingDisabled;
    public const string ScheduleConflict = SmartSolar.Modules.Scheduling.Domain.EligibilityReasons.ScheduleConflict;
    public const string OutsideWorkingHours = SmartSolar.Modules.Scheduling.Domain.EligibilityReasons.OutsideWorkingHours;
}
