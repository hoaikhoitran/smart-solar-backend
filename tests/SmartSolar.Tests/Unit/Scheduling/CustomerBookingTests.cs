using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Customer slots, booking (capacity hold), rescheduling and cancellation. Geography is SYNTHETIC.</summary>
public sealed class CustomerBookingTests
{
    private static BookSurveyAppointmentCommand Book(SeededSurveyRequest s, int hour, int minute = 0, string? note = null)
        => new(s.Customer.Id, s.Request.Id, At(hour, minute), note);

    [Fact]
    public async Task Slots_list_only_times_a_technician_of_the_zone_can_hold()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Binh Tan tech", BinhTan);
        await ctx.SeedTechnicianAsync("District 1 tech", District1);   // other zone: never counted
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var result = await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var view = result.Value!;
        Assert.Equal("Asia/Ho_Chi_Minh", view.TimeZone);
        Assert.Equal("Z-BINH-TAN", view.Zone!.Code);
        Assert.Equal(12, view.Slots.Count);                          // 08:00–10:30 and 13:00–15:30 every 30 min
        Assert.Equal(At(8), view.Slots[0].StartsAt);
        Assert.All(view.Slots, s => Assert.Equal(1, s.KnownAvailableTechnicians));
    }

    [Fact]
    public async Task Slots_and_booking_need_a_site_commune_in_an_active_serviceable_zone()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var noCodes = await ctx.SeedSurveyRequestAsync(BinhTan, "No codes", withCodes: false);
        var uncovered = await ctx.SeedSurveyRequestAsync(ThuDuc, "Uncovered");   // Phường Thủ Đức: no zone maps it

        // Slots answer 200 with the coverage status; only uncovered areas offer a dispatch (a site without location must be fixed first).
        var noLocationSlots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(noCodes.Customer.Id, noCodes.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.Equal((SchedulingErrorCodes.SiteLocationRequired, false, 0), (noLocationSlots.CoverageStatus, noLocationSlots.DispatchAvailable, noLocationSlots.Slots.Count));
        var uncoveredSlots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(uncovered.Customer.Id, uncovered.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.Equal((SchedulingErrorCodes.NoZoneCoverage, true, 0), (uncoveredSlots.CoverageStatus, uncoveredSlots.DispatchAvailable, uncoveredSlots.Slots.Count));
        var noLocationBooking = await ctx.Book().HandleAsync(Book(noCodes, 9), CancellationToken.None);
        Assert.Equal(SchedulingErrorCodes.SiteLocationRequired, noLocationBooking.ErrorCode);
        Assert.Equal(new CapacityFailures.CoverageDetails(SchedulingErrorCodes.SiteLocationRequired, false), noLocationBooking.Details);
        var uncoveredBooking = await ctx.Book().HandleAsync(Book(uncovered, 9), CancellationToken.None);
        Assert.Equal(SchedulingErrorCodes.NoZoneCoverage, uncoveredBooking.ErrorCode);
        Assert.Equal(new CapacityFailures.CoverageDetails(SchedulingErrorCodes.NoZoneCoverage, true), uncoveredBooking.Details);

        // An inactive zone covers nothing; an unserviceable zone is reported as such — temporarily uncovered, so dispatchable (409, not 422).
        await ctx.EnsureZoneAsync("Z-THU-DUC", active: false);
        Assert.Equal(SchedulingErrorCodes.NoZoneCoverage, (await ctx.Book().HandleAsync(Book(uncovered, 9), CancellationToken.None)).ErrorCode);
        await ctx.Db.OperationalZones.Where(z => z.Code == "Z-THU-DUC")
            .ExecuteUpdateAsync(x => x.SetProperty(z => z.IsActive, true).SetProperty(z => z.IsServiceable, false));
        var unserviceable = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(uncovered.Customer.Id, uncovered.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.Equal((SchedulingErrorCodes.ZoneNotServiceable, true), (unserviceable.CoverageStatus, unserviceable.DispatchAvailable));
        Assert.Equal(SchedulingErrorCodes.ZoneNotServiceable, (await ctx.Book().HandleAsync(Book(uncovered, 9), CancellationToken.None)).ErrorCode);
        // A commune closed by a newer dataset no longer counts as a location.
        await ctx.Db.AdministrativeCommunes.Where(c => c.Code == CommuneCodeOf(BinhTan))
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.ValidTo, new DateOnly(2026, 1, 1)));
        var closed = await ctx.SeedSurveyRequestAsync(BinhTan, "Closed commune");
        Assert.Equal(SchedulingErrorCodes.SiteLocationRequired, (await ctx.Book().HandleAsync(Book(closed, 9), CancellationToken.None)).ErrorCode);
        Assert.Equal(409, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.ZoneNotServiceable));
        Assert.Equal(409, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.NoZoneCoverage));
        Assert.Equal(422, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.SiteLocationRequired));
        Assert.Empty(await ctx.Db.TechnicianAppointments.ToListAsync());
    }

    [Fact]
    public async Task Slots_respect_ownership_and_horizon()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var other = await ctx.SeedSurveyRequestAsync(BinhTan, "Other");

        Assert.Equal(SchedulingErrorCodes.PropertySiteNotFound,
            (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(other.Customer.Id, request.Site.Id, Day), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime,
            (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day.AddDays(60)), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime,
            (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day.AddDays(-5)), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Booking_holds_one_technicians_capacity_and_waits_for_acceptance()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var result = await ctx.Book().HandleAsync(Book(request, 9, 0, "Gate code 1234"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Value!.Created);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, result.Value.AppointmentStatus);
        Assert.Equal(AcceptanceStatuses.WaitingForTechnician, result.Value.AcceptanceStatus);
        Assert.Equal(At(10, 30), result.Value.EndsAt);

        var stored = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(tech.Id, stored.ReservedTechnicianUserId);         // capacity holder
        Assert.Null(stored.AssignedTechnicianUserId);                     // nobody accepted
        Assert.Equal((await ctx.EnsureZoneAsync("Z-BINH-TAN")).Id, stored.OperationalZoneId);
        // LEAST(booked + 240 min, start − 120 min) = LEAST(10:00 on the 19th, 07:00 on the 20th).
        Assert.Equal(DefaultNow.AddMinutes(240).ToUniversalTime(), stored.AcceptanceDeadlineAt);
        Assert.Equal("Gate code 1234", stored.CustomerNote);

        var surveyRequest = await ctx.RequestAsync(request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Pending, surveyRequest.Status);
        Assert.Null(surveyRequest.ScheduledAt);

        var view = (await ctx.CustomerQueries().ListAsync(request.Customer.Id, CancellationToken.None)).Single().Appointment!;
        Assert.False(view.TechnicianAssigned);
        Assert.Null(view.AssignedTechnicianName);                         // the holder is never shown
        Assert.Equal(AcceptanceStatuses.WaitingForTechnician, view.AcceptanceStatus);
    }

    [Fact]
    public async Task The_deadline_is_capped_by_the_minimum_lead_before_the_start()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        ctx.Clock.Now = At(6).ToUniversalTime();                          // 06:00 on the day itself

        await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None);

        Assert.Equal(At(7).ToUniversalTime(), (await ctx.ActiveAppointmentAsync(request.Request.Id)).AcceptanceDeadlineAt);   // 09:00 − 120 min
    }

    [Fact]
    public async Task Retrying_the_same_booking_is_idempotent_and_a_different_time_is_rejected()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var first = await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None);
        var retry = await ctx.Book().HandleAsync(Book(request, 9) with { StartsAt = At(9).ToUniversalTime() }, CancellationToken.None);
        var other = await ctx.Book().HandleAsync(Book(request, 13), CancellationToken.None);

        Assert.True(first.Value!.Created);
        Assert.False(retry.Value!.Created);
        Assert.Equal(first.Value.AppointmentId, retry.Value.AppointmentId);
        Assert.Equal(SchedulingErrorCodes.BookingAlreadyExists, other.ErrorCode);
    }

    [Fact]
    public async Task Holders_are_spread_by_fewest_commitments_and_pending_holds_consume_capacity()
    {
        await using var ctx = Create();
        var a = await ctx.SeedTechnicianAsync("A", BinhTan);
        var b = await ctx.SeedTechnicianAsync("B", BinhTan);
        var r1 = await ctx.SeedSurveyRequestAsync(BinhTan, "One");
        var r2 = await ctx.SeedSurveyRequestAsync(BinhTan, "Two");
        var r3 = await ctx.SeedSurveyRequestAsync(BinhTan, "Three");

        var first = await ctx.BookAsync(r1, At(9));
        var second = await ctx.BookAsync(r2, At(13));
        Assert.Equal(new[] { a.Id, b.Id }.Order().First(), first.ReservedTechnicianUserId);
        Assert.NotEqual(first.ReservedTechnicianUserId, second.ReservedTechnicianUserId);   // the other one had fewer commitments

        // 09:30 is too close to the 09:00 hold of one technician but the other is free; then nobody is.
        var third = await ctx.BookAsync(r3, At(9, 30));
        Assert.Equal(second.ReservedTechnicianUserId, third.ReservedTechnicianUserId);
        var r4 = await ctx.SeedSurveyRequestAsync(BinhTan, "Four");
        Assert.Equal(SchedulingErrorCodes.ScheduleConflict, (await ctx.Book().HandleAsync(Book(r4, 9, 30), CancellationToken.None)).ErrorCode);
        var slots = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(r4.Customer.Id, r4.Site.Id, Day), CancellationToken.None)).Value!;
        Assert.DoesNotContain(slots.Slots, s => s.StartsAt == At(9, 30));
    }

    [Fact]
    public async Task Invalid_start_times_are_rejected()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, (await ctx.Book().HandleAsync(Book(request, 11), CancellationToken.None)).ErrorCode);   // crosses lunch
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, (await ctx.Book().HandleAsync(Book(request, 9, 10), CancellationToken.None)).ErrorCode); // off grid
        ctx.Clock.Now = At(12).ToUniversalTime();
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, (await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None)).ErrorCode);   // past
    }

    [Fact]
    public async Task Customer_reschedules_while_waiting_for_a_technician()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = await ctx.BookAsync(request, At(9));

        var result = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13), null), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, result.Value!.AppointmentStatus);
        var old = await ctx.AppointmentAsync(original.Id);
        Assert.Equal(AppointmentStatuses.Cancelled, old.Status);
        Assert.Equal(CancellationReasons.Rescheduled, old.CancellationReason);
        Assert.Equal(result.Value.AppointmentId, old.ReplacedByAppointmentId);
        Assert.Equal(At(13).ToUniversalTime(), (await ctx.ActiveAppointmentAsync(request.Request.Id)).StartsAt);
        Assert.Contains(await ctx.Db.TechnicianAppointmentEvents.Select(e => e.EventType).ToListAsync(), t => t == AppointmentEventTypes.Rescheduled);
    }

    [Fact]
    public async Task Rescheduling_an_accepted_appointment_keeps_the_technician_when_still_eligible()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, original.Id)).Succeeded);

        var result = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var moved = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(AppointmentStatuses.Accepted, moved.Status);
        Assert.Equal(AssignmentSources.SelfAccepted, moved.AssignmentSource);
        Assert.Equal(tech.Id, moved.AssignedTechnicianUserId);
        var surveyRequest = await ctx.RequestAsync(request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Scheduled, surveyRequest.Status);
        Assert.Equal(At(14).ToUniversalTime(), surveyRequest.ScheduledAt);
        Assert.Equal(AcceptanceStatuses.Confirmed, result.Value!.AcceptanceStatus);
    }

    [Fact]
    public async Task Rescheduling_an_accepted_appointment_needs_re_acceptance_when_the_technician_cannot_attend()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var other = await ctx.SeedTechnicianAsync("Other", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, original.Id)).Succeeded);
        var leave = await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), tech.Id, At(14), At(17), "Dentist"), CancellationToken.None);
        Assert.True(leave.Succeeded, leave.Message);

        var result = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var moved = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, moved.Status);
        Assert.Null(moved.AssignedTechnicianUserId);
        Assert.Equal(other.Id, moved.ReservedTechnicianUserId);
        var surveyRequest = await ctx.RequestAsync(request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Pending, surveyRequest.Status);
        Assert.Null(surveyRequest.ScheduledAt);
        Assert.Equal(AcceptanceStatuses.WaitingForTechnician, result.Value!.AcceptanceStatus);
    }

    [Fact]
    public async Task Rescheduling_to_an_unavailable_time_keeps_the_original()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var blocker = await ctx.SeedSurveyRequestAsync(BinhTan, "Blocker");
        var original = await ctx.BookAsync(request, At(9));
        await ctx.BookAsync(blocker, At(13));

        var result = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13, 30), null), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.ScheduleConflict, result.ErrorCode);
        Assert.Equal(new CapacityFailures.CoverageDetails(SchedulingErrorCodes.ScheduleConflict, true), result.Details);
        Assert.Equal(original.Id, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Id);
    }

    [Fact]
    public async Task Cancellation_releases_capacity_and_returns_a_confirmed_request_to_pending()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Only tech", BinhTan);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        var appointment = await ctx.BookAsync(first, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);
        Assert.Equal(SchedulingErrorCodes.ScheduleConflict, (await ctx.Book().HandleAsync(Book(second, 9), CancellationToken.None)).ErrorCode);

        var cancelled = await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(first.Customer.Id, first.Request.Id, "Changed plans"), CancellationToken.None);

        Assert.True(cancelled.Succeeded, cancelled.Message);
        Assert.Equal(AcceptanceStatuses.Cancelled, cancelled.Value!.AcceptanceStatus);
        Assert.Equal(SurveyRequestStatus.Pending, (await ctx.RequestAsync(first.Request.Id)).Status);
        Assert.True((await ctx.Book().HandleAsync(Book(second, 9), CancellationToken.None)).Succeeded);
        Assert.Contains(await ctx.Db.SchedulingNotifications.ToListAsync(), n => n.RecipientUserId == tech.Id && n.Type == AppointmentEventTypes.Cancelled);
    }

    [Fact]
    public async Task Customers_cannot_change_within_the_lead_time()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        await ctx.BookAsync(request, At(9));
        ctx.Clock.Now = At(7, 30).ToUniversalTime();                       // 90 min before, lead is 120

        Assert.Equal(SchedulingErrorCodes.InvalidBookingState,
            (await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, null), CancellationToken.None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState,
            (await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(13), null), CancellationToken.None)).ErrorCode);
        var view = (await ctx.CustomerQueries().ListAsync(request.Customer.Id, CancellationToken.None)).Single().Appointment!;
        Assert.False(view.CanCustomerChange);
        Assert.Equal(AcceptanceStatuses.NeedsStaff, view.AcceptanceStatus);   // the acceptance deadline passed
    }
}
