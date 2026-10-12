using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Travel;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Customer slots, booking, rescheduling and cancellation. Travel times are SYNTHETIC.</summary>
public sealed class CustomerBookingTests
{
    private static BookSurveyAppointmentCommand Book(SeededSurveyRequest s, int hour, int minute = 0, string? note = null)
        => new(s.Customer.Id, s.Request.Id, At(hour, minute), note);

    [Fact]
    public async Task Slots_list_only_times_with_verified_technician_capacity()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var result = await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None);

        Assert.True(result.Succeeded);
        var view = result.Value!;
        Assert.Equal("Asia/Ho_Chi_Minh", view.TimeZone);
        Assert.Equal(90, view.AppointmentDurationMinutes);
        // 08:00 is gone since the earliest-base-departure rule: District 1 → Binh Tan (synthetic ~40 min)
        // + 30 min buffer means leaving at 06:50, before the default 07:00 limit.
        Assert.Equal(11, view.Slots.Count);
        Assert.Equal(At(8, 30), view.Slots[0].StartsAt);
        Assert.Equal(TimeSpan.FromHours(7), view.Slots[0].StartsAt.Offset);
        Assert.Equal(At(10), view.Slots[0].EndsAt);
        Assert.All(view.Slots, s =>
        {
            Assert.True(s.CanBook);
            Assert.Equal(1, s.KnownAvailableTechnicians);
            Assert.Equal(CapacityStatuses.Exact, s.CapacityStatus);   // the only technician was fully checked
        });
        Assert.Equal("VERIFIED", view.RoutingStatus);
    }

    [Fact]
    public async Task Slots_without_any_technician_are_empty_with_a_note()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var view = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None)).Value!;

        Assert.Empty(view.Slots);
        Assert.NotNull(view.Note);
        Assert.Empty(ctx.Travel.Requests);
    }

    [Fact]
    public async Task Slots_are_not_offered_when_travel_cannot_be_verified()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        ctx.Travel.FailWith = RouteCalculationStatuses.Timeout;

        var view = (await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day), CancellationToken.None)).Value!;

        Assert.Empty(view.Slots);
        Assert.Equal("UNAVAILABLE", view.RoutingStatus);
        Assert.Equal(12, view.UnverifiedSlotCount);
    }

    [Fact]
    public async Task Slots_respect_ownership_location_and_horizon()
    {
        await using var ctx = Create();
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var noGps = await ctx.SeedSurveyRequestAsync(null, "No GPS");

        var stranger = await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(noGps.Customer.Id, request.Site.Id, Day), CancellationToken.None);
        var missingGps = await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(noGps.Customer.Id, noGps.Site.Id, Day), CancellationToken.None);
        var tooFar = await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day.AddDays(60)), CancellationToken.None);
        var past = await ctx.Slots().HandleAsync(new GetSurveySlotsQuery(request.Customer.Id, request.Site.Id, Day.AddDays(-5)), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.PropertySiteNotFound, stranger.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.PropertySiteLocationRequired, missingGps.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, tooFar.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, past.ErrorCode);
    }

    [Fact]
    public async Task Booking_reserves_capacity_immediately_and_leaves_the_request_pending()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var result = await ctx.Book().HandleAsync(Book(request, 9, 0, "Gate code 1234"), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        var confirmation = result.Value!;
        Assert.True(confirmation.Created);
        Assert.Equal(AppointmentStatuses.Booked, confirmation.AppointmentStatus);
        Assert.Equal(At(9), confirmation.StartsAt);
        Assert.Equal(At(10, 30), confirmation.EndsAt);

        var stored = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(tech.Id, stored.ReservedTechnicianUserId);
        Assert.Null(stored.AssignedTechnicianUserId);
        Assert.Equal("Gate code 1234", stored.CustomerNote);
        Assert.Equal(At(9).ToUniversalTime(), stored.StartsAt);

        var surveyRequest = await ctx.Db.SurveyRequests.AsNoTracking().SingleAsync(r => r.Id == request.Request.Id);
        Assert.Equal(SurveyRequestStatus.Pending, surveyRequest.Status);
        Assert.Null(surveyRequest.ScheduledAt);

        var estimate = await ctx.Db.TechnicianTravelEstimates.AsNoTracking().SingleAsync();
        Assert.Equal(stored.Id, estimate.AppointmentId);
        Assert.Equal(TravelOriginKinds.Base, estimate.OriginKind);
        Assert.Equal("VIETMAP", estimate.Provider);
        Assert.Equal("v4", estimate.ProviderVersion);
        Assert.Equal("motorcycle", estimate.Vehicle);
        Assert.Equal(30, estimate.SafetyBufferMinutes);
        Assert.Equal(At(9).ToUniversalTime().AddMinutes(-30), estimate.EstimatedArrivalAt);
    }

    [Fact]
    public async Task Retrying_the_same_booking_is_idempotent_and_a_different_time_is_rejected()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var first = await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None);
        var retry = await ctx.Book().HandleAsync(Book(request, 9) with { StartsAt = At(9).ToUniversalTime() }, CancellationToken.None);
        var other = await ctx.Book().HandleAsync(Book(request, 13), CancellationToken.None);

        Assert.True(first.Value!.Created);
        Assert.False(retry.Value!.Created);
        Assert.Equal(first.Value.AppointmentId, retry.Value.AppointmentId);
        Assert.Equal(SchedulingErrorCodes.BookingAlreadyExists, other.ErrorCode);
        Assert.Single(await ctx.Db.TechnicianAppointments.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(8, 15)]   // off grid
    [InlineData(11, 0)]   // crosses lunch
    [InlineData(16, 0)]   // past end of day
    [InlineData(7, 0)]    // before work
    public async Task Invalid_start_times_are_rejected(int hour, int minute)
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        var result = await ctx.Book().HandleAsync(Book(request, hour, minute), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, result.ErrorCode);
    }

    [Fact]
    public async Task Past_start_time_is_rejected()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        ctx.Clock.Now = At(9, 10).ToUniversalTime();

        Assert.Equal(SchedulingErrorCodes.InvalidBookingTime, (await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None)).ErrorCode);
    }

    [Theory]
    [InlineData(RouteCalculationStatuses.QuotaExceeded, SchedulingErrorCodes.TravelProviderQuotaExceeded)]
    [InlineData(RouteCalculationStatuses.Timeout, SchedulingErrorCodes.TravelProviderUnavailable)]
    [InlineData(RouteCalculationStatuses.ProviderDisabled, SchedulingErrorCodes.TravelProviderUnavailable)]
    [InlineData(RouteCalculationStatuses.RouteNotFound, SchedulingErrorCodes.TravelRouteUnavailable)]
    public async Task Unverified_travel_never_confirms_an_appointment(string providerStatus, string expectedCode)
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        ctx.Travel.FailWith = providerStatus;

        var result = await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None);

        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Empty(await ctx.Db.TechnicianAppointments.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Spec_example_technician_A_cannot_reach_binh_tan_so_technician_B_is_reserved()
    {
        await using var ctx = Create();
        var techA = await ctx.SeedTechnicianAsync("Tech A", ThuDuc);
        var techB = await ctx.SeedTechnicianAsync("Tech B", District7);
        // A has 08:00–09:30 in Thu Duc. Thu Duc → Binh Tan takes 75 minutes (synthetic).
        var existing = await ctx.SeedSurveyRequestAsync(ThuDuc, "Existing");
        ctx.Travel.Minutes = r => r.Destination == ThuDuc && r.Origin == ThuDuc ? 5 : null;
        Assert.True((await ctx.Book().HandleAsync(Book(existing, 8), CancellationToken.None)).Succeeded);
        Assert.Equal(techA.Id, (await ctx.ActiveAppointmentAsync(existing.Request.Id)).ReservedTechnicianUserId);

        ctx.Travel.Minutes = r => r.Origin == ThuDuc && r.Destination == BinhTan ? 75 : null;
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var result = await ctx.Book().HandleAsync(Book(request, 10, 30), CancellationToken.None);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(techB.Id, (await ctx.ActiveAppointmentAsync(request.Request.Id)).ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Only_technician_busy_or_late_means_the_slot_is_unavailable()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", ThuDuc);
        var first = await ctx.SeedSurveyRequestAsync(ThuDuc, "First");
        Assert.True((await ctx.Book().HandleAsync(Book(first, 8), CancellationToken.None)).Succeeded);

        ctx.Travel.Minutes = r => r.Origin == ThuDuc && r.Destination == BinhTan || r.Origin == BinhTan && r.Destination == ThuDuc ? 75 : null;
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        var overlapping = await ctx.Book().HandleAsync(Book(second, 9), CancellationToken.None);
        var tooSoon = await ctx.Book().HandleAsync(Book(second, 10, 30), CancellationToken.None);
        var reachable = await ctx.Book().HandleAsync(Book(second, 13), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, overlapping.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, tooSoon.ErrorCode);
        Assert.True(reachable.Succeeded, reachable.Message);
    }

    [Fact]
    public async Task A_new_booking_may_not_make_the_technician_late_for_a_later_job()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", ThuDuc);
        var later = await ctx.SeedSurveyRequestAsync(ThuDuc, "Later");
        Assert.True((await ctx.Book().HandleAsync(Book(later, 10, 30), CancellationToken.None)).Succeeded);

        // New 08:30–10:00 in Binh Tan, then Binh Tan → Thu Duc 75 min: 10:00 + 75 + 30 = 11:45 > 10:30.
        ctx.Travel.Minutes = r => r.Origin == BinhTan && r.Destination == ThuDuc ? 75 : null;
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, (await ctx.Book().HandleAsync(Book(request, 8, 30), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Daily_limit_and_unavailable_periods_are_respected()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1, maxPerDay: 1);
        var first = await ctx.SeedSurveyRequestAsync(District1, "First");
        Assert.True((await ctx.Book().HandleAsync(Book(first, 8), CancellationToken.None)).Succeeded);

        var second = await ctx.SeedSurveyRequestAsync(District1, "Second");
        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, (await ctx.Book().HandleAsync(Book(second, 14), CancellationToken.None)).ErrorCode);

        await using var other = Create();
        var onLeave = await other.SeedTechnicianAsync("On leave", District1);
        Assert.True((await other.Admin().AddUnavailablePeriodAsync(
            new AddUnavailablePeriodCommand(Guid.NewGuid(), onLeave.Id, At(7), At(18), "Leave"), CancellationToken.None)).Succeeded);
        var request = await other.SeedSurveyRequestAsync(District1);
        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, (await other.Book().HandleAsync(Book(request, 9), CancellationToken.None)).ErrorCode);
        _ = tech;
    }

    [Fact]
    public async Task Technicians_without_role_profile_base_or_active_status_are_never_used()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("No role", District1, withRole: false);
        await ctx.SeedTechnicianAsync("Inactive profile", District1, active: false);
        await ctx.SeedTechnicianAsync("No base", null, active: true);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);

        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, (await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None)).ErrorCode);
        Assert.Empty(ctx.Travel.Requests);
    }

    [Fact]
    public async Task Booking_requires_ownership_location_and_an_open_request()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var noGps = await ctx.SeedSurveyRequestAsync(null, "No GPS");

        var stranger = await ctx.Book().HandleAsync(Book(request, 9) with { CustomerUserId = noGps.Customer.Id }, CancellationToken.None);
        var missingGps = await ctx.Book().HandleAsync(Book(noGps, 9), CancellationToken.None);
        await ctx.Db.SurveyRequests.Where(r => r.Id == request.Request.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, SurveyRequestStatus.Cancelled));
        var closed = await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.SurveyRequestNotFound, stranger.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.PropertySiteLocationRequired, missingGps.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, closed.ErrorCode);
    }

    [Fact]
    public async Task Rescheduling_replaces_the_appointment_and_keeps_history()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = (await ctx.Book().HandleAsync(Book(request, 9, 0, "first note"), CancellationToken.None)).Value!;

        var moved = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.NotEqual(original.AppointmentId, moved.Value!.AppointmentId);
        Assert.Equal(At(14), moved.Value.StartsAt);
        var all = await ctx.Db.TechnicianAppointments.AsNoTracking().OrderBy(a => a.BookedAt).ToListAsync();
        Assert.Equal(2, all.Count);
        var old = all.Single(a => a.Id == original.AppointmentId);
        Assert.Equal(AppointmentStatuses.Cancelled, old.Status);
        Assert.Equal("RESCHEDULED", old.CancellationReason);
        Assert.Equal(moved.Value.AppointmentId, old.ReplacedByAppointmentId);
        Assert.Equal("first note", all.Single(a => a.Id == moved.Value.AppointmentId).CustomerNote);

        var detail = (await ctx.CustomerQueries().GetAsync(request.Customer.Id, request.Request.Id, CancellationToken.None)).Value!;
        Assert.Equal(2, detail.History.Count);
        Assert.Equal(moved.Value.AppointmentId, detail.Request.Appointment!.AppointmentId);
    }

    [Fact]
    public async Task Rescheduling_to_an_unavailable_time_keeps_the_original_booking()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var original = (await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None)).Value!;
        ctx.Travel.FailWith = RouteCalculationStatuses.ProviderUnavailable;

        var moved = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.TravelProviderUnavailable, moved.ErrorCode);
        var active = await ctx.ActiveAppointmentAsync(request.Request.Id);
        Assert.Equal(original.AppointmentId, active.Id);
        Assert.Equal(AppointmentStatuses.Booked, active.Status);
    }

    [Fact]
    public async Task Cancellation_releases_the_capacity_for_others()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Only tech", District1, maxPerDay: 1);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        Assert.True((await ctx.Book().HandleAsync(Book(first, 9), CancellationToken.None)).Succeeded);
        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, (await ctx.Book().HandleAsync(Book(second, 9), CancellationToken.None)).ErrorCode);

        var cancelled = await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(first.Customer.Id, first.Request.Id, "Changed plans"), CancellationToken.None);

        Assert.Equal(AppointmentStatuses.Cancelled, cancelled.Value!.Status);
        Assert.True((await ctx.Book().HandleAsync(Book(second, 9), CancellationToken.None)).Succeeded);
        var history = await ctx.Db.TechnicianAppointments.AsNoTracking().Where(a => a.SurveyRequestId == first.Request.Id).SingleAsync();
        Assert.Equal("Changed plans", history.CancellationReason);
        // The first customer can book again.
        Assert.Equal(SchedulingErrorCodes.SlotUnavailable, (await ctx.Book().HandleAsync(Book(first, 13), CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Customer_changes_are_refused_after_sales_assignment()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        Assert.True((await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None)).Succeeded);
        var sales = await ctx.ClaimAsync(request);
        Assert.True((await ctx.Assign().HandleAsync(new AssignTechnicianCommand(sales.Id, request.Request.Id, tech.Id), CancellationToken.None)).Succeeded);

        var reschedule = await ctx.Reschedule().HandleAsync(new RescheduleSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, At(14), null), CancellationToken.None);
        var cancel = await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(request.Customer.Id, request.Request.Id, null), CancellationToken.None);

        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, reschedule.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.InvalidBookingState, cancel.ErrorCode);
        Assert.Equal(AppointmentStatuses.Assigned, (await ctx.ActiveAppointmentAsync(request.Request.Id)).Status);
    }

    [Fact]
    public async Task Customer_views_never_expose_the_internal_reservation()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech A", District1);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        await ctx.Book().HandleAsync(Book(request, 9), CancellationToken.None);

        var list = await ctx.CustomerQueries().ListAsync(request.Customer.Id, CancellationToken.None);

        var item = Assert.Single(list);
        Assert.Equal("PENDING", item.Status);
        Assert.False(item.Appointment!.TechnicianAssigned);
        Assert.Null(item.Appointment.AssignedTechnicianName);
        Assert.True(item.Appointment.CanCustomerChange);
        Assert.DoesNotContain(tech.Id.ToString(), System.Text.Json.JsonSerializer.Serialize(list));
        Assert.Empty(await ctx.CustomerQueries().ListAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
