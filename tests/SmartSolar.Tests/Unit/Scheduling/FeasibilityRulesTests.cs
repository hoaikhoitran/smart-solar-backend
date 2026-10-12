using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Travel;
using static SmartSolar.Tests.Unit.Scheduling.SchedulingTestData;

namespace SmartSolar.Tests.Unit.Scheduling;

public sealed class FeasibilityRulesTests
{
    private static readonly TimeSpan Buffer = TimeSpan.FromMinutes(30);
    private static readonly DateTimeOffset Now = Local(2026, 10, 20, 6, 0);

    private static TechnicianDayState Day(params ScheduledStop[] stops)
        => new(Guid.NewGuid(), "Tech", Base, stops, []);

    private static ScheduledStop Stop(int startHour, int startMinute, GeoPoint location, int durationMinutes = 90)
    {
        var start = Local(2026, 10, 20, startHour, startMinute);
        return new ScheduledStop(Guid.NewGuid(), start, start.AddMinutes(durationMinutes), location);
    }

    private static AppointmentCandidate Candidate(int hour, int minute, GeoPoint location)
    {
        var start = Local(2026, 10, 20, hour, minute);
        return new AppointmentCandidate(start, start.AddMinutes(90), location);
    }

    [Fact]
    public void Spec_example_previous_appointment_plus_75_minutes_and_buffer_misses_10_30()
    {
        // Existing 08:00–09:30 in Thu Duc; customer wants 10:30 in Binh Tan; ETA 75 min; buffer 30.
        var day = Day(Stop(8, 0, ThuDuc));
        var candidate = Candidate(10, 30, BinhTan);
        var plan = FeasibilityRules.PlanLegs(day, candidate, Buffer, Now);

        var result = FeasibilityRules.Evaluate(day, candidate, plan, Travel(75), null, Buffer, Now);

        Assert.False(result.IsFeasible);
        Assert.Equal(FeasibilityReasons.CannotArriveInTime, result.Reason);
        Assert.Equal(Local(2026, 10, 20, 11, 15), result.EarliestArrivalUtc);
        Assert.Equal(Local(2026, 10, 20, 9, 30), plan.Inbound.DepartureAtUtc);
        Assert.Equal(ThuDuc, plan.Inbound.Origin);
    }

    [Fact]
    public void Exactly_meeting_previous_end_plus_travel_plus_buffer_is_feasible()
    {
        var day = Day(Stop(8, 0, ThuDuc));
        var candidate = Candidate(11, 30, BinhTan);
        var plan = FeasibilityRules.PlanLegs(day, candidate, Buffer, Now);

        Assert.True(FeasibilityRules.Evaluate(day, candidate, plan, Travel(90), null, Buffer, Now).IsFeasible);
        Assert.False(FeasibilityRules.Evaluate(day, candidate, plan, Travel(91), null, Buffer, Now).IsFeasible);
    }

    [Fact]
    public void The_next_appointment_must_still_be_reachable()
    {
        // New 08:00–09:30, existing next at 10:30: 09:30 + 31 + 30 = 10:31 > 10:30.
        var day = Day(Stop(10, 30, ThuDuc));
        var candidate = Candidate(8, 0, BinhTan);
        var plan = FeasibilityRules.PlanLegs(day, candidate, Buffer, Now);

        Assert.NotNull(plan.Outbound);
        Assert.Equal(Local(2026, 10, 20, 9, 30), plan.Outbound!.DepartureAtUtc);
        Assert.Equal(FeasibilityReasons.LateForNextAppointment,
            FeasibilityRules.Evaluate(day, candidate, plan, Travel(20), Travel(31), Buffer, Now).Reason);
        Assert.True(FeasibilityRules.Evaluate(day, candidate, plan, Travel(20), Travel(30), Buffer, Now).IsFeasible);
    }

    [Fact]
    public void First_appointment_travels_from_the_base_and_cannot_depart_in_the_past()
    {
        var day = Day();
        var candidate = Candidate(8, 0, BinhTan);
        var plan = FeasibilityRules.PlanLegs(day, candidate, Buffer, Local(2026, 10, 20, 7, 0));

        Assert.Equal(Base, plan.Inbound.Origin);
        Assert.Equal("BASE", plan.Inbound.OriginKind);
        // Must leave 08:00 − 30 − 40 = 06:50, but it is already 07:00.
        Assert.Equal(FeasibilityReasons.DepartureInPast,
            FeasibilityRules.Evaluate(day, candidate, plan, Travel(40), null, Buffer, Local(2026, 10, 20, 7, 0)).Reason);
        Assert.True(FeasibilityRules.Evaluate(day, candidate, plan, Travel(30), null, Buffer, Local(2026, 10, 20, 7, 0)).IsFeasible);
    }

