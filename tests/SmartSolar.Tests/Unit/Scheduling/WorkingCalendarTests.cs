using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using static SmartSolar.Tests.Unit.Scheduling.SchedulingTestData;

namespace SmartSolar.Tests.Unit.Scheduling;

public sealed class WorkingCalendarTests
{
    /// <summary>The default booking limits (scheduling.bookingHorizonDays = 30, scheduling.minimumBookingLeadMinutes = 0).</summary>
    private static readonly BookingLimits Limits = new(new SchedulingOptions().BookingHorizonDays, new SchedulingOptions().MinimumBookingLeadMinutes);

    private static readonly DateOnly Day = new(2026, 10, 20);
    private static WorkingCalendar Calendar(Action<SchedulingOptions>? configure = null)
    {
        var options = new SchedulingOptions();
        configure?.Invoke(options);
        return new WorkingCalendar(options);
    }

    [Fact]
    public void Default_slots_start_every_30_minutes_and_fit_90_minutes_inside_each_period()
    {
        var starts = Calendar().CandidateWindows(Day).Select(w => w.LocalStart.ToString("HH:mm")).ToList();

        Assert.Equal(["08:00", "08:30", "09:00", "09:30", "10:00", "10:30", "13:00", "13:30", "14:00", "14:30", "15:00", "15:30"], starts);
    }

    [Fact]
    public void No_slot_crosses_lunch_or_ends_after_working_hours()
    {
        var windows = Calendar().CandidateWindows(Day);

        Assert.DoesNotContain(windows, w => w.LocalStart < new TimeOnly(12, 0) && w.LocalEnd > new TimeOnly(12, 0));
        Assert.All(windows, w => Assert.True(w.LocalEnd <= new TimeOnly(17, 0)));
        Assert.All(windows, w => Assert.Equal(TimeSpan.FromMinutes(90), w.EndsAtUtc - w.StartsAtUtc));
    }

    [Fact]
    public void Local_times_are_stored_as_utc()
    {
        var first = Calendar().CandidateWindows(Day)[0];

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero), first.StartsAtUtc);
        Assert.Equal(TimeSpan.Zero, first.StartsAtUtc.Offset);
    }

    [Fact]
    public void Configuration_changes_duration_interval_and_hours()
    {
        var starts = Calendar(o => { o.WorkingHours = "07:00-11:00"; o.AppointmentDurationMinutes = 120; o.SlotIntervalMinutes = 60; })
            .CandidateWindows(Day).Select(w => w.LocalStart.ToString("HH:mm")).ToList();

        Assert.Equal(["07:00", "08:00", "09:00"], starts);
    }

    [Fact]
    public void Past_start_times_are_never_bookable()
    {
        var now = Local(2026, 10, 20, 9, 10);

        var starts = Calendar().BookableWindows(Day, now, Limits).Select(w => w.LocalStart.ToString("HH:mm")).ToList();

        Assert.Equal("09:30", starts[0]);
        Assert.DoesNotContain("09:00", starts);
    }

    [Fact]
    public void Dates_outside_the_booking_horizon_have_no_slots()
    {
        var now = Local(2026, 10, 20, 7, 0);
        var calendar = Calendar();

        Assert.Empty(calendar.BookableWindows(Day.AddDays(-1), now, Limits));
        Assert.NotEmpty(calendar.BookableWindows(Day.AddDays(29), now, Limits));
        Assert.Empty(calendar.BookableWindows(Day.AddDays(30), now, Limits));
    }

    [Theory]
    [InlineData("2026-10-20T08:00:00+07:00", true)]
    [InlineData("2026-10-20T01:00:00Z", true)]          // same instant in UTC
    [InlineData("2026-10-20T08:15:00+07:00", false)]    // off the slot grid
    [InlineData("2026-10-20T11:00:00+07:00", false)]    // would cross lunch
    [InlineData("2026-10-20T16:00:00+07:00", false)]    // would end after 17:00
    [InlineData("2026-10-20T07:30:00+07:00", false)]    // before working hours
    [InlineData("2026-10-19T08:00:00+07:00", false)]    // past
    public void Requested_start_must_resolve_to_a_bookable_window(string start, bool expected)
    {
        var ok = Calendar().TryResolveBookable(DateTimeOffset.Parse(start), Local(2026, 10, 19, 12, 0), Limits, out var window, out var reason);

        Assert.Equal(expected, ok);
        Assert.Equal(expected, window is not null);
        Assert.Equal(expected, reason is null);
    }

    [Theory]
    [InlineData("08:00-12:00,11:00-17:00")]
    [InlineData("12:00-08:00")]
    [InlineData("8-12")]
    [InlineData("")]
    public void Invalid_working_hours_are_rejected(string hours)
        => Assert.Throws<FormatException>(() => WorkingCalendar.ParsePeriods(hours));
}
