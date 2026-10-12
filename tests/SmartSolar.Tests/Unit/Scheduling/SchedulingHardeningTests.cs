using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Constants;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Services;
using SmartSolar.Modules.Scheduling.Travel;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Per-technician schedules, earliest base departure, durable route recalculation, Sales
/// reschedule/reassignment, leave conflicts, dashboard snapshots and capacity semantics.
/// All travel times are SYNTHETIC (FakeTravelTimeEstimator); no provider is called.
/// </summary>
public sealed class SchedulingHardeningTests
{
    private static readonly DateOnly Saturday = new(2026, 10, 24);

    private static async Task<bool> BookAsync(SchedulingTestContext ctx, SeededSurveyRequest r, DateTimeOffset start)
    {
        var result = await ctx.Book().HandleAsync(new BookSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, start, null), CancellationToken.None);
        if (!result.Succeeded) Console.WriteLine($"BOOK {start:HH:mm} FAILED: {result.ErrorCode} {result.Message}");
        return result.Succeeded;
    }

    private static Task<TechnicianScheduleView> ScheduleAsync(SchedulingTestContext ctx, Guid tech, DateOnly? date = null)
        => ctx.Schedule().HandleAsync(tech, date ?? Day, CancellationToken.None);

    private static async Task SetWeekAsync(SchedulingTestContext ctx, Guid tech, params WorkingPeriodInput[] periods)
    {
        var result = await ctx.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(tech, false, periods), CancellationToken.None);
        Assert.True(result.Succeeded, result.Message);
    }

    private static IEnumerable<WorkingPeriodInput> Days(DayOfWeek from, DayOfWeek to, params (int StartHour, int EndHour)[] periods)
    {
        for (var d = (int)from; d <= (int)to; d++)
            foreach (var (s, e) in periods)
                yield return new WorkingPeriodInput((DayOfWeek)d, new TimeOnly(s, 0), new TimeOnly(e, 0));
    }

    // ------------------------------------------------------------ working schedules

    [Fact]
    public async Task Individual_weekly_schedules_drive_slots_and_reservations()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var techA = await ctx.SeedTechnicianAsync("Tech A", District1);
        var techB = await ctx.SeedTechnicianAsync("Tech B", District1);
        // A: Monday–Friday 08–12, 13–17, Saturday off. B: Monday–Saturday 08–12, 13–16.
        await SetWeekAsync(ctx, techA.Id, Days(DayOfWeek.Monday, DayOfWeek.Friday, (8, 12), (13, 17)).ToArray());
        await SetWeekAsync(ctx, techB.Id, Days(DayOfWeek.Monday, DayOfWeek.Saturday, (8, 12), (13, 16)).ToArray());
        var request = await ctx.SeedSurveyRequestAsync(District7);

        var saturday = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Saturday), CancellationToken.None)).Value!;
        var tuesday = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None)).Value!;

        // Saturday: only B, and the afternoon ends at 16:00 (last start 14:30).
        Assert.Contains(saturday.Slots, s => s.StartsAt == At(14, 30, Saturday));
        Assert.DoesNotContain(saturday.Slots, s => s.StartsAt == At(15, 0, Saturday));
        Assert.All(saturday.Slots, s => Assert.Equal(1, s.KnownAvailableTechnicians));
        // Tuesday: both technicians until 14:30, only A from 15:00 to 15:30.
        Assert.Equal(2, tuesday.Slots.Single(s => s.StartsAt == At(14, 30)).KnownAvailableTechnicians);
        Assert.Equal(1, tuesday.Slots.Single(s => s.StartsAt == At(15, 30)).KnownAvailableTechnicians);

        Assert.True(await BookAsync(ctx, request, At(9, 0, Saturday)));
        Assert.Equal(techB.Id, (await ctx.ActiveAppointmentAsync(request.Request.Id)).ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Default_working_days_are_configurable_and_times_are_stored_in_utc()
    {
        await using var ctx = Create();
        ctx.Options.WorkingDays = "MON,TUE,WED,THU,FRI";
        ctx.Travel.Minutes = _ => 20;
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        var calendar = new WorkingCalendar(ctx.Options);
        var slots = new GetSurveySlotsHandler(ctx.UnitOfWork, new TechnicianAvailabilityEvaluator(ctx.Options, ctx.Clock), ctx.Travel, calendar, ctx.Options, ctx.Clock);

        Assert.Empty((await slots.HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Saturday), CancellationToken.None)).Value!.Slots);
        var tuesday = (await slots.HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.Equal(TimeSpan.FromHours(7), tuesday.Slots[0].StartsAt.Offset);
        Assert.Equal(TimeSpan.Zero, tuesday.Slots[0].StartsAtUtc.Offset);
        Assert.Equal(tuesday.Slots[0].StartsAt.UtcDateTime, tuesday.Slots[0].StartsAtUtc.UtcDateTime);

        Assert.True(await BookAsync(ctx, request, At(9)));
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 2, 0, 0, TimeSpan.Zero), (await ctx.ActiveAppointmentAsync(request.Request.Id)).StartsAt);
    }

    [Fact]
    public async Task Overlapping_or_invalid_weekly_periods_are_rejected()
    {
        var validator = new SetWorkingScheduleCommandValidator();
        var overlap = new SetWorkingScheduleCommand(Guid.NewGuid(), false,
            [new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0)), new(DayOfWeek.Monday, new TimeOnly(11, 0), new TimeOnly(14, 0))]);
        var reversed = new SetWorkingScheduleCommand(Guid.NewGuid(), false, [new(DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(8, 0))]);
        var empty = new SetWorkingScheduleCommand(Guid.NewGuid(), false, []);
        var sameTimeOtherDays = new SetWorkingScheduleCommand(Guid.NewGuid(), false,
            [new(DayOfWeek.Monday, new TimeOnly(8, 0), new TimeOnly(12, 0)), new(DayOfWeek.Tuesday, new TimeOnly(8, 0), new TimeOnly(12, 0))]);

        Assert.False((await validator.ValidateAsync(overlap)).IsValid);
        Assert.False((await validator.ValidateAsync(reversed)).IsValid);
        Assert.False((await validator.ValidateAsync(empty)).IsValid);
        Assert.True((await validator.ValidateAsync(sameTimeOtherDays)).IsValid);
    }

    [Fact]
    public async Task Schedule_change_that_would_strand_an_existing_booking_is_refused()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        Assert.True(await BookAsync(ctx, request, At(15)));   // Tuesday 15:00–16:30

        var conflict = await ctx.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(tech.Id, false,
            Days(DayOfWeek.Monday, DayOfWeek.Friday, (8, 12), (13, 16)).ToList()), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, conflict.ErrorCode);
        Assert.False((await ctx.Db.TechnicianProfiles.AsNoTracking().SingleAsync(p => p.UserId == tech.Id)).HasCustomWorkingSchedule);
        Assert.Empty(await ctx.Db.TechnicianWorkingPeriods.AsNoTracking().ToListAsync());

        // A week that still contains the booking is accepted.
        Assert.True((await ctx.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(tech.Id, false,
            Days(DayOfWeek.Monday, DayOfWeek.Friday, (8, 12), (13, 17)).ToList()), CancellationToken.None)).Succeeded);
    }

    // ------------------------------------------------------------ earliest base departure

    [Fact]
    public void Departure_before_the_earliest_allowed_time_is_infeasible()
    {
        var day = new TechnicianDayState(Guid.NewGuid(), "T", District1, [], [], EarliestBaseDepartureUtc: At(7).ToUniversalTime());
        var candidate = new AppointmentCandidate(At(8).ToUniversalTime(), At(9, 30).ToUniversalTime(), BinhTan);
        var buffer = TimeSpan.FromMinutes(30);
        var now = DefaultNow.ToUniversalTime().AddHours(-12);
        var plan = FeasibilityRules.PlanLegs(day, candidate, buffer, now);

        // 90 min travel + 30 min buffer: must leave at 06:00, before 07:00.
        var tooEarly = FeasibilityRules.Evaluate(day, candidate, plan, SchedulingTestData.Travel(90), null, buffer, now);
        // 30 min travel: leaves at 07:00 exactly, which is allowed.
        var onTime = FeasibilityRules.Evaluate(day, candidate, plan, SchedulingTestData.Travel(30), null, buffer, now);
        // An explicit 06:00 policy allows the 90-minute trip.
        var early = FeasibilityRules.Evaluate(day with { EarliestBaseDepartureUtc = At(6).ToUniversalTime() }, candidate, plan,
            SchedulingTestData.Travel(90), null, buffer, now);

        Assert.Equal(FeasibilityReasons.BeforeEarliestDeparture, tooEarly.Reason);
        Assert.True(onTime.IsFeasible);
        Assert.True(early.IsFeasible);
    }

    [Fact]
    public async Task Technician_specific_earliest_departure_overrides_the_default()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 90;
        var tech = await ctx.SeedTechnicianAsync("Far Tech", District1);
        var request = await ctx.SeedSurveyRequestAsync(ThuDuc);

        Assert.False(await BookAsync(ctx, request, At(8)));   // would need 06:00 with the default 07:00 limit

        Assert.True((await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech.Id, District1.Latitude, District1.Longitude, null, true, null,
            new TimeOnly(6, 0)), CancellationToken.None)).Succeeded);
        Assert.True(await BookAsync(ctx, request, At(8)));
        var schedule = await ScheduleAsync(ctx, tech.Id);
        Assert.Equal(At(6), schedule.EarliestBaseDeparture);
        Assert.Equal(0, schedule.ConflictCount);

        // Raising the limit again would make the booked first trip impossible: refused.
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments,
            (await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech.Id, District1.Latitude, District1.Longitude, null, true, null,
                new TimeOnly(7, 0)), CancellationToken.None)).ErrorCode);
    }

    // ------------------------------------------------------------ cancellation → recalculation

    /// <summary>A → B → C on one technician-day (Thu Duc, Binh Tan, District 7).</summary>
    private static async Task<(SchedulingTestContext Ctx, Guid Tech, SeededSurveyRequest A, SeededSurveyRequest B, SeededSurveyRequest C)> ChainAsync()
    {
        var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var a = await ctx.SeedSurveyRequestAsync(ThuDuc, "A");
        var b = await ctx.SeedSurveyRequestAsync(BinhTan, "B");
        var c = await ctx.SeedSurveyRequestAsync(District7, "C");
        Assert.True(await BookAsync(ctx, a, At(8)));
        Assert.True(await BookAsync(ctx, b, At(10, 30)));
        Assert.True(await BookAsync(ctx, c, At(13)));
        return (ctx, tech.Id, a, b, c);
    }

    private static Task<bool> CancelAsync(SchedulingTestContext ctx, SeededSurveyRequest r)
        => ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(r.Customer.Id, r.Request.Id, null), CancellationToken.None).ContinueWith(t => t.Result.Succeeded);

    [Fact]
    public async Task Cancelling_the_middle_appointment_recalculates_A_to_C_and_supersedes_B_to_C()
    {
        var (ctx, tech, a, b, c) = await ChainAsync();
        await using var _ = ctx;
        var aId = (await ctx.ActiveAppointmentAsync(a.Request.Id)).Id;
        var cId = (await ctx.ActiveAppointmentAsync(c.Request.Id)).Id;
        var before = await ScheduleAsync(ctx, tech);
        Assert.Equal((await ctx.ActiveAppointmentAsync(b.Request.Id)).Id, before.Appointments[2].InboundTravel!.OriginAppointmentId);

        ctx.Travel.Minutes = r => r.Origin == ThuDuc && r.Destination == District7 ? 35 : 20;
        Assert.True(await CancelAsync(ctx, b));

        var after = await ScheduleAsync(ctx, tech);
        Assert.Equal(2, after.AppointmentCount);
        var leg = after.Appointments[1].InboundTravel!;
        Assert.Equal("OK", after.Appointments[1].RoutingStatus);
        Assert.Equal(aId, leg.OriginAppointmentId);
        Assert.Equal(ThuDuc.Latitude, leg.OriginLatitude);
        Assert.Equal(35 * 60, leg.DurationSeconds);
        Assert.Equal(At(9, 30), leg.DepartureAt);                       // A ends 09:30
        Assert.Equal(At(10, 5), leg.EstimatedArrivalAt);
        Assert.Equal(30, leg.SafetyBufferMinutes);
        Assert.Null(after.Recalculation);

        // The old B → C snapshot is kept as history but superseded; it never counts as current.
        var cEstimates = await ctx.Db.TechnicianTravelEstimates.AsNoTracking().Where(e => e.AppointmentId == cId).ToListAsync();
        Assert.Single(cEstimates, e => e.SupersededAt is null);
        Assert.Contains(cEstimates, e => e.SupersededAt is not null && e.OriginAppointmentId != aId);
        // Totals: exactly two current legs (base → A, A → C), nothing double-counted.
        Assert.Equal(after.Appointments.Sum(x => x.InboundTravel!.DurationSeconds), after.EstimatedTravelSeconds);
        Assert.Equal(20 * 60 + 35 * 60, after.EstimatedTravelSeconds);
        Assert.Equal(2 * 30, after.SafetyBufferMinutesTotal);
    }

    [Fact]
    public async Task Cancelling_the_first_appointment_makes_the_next_leg_start_at_the_base()
    {
        var (ctx, tech, a, _, _) = await ChainAsync();
        await using var __ = ctx;

        Assert.True(await CancelAsync(ctx, a));

        var after = await ScheduleAsync(ctx, tech);
        Assert.Equal(TravelOriginKinds.Base, after.Appointments[0].InboundTravel!.OriginKind);
        Assert.Equal(District1.Latitude, after.Appointments[0].InboundTravel!.OriginLatitude);
        Assert.All(after.Appointments, x => Assert.Equal("OK", x.RoutingStatus));
    }

    [Fact]
    public async Task Cancelling_the_last_or_only_appointment_needs_no_new_route_and_clears_the_request()
    {
        var (ctx, tech, a, b, c) = await ChainAsync();
        await using var _ = ctx;
        var calls = ctx.Travel.Requests.Count;

        Assert.True(await CancelAsync(ctx, c));
        Assert.Equal(calls, ctx.Travel.Requests.Count);           // remaining legs are still valid
        Assert.All((await ScheduleAsync(ctx, tech)).Appointments, x => Assert.Equal("OK", x.RoutingStatus));

        Assert.True(await CancelAsync(ctx, b));
        Assert.True(await CancelAsync(ctx, a));
        var empty = await ScheduleAsync(ctx, tech);
        Assert.Equal(0, empty.AppointmentCount);
        Assert.Equal(0, empty.EstimatedTravelSeconds);
        Assert.Empty(await ctx.Db.TechnicianScheduleRecalculations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Cancellation_succeeds_during_a_provider_outage_and_is_recalculated_on_retry()
    {
        var (ctx, tech, _, b, c) = await ChainAsync();
        await using var _ = ctx;
        var cId = (await ctx.ActiveAppointmentAsync(c.Request.Id)).Id;

        ctx.Travel.FailWith = RouteCalculationStatuses.ProviderUnavailable;
        Assert.True(await CancelAsync(ctx, b));                    // the business change is never blocked by Vietmap

        var pending = await ctx.Db.TechnicianScheduleRecalculations.AsNoTracking().SingleAsync();
        Assert.Equal(RecalculationStatuses.Failed, pending.Status);
        Assert.Equal(1, pending.Attempts);
        Assert.True(pending.NextAttemptAt > ctx.Clock.Now);
        var during = await ScheduleAsync(ctx, tech);
        var leg = during.Appointments.Single(x => x.AppointmentId == cId);
        Assert.Equal("FAILED", leg.RoutingStatus);
        Assert.Null(leg.InboundTravel);                            // the invalidated B → C ETA is not shown as current
        Assert.Equal(RouteCalculationStatuses.ProviderUnavailable, leg.NonCurrentEstimate!.RouteCalculationStatus);
        Assert.Equal(RecalculationStatuses.Failed, during.Recalculation!.Status);
        Assert.Equal(1, during.RoutesNeedingReview);

        // Not due yet: nothing happens. After the backoff a fresh service instance (as after a restart) completes it.
        Assert.Equal(RecalculationOutcome.NotDue, await ctx.Recalculation().RunAsync(tech, Day, CancellationToken.None));
        ctx.Travel.FailWith = null;
        ctx.Clock.Now = pending.NextAttemptAt.AddSeconds(1);
        Assert.Equal(RecalculationOutcome.Completed, await ctx.Recalculation().RunAsync(tech, Day, CancellationToken.None));

        var recovered = await ScheduleAsync(ctx, tech);
        Assert.Equal("OK", recovered.Appointments.Single(x => x.AppointmentId == cId).RoutingStatus);
        Assert.Null(recovered.Recalculation);
        Assert.Empty(await ctx.Db.TechnicianScheduleRecalculations.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Missing_recalculation_is_reported_as_pending_not_as_a_current_eta()
    {
        var (ctx, tech, _, b, c) = await ChainAsync();
        await using var _ = ctx;
        var cId = (await ctx.ActiveAppointmentAsync(c.Request.Id)).Id;

        // Simulate a crash between the committed cancellation and the inline recalculation.
        ctx.Travel.BeforeAnswer = () => throw new InvalidOperationException("process stopped");
        Assert.True(await CancelAsync(ctx, b));
        ctx.Travel.BeforeAnswer = null;
        var view = await ScheduleAsync(ctx, tech);
        var leg = view.Appointments.Single(x => x.AppointmentId == cId);

        Assert.NotEqual("OK", leg.RoutingStatus);
        Assert.Null(leg.InboundTravel);
        Assert.NotNull(view.Recalculation);
    }

    [Fact]
    public async Task A_slot_blocked_by_the_daily_limit_becomes_bookable_after_a_cancellation()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        await ctx.SeedTechnicianAsync("Only Tech", District1, maxPerDay: 1);
        var first = await ctx.SeedSurveyRequestAsync(District7, "First");
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        Assert.True(await BookAsync(ctx, first, At(9)));

        var blocked = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(second.Customer.Id, second.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.Empty(blocked.Slots);

        Assert.True(await CancelAsync(ctx, first));
        var open = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(second.Customer.Id, second.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.Contains(open.Slots, s => s.StartsAt == At(9));
        Assert.True(await BookAsync(ctx, second, At(9)));
    }

    [Fact]
    public async Task Base_location_change_recalculates_first_legs_from_the_new_base()
    {
        var (ctx, tech, a, _, _) = await ChainAsync();
        await using var _ = ctx;
        var aId = (await ctx.ActiveAppointmentAsync(a.Request.Id)).Id;

        Assert.True((await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech, BinhTan.Latitude, BinhTan.Longitude, "New base", true, null),
            CancellationToken.None)).Succeeded);

        var leg = (await ScheduleAsync(ctx, tech)).Appointments.Single(x => x.AppointmentId == aId).InboundTravel!;
        Assert.Equal(BinhTan.Latitude, leg.OriginLatitude);
        Assert.Equal(TravelOriginKinds.Base, leg.OriginKind);
        Assert.Empty(await ctx.Db.TechnicianScheduleRecalculations.AsNoTracking().ToListAsync());
    }

    // ------------------------------------------------------------ unavailable periods / disabling

    [Fact]
    public async Task Leave_over_an_existing_booking_is_refused_with_the_affected_appointments()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        Assert.True(await BookAsync(ctx, request, At(10)));
        var appointmentId = (await ctx.ActiveAppointmentAsync(request.Request.Id)).Id;

        var refused = await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), tech.Id, At(9), At(12), "Sick"), CancellationToken.None);
        var allowed = await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), tech.Id, At(13), At(17), "Training"), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, refused.ErrorCode);
        var details = System.Text.Json.JsonSerializer.Serialize(refused.Details);
        Assert.Contains(appointmentId.ToString(), details);
        Assert.True(allowed.Succeeded);
        Assert.Single(await ctx.Db.TechnicianUnavailablePeriods.AsNoTracking().ToListAsync());
        Assert.Equal(AppointmentStatuses.Booked, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Status);   // never auto-cancelled
    }

    [Fact]
    public async Task Disabling_scheduling_with_upcoming_appointments_is_refused()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        Assert.True(await BookAsync(ctx, request, At(10)));

        var result = await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(tech.Id, District1.Latitude, District1.Longitude, null, false, null), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, result.ErrorCode);
        Assert.True((await ctx.Db.TechnicianProfiles.AsNoTracking().SingleAsync(p => p.UserId == tech.Id)).IsSchedulingActive);
    }

    // ------------------------------------------------------------ Sales reschedule / reassignment

    private static async Task<(SchedulingTestContext Ctx, Guid TechA, Guid TechB, SeededSurveyRequest Request, Guid Sales)> AssignedAsync(bool acknowledge)
    {
        var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var techA = await ctx.SeedTechnicianAsync("Tech A", District1);
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        Assert.True(await BookAsync(ctx, request, At(9)));
        var reserved = (await ctx.ActiveAppointmentAsync(request.Request.Id)).ReservedTechnicianUserId;
        var sales = await ctx.ClaimAsync(request);
        Assert.True((await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, reserved), CancellationToken.None)).Succeeded);
        if (acknowledge)
            Assert.True((await ctx.Jobs().AcknowledgeAsync(reserved, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Id, CancellationToken.None)).Succeeded);
        var other = reserved == techA.Id ? techB.Id : techA.Id;
        return (ctx, reserved, other, request, sales.Id);
    }

    [Fact]
    public async Task Sales_reschedules_an_acknowledged_appointment_which_then_needs_a_new_acknowledgment()
    {
        var (ctx, tech, _, request, sales) = await AssignedAsync(acknowledge: true);
        await using var _ = ctx;
        var original = await ctx.ActiveAppointmentAsync(request.Request.Id);

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(sales, request.Request.Id, At(13, 30), "Customer requested another time", null),
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var view = result.Value!;
        Assert.Equal(At(13, 30), view.StartsAt);
        Assert.Equal(original.Id, view.PreviousAppointmentId);
        Assert.Equal(AppointmentStatuses.Assigned, view.AppointmentStatus);
        Assert.True(view.AcknowledgementRequired);
        Assert.Equal(At(13, 30), view.ScheduledAt);

        var current = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(tech, current.AssignedTechnicianUserId);
        Assert.Null(current.AcknowledgedAt);
        var old = await ctx.Db.TechnicianAppointments.AsNoTracking().SingleAsync(a => a.Id == original.Id);
        Assert.Equal(AppointmentStatuses.Cancelled, old.Status);
        Assert.Equal(current.Id, old.ReplacedByAppointmentId);
        Assert.Equal(At(13, 30).ToUniversalTime(), (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id)).ScheduledAt);

        var events = await ctx.Db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.SurveyRequestId == request.Request.Id).Select(e => e.EventType).ToListAsync();
        Assert.Contains(AppointmentEventTypes.Rescheduled, events);
        Assert.Contains(AppointmentEventTypes.AcknowledgementInvalidated, events);
        Assert.All(await ctx.Db.SchedulingNotifications.AsNoTracking().ToListAsync(), n => Assert.Equal(SchedulingNotificationStatuses.NotDelivered, n.DeliveryStatus));

        // The technician acknowledges the new assignment.
        Assert.False((await ctx.Jobs().AcknowledgeAsync(tech, current.Id, CancellationToken.None)).Value!.AlreadyAcknowledged);
    }

    [Fact]
    public async Task Failed_sales_reschedule_keeps_the_original_appointment()
    {
        var (ctx, tech, _, request, sales) = await AssignedAsync(acknowledge: true);
        await using var _ = ctx;
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), tech, At(13), At(17), "Training"), CancellationToken.None)).Succeeded);
        var original = await ctx.ActiveAppointmentAsync(request.Request.Id);

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(sales, request.Request.Id, At(14), null, null), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TechnicianNotAvailable, result.ErrorCode);
        Assert.Contains("technicianUserId", result.Message);
        var unchanged = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(original.Id, unchanged.Id);
        Assert.Equal(AppointmentStatuses.Acknowledged, unchanged.Status);
        Assert.Equal(original.Revision, unchanged.Revision);
    }

    [Fact]
    public async Task Sales_can_reschedule_and_reassign_together()
    {
        var (ctx, techA, techB, request, sales) = await AssignedAsync(acknowledge: false);
        await using var _ = ctx;
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), techA, At(13), At(17), null), CancellationToken.None)).Succeeded);

        var result = await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(sales, request.Request.Id, At(14), "Tech A unavailable", techB), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Value!.Reassigned);
        var current = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(techB, current.ReservedTechnicianUserId);
        Assert.Equal(techB, current.AssignedTechnicianUserId);
    }

    [Fact]
    public async Task Reassigning_an_acknowledged_job_requires_the_new_technician_to_acknowledge()
    {
        var (ctx, techA, techB, request, sales) = await AssignedAsync(acknowledge: true);
        await using var _ = ctx;
        var appointment = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Single(await ctx.Jobs().ListAsync(techA, CancellationToken.None));

        var result = await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, techB, "Tech A is sick"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Value!.Reassigned);
        Assert.True(result.Value.AcknowledgementRequired);
        var stored = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(appointment.Id, stored.Id);
        Assert.Equal(AppointmentStatuses.Assigned, stored.Status);
        Assert.Null(stored.AcknowledgedAt);
        Assert.Equal(techB, stored.ReservedTechnicianUserId);
        Assert.Equal(techB, stored.AssignedTechnicianUserId);

        // The old technician no longer sees or can acknowledge the job; the new one must.
        Assert.Empty(await ctx.Jobs().ListAsync(techA, CancellationToken.None));
        Assert.Equal(SchedulingErrorCodes.AppointmentNotFound, (await ctx.Jobs().AcknowledgeAsync(techA, appointment.Id, CancellationToken.None)).ErrorCode);
        Assert.False((await ctx.Jobs().AcknowledgeAsync(techB, appointment.Id, CancellationToken.None)).Value!.AlreadyAcknowledged);

        var events = await ctx.Db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.AppointmentId == appointment.Id).OrderBy(e => e.CreatedAt).ToListAsync();
        Assert.Contains(events, e => e.EventType == AppointmentEventTypes.AcknowledgementInvalidated && e.PreviousTechnicianUserId == techA);
        Assert.Contains(events, e => e.EventType == AppointmentEventTypes.Reassigned && e.NewTechnicianUserId == techB && e.Reason == "Tech A is sick");
        var recipients = await ctx.Db.SchedulingNotifications.AsNoTracking().Select(n => n.RecipientUserId).Distinct().ToListAsync();
        Assert.Contains(techA, recipients);
        Assert.Contains(techB, recipients);

        // Both technicians' dashboards reflect the change.
        Assert.Equal(0, (await ScheduleAsync(ctx, techA)).AppointmentCount);
        var b = await ScheduleAsync(ctx, techB);
        Assert.Equal("OK", Assert.Single(b.Appointments).RoutingStatus);
        Assert.Equal(TravelOriginKinds.Base, b.Appointments[0].InboundTravel!.OriginKind);
    }

    [Fact]
    public async Task Inactive_or_unknown_replacements_are_rejected_and_the_assignment_is_kept()
    {
        var (ctx, techA, _, request, sales) = await AssignedAsync(acknowledge: true);
        await using var _ = ctx;
        var inactive = await ctx.SeedTechnicianAsync("Inactive", District7, active: false);

        Assert.Equal(SchedulingErrorCodes.TechnicianNotAvailable,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, inactive.Id), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.TechnicianNotFound,
            (await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales, request.Request.Id, Guid.NewGuid()), CancellationToken.None)).ErrorCode);
        var stored = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(techA, stored.AssignedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.Acknowledged, stored.Status);
    }

    [Fact]
    public async Task Sales_rules_ownership_and_past_appointments()
    {
        var (ctx, _, _, request, _) = await AssignedAsync(acknowledge: false);
        await using var _ = ctx;
        var stranger = await ctx.Inner.SeedSalesUserAsync();

        Assert.Equal(PreSurveyErrorCodes.SurveyRequestNotAssigned,
            (await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(stranger.Id, request.Request.Id, At(14), null, null), CancellationToken.None)).ErrorCode);

        ctx.Clock.Now = At(9, 15).ToUniversalTime();   // the appointment is under way
        var owner = (await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id)).AssignedSaleId!.Value;
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState,
            (await ctx.SalesReschedule().HandleAsync(new SalesRescheduleCommand(owner, request.Request.Id, At(14), null, null), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Customer_cannot_change_an_officially_assigned_appointment()
    {
        var (ctx, _, _, request, _) = await AssignedAsync(acknowledge: false);
        await using var _ = ctx;

        var result = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, result.ErrorCode);
    }

    // ------------------------------------------------------------ capacity semantics and congestion

    [Fact]
    public async Task Capacity_is_exact_only_when_every_candidate_was_verified()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        ctx.Options.MaxReportedSlotCapacity = 2;
        var t1 = await ctx.SeedTechnicianAsync("T1", District1);
        await ctx.SeedTechnicianAsync("T2", District1);
        await ctx.SeedTechnicianAsync("T3", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        var slotAt10 = async () => (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None))
            .Value!.Slots.Single(s => s.StartsAt == At(10));

        // Three can serve but the search stops at two: a lower bound, never claimed as exact.
        var capped = await slotAt10();
        Assert.Equal(2, capped.KnownAvailableTechnicians);
        Assert.Equal(CapacityStatuses.AtLeast, capped.CapacityStatus);

        // Two can serve, the third is fully excluded by a rule: exact.
        Assert.True((await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), t1.Id, At(7), At(18), null), CancellationToken.None)).Succeeded);
        var exact = await slotAt10();
        Assert.Equal(2, exact.KnownAvailableTechnicians);
        Assert.Equal(CapacityStatuses.Exact, exact.CapacityStatus);

        // One route cannot be verified: the verified technician is offered, but only as "at least".
        ctx.Options.MaxReportedSlotCapacity = 3;
        ctx.Travel.FailFor = r => r.Origin == BinhTan ? RouteCalculationStatuses.Timeout : null;
        var partial = await slotAt10();
        Assert.True(partial.CanBook);
        Assert.Equal(1, partial.KnownAvailableTechnicians);
        Assert.Equal(CapacityStatuses.AtLeast, partial.CapacityStatus);
    }

    [Fact]
    public async Task Missing_congestion_is_reported_as_not_returned_and_never_invented()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        Assert.True(await BookAsync(ctx, request, At(10)));

        var leg = Assert.Single((await ScheduleAsync(ctx, tech.Id)).Appointments).InboundTravel!;
        Assert.Equal("NOT_RETURNED_BY_PROVIDER", leg.Congestion.Status);
        Assert.Null(leg.Congestion.SegmentsByLevel);
        Assert.Null(leg.Congestion.HeavyOrWorseMeters);
        Assert.Null((await ctx.Db.TechnicianTravelEstimates.AsNoTracking().SingleAsync()).CongestionSummary);
    }

    [Fact]
    public async Task Provider_congestion_is_stored_and_shown_as_returned()
    {
        await using var ctx = Create();
        ctx.Travel.Minutes = _ => 20;
        ctx.Travel.Congestion = new CongestionSummary(new Dictionary<string, int> { ["low"] = 3, ["heavy"] = 1 }, 120.5);
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(District7);
        Assert.True(await BookAsync(ctx, request, At(10)));

        var leg = Assert.Single((await ScheduleAsync(ctx, tech.Id)).Appointments).InboundTravel!;
        Assert.Equal("AVAILABLE", leg.Congestion.Status);
        Assert.Equal(1, leg.Congestion.SegmentsByLevel!["heavy"]);
        Assert.Equal(120.5, leg.Congestion.HeavyOrWorseMeters);
    }
}