    [Fact]
    public void Missing_or_failed_route_data_is_never_treated_as_zero_travel()
    {
        var day = Day(Stop(10, 30, ThuDuc));
        var candidate = Candidate(8, 0, BinhTan);
        var plan = FeasibilityRules.PlanLegs(day, candidate, Buffer, Now);

        Assert.Equal(FeasibilityReasons.RouteNotVerified,
            FeasibilityRules.Evaluate(day, candidate, plan, Failed(RouteCalculationStatuses.Timeout), Travel(1), Buffer, Now).Reason);
        Assert.Equal(FeasibilityReasons.RouteNotVerified,
            FeasibilityRules.Evaluate(day, candidate, plan, Travel(1), Failed(RouteCalculationStatuses.RouteNotFound), Buffer, Now).Reason);
        Assert.Equal(FeasibilityReasons.RouteNotVerified,
            FeasibilityRules.Evaluate(day, candidate, plan, Travel(1), null, Buffer, Now).Reason);
    }

    [Fact]
    public void Daily_limit_is_an_upper_bound()
    {
        var day = Day(Stop(8, 0, ThuDuc), Stop(10, 30, ThuDuc), Stop(13, 0, ThuDuc));

        Assert.Equal(FeasibilityReasons.DailyLimitReached, FeasibilityRules.PreCheck(day, Candidate(15, 30, ThuDuc), maxPerDay: 3));
        Assert.Null(FeasibilityRules.PreCheck(day, Candidate(15, 30, ThuDuc), maxPerDay: 4));
    }

    [Fact]
    public void Overlap_is_rejected_but_touching_is_not()
    {
        var day = Day(Stop(8, 0, ThuDuc));

        Assert.Equal(FeasibilityReasons.Overlap, FeasibilityRules.PreCheck(day, Candidate(9, 0, ThuDuc), 3));
        Assert.Null(FeasibilityRules.PreCheck(day, Candidate(9, 30, ThuDuc), 3));
    }

    [Fact]
    public void Unavailable_periods_block_the_appointment_and_its_travel()
    {
        var blocked = new BlockedPeriod(Local(2026, 10, 20, 7, 0), Local(2026, 10, 20, 8, 15));
        var day = new TechnicianDayState(Guid.NewGuid(), "Tech", Base, [], [blocked]);

        Assert.Equal(FeasibilityReasons.Unavailable, FeasibilityRules.PreCheck(day, Candidate(8, 0, BinhTan), 3));

        // 09:00 appointment needs departure at 09:00 − 30 − 30 = 08:00, inside the block ending 08:15.
        var candidate = Candidate(9, 0, BinhTan);
        Assert.Null(FeasibilityRules.PreCheck(day, candidate, 3));
        var plan = FeasibilityRules.PlanLegs(day, candidate, Buffer, Now);
        Assert.Equal(FeasibilityReasons.Unavailable, FeasibilityRules.Evaluate(day, candidate, plan, Travel(30), null, Buffer, Now).Reason);
        Assert.True(FeasibilityRules.Evaluate(day, candidate, plan, Travel(10), null, Buffer, Now).IsFeasible);
    }

    [Fact]
    public void Missing_base_location_excludes_the_technician()
    {
        var day = new TechnicianDayState(Guid.NewGuid(), "Tech", null, [], []);

        Assert.Equal(FeasibilityReasons.BaseLocationMissing, FeasibilityRules.PreCheck(day, Candidate(8, 0, BinhTan), 3));
    }

    [Fact]
    public void The_appointment_being_moved_is_ignored()
    {
        var existing = Stop(8, 0, ThuDuc);
        var day = Day(existing);
        var moved = Candidate(8, 30, ThuDuc) with { IgnoreAppointmentId = existing.AppointmentId };

        Assert.Null(FeasibilityRules.PreCheck(day, moved, 1));
        Assert.Null(FeasibilityRules.PlanLegs(day, moved, Buffer, Now).Previous);
    }

    [Fact]
    public void Previous_appointment_already_finished_departs_now()
    {
        var day = Day(Stop(8, 0, ThuDuc));
        var now = Local(2026, 10, 20, 10, 0);
        var plan = FeasibilityRules.PlanLegs(day, Candidate(13, 0, BinhTan), Buffer, now);

        Assert.Equal(now, plan.Inbound.DepartureAtUtc);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(91, 106, false)]
    [InlineData(10.8, 181, false)]
    [InlineData(10.8, 106.7, true)]
    public void Coordinates_are_validated_and_never_substituted(double lat, double lon, bool valid)
        => Assert.Equal(valid, GeoPoint.From((decimal)lat, (decimal)lon) is not null);
}
