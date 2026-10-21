using SmartSolar.Modules.Scheduling.Domain;
using static SmartSolar.Tests.Unit.Scheduling.SchedulingTestData;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Pure eligibility rules (buffer, working hours, leave, daily limit, area, account state). No database.</summary>
public sealed class EligibilityRulesTests
{
    private static readonly TimeSpan Buffer = TimeSpan.FromMinutes(30);
    private static readonly Guid Area = Guid.NewGuid();
    private static readonly Guid OtherArea = Guid.NewGuid();

    private static TechnicianDayState Day(params ScheduledStop[] stops)
        => new(Guid.NewGuid(), "Tech", stops, [], ZoneIds: new HashSet<Guid> { Area },
            WorkingIntervals: [new WorkingInterval(Local(2026, 10, 20, 8), Local(2026, 10, 20, 12)), new WorkingInterval(Local(2026, 10, 20, 13), Local(2026, 10, 20, 17))]);

    private static ScheduledStop Stop(int hour, int minute = 0, int minutes = 90, bool counts = true, bool hold = false)
    {
        var start = Local(2026, 10, 20, hour, minute);
        return new ScheduledStop(Guid.NewGuid(), start, start.AddMinutes(minutes), counts, hold);
    }

    private static EligibilityRequest At(int hour, int minute = 0, Guid? area = null, IReadOnlySet<Guid>? ignore = null)
    {
        var start = Local(2026, 10, 20, hour, minute);
        return new EligibilityRequest(start, start.AddMinutes(90), [area ?? Area], ignore);
    }

    private static string? Check(TechnicianDayState day, EligibilityRequest request, int max = 3) => EligibilityRules.Check(day, request, max, Buffer);

    [Fact]
    public void A_free_technician_in_the_area_is_eligible()
        => Assert.Null(Check(Day(), At(8)));

    [Fact]
    public void Previous_end_plus_buffer_must_not_pass_the_start()
    {
        var day = Day(Stop(8));                                         // 08:00–09:30
        Assert.Equal(EligibilityReasons.ScheduleConflict, Check(day, At(9, 30)));   // touching is not enough: buffer
        Assert.Equal(EligibilityReasons.ScheduleConflict, Check(day, At(9, 59)));
        Assert.Null(Check(day, At(10)));                                 // 09:30 + 30 = 10:00
    }

    [Fact]
    public void End_plus_buffer_must_not_pass_the_next_start()
    {
        var day = Day(Stop(10, 30));                                     // next at 10:30
        Assert.Equal(EligibilityReasons.ScheduleConflict, Check(day, At(8, 31))); // ends 10:01 + 30 > 10:30
        Assert.Null(Check(day, At(8, 30)));                              // ends 10:00 + 30 = 10:30
    }

    [Fact]
    public void Installation_commitments_block_with_the_buffer_but_do_not_count_toward_the_limit()
    {
        var day = Day(Stop(8, minutes: 240, counts: false));             // installation 08:00–12:00
        Assert.Equal(EligibilityReasons.ScheduleConflict, Check(day, At(10, 30)));
        Assert.Null(Check(day, At(13), max: 1));
    }

    [Fact]
    public void The_daily_limit_counts_pending_holds_and_confirmed_jobs()
    {
        var day = Day(Stop(8), Stop(13, hold: true));
        Assert.Equal(EligibilityReasons.DailyLimitReached, Check(day, At(15), max: 2));
        Assert.Null(Check(day, At(15), max: 3));
    }

    [Fact]
    public void Ignored_commitments_free_their_interval()
    {
        var stop = Stop(8);
        var day = Day(stop);
        Assert.Null(Check(day, At(8, ignore: new HashSet<Guid> { stop.AppointmentId })));
    }

    [Fact]
    public void Working_hours_and_leave_are_respected()
    {
        Assert.Equal(EligibilityReasons.OutsideWorkingHours, Check(Day(), At(11)));        // 11:00–12:30 crosses the break
        var onLeave = Day() with { Blocked = [new BlockedPeriod(Local(2026, 10, 20, 9), Local(2026, 10, 20, 10))] };
        Assert.Equal(EligibilityReasons.OnLeave, Check(onLeave, At(8)));
    }

    [Fact]
    public void Area_membership_account_state_and_scheduling_flag_come_first()
    {
        Assert.Equal(EligibilityReasons.OutsideZone, Check(Day(), At(8, area: OtherArea)));
        Assert.Equal(EligibilityReasons.TechnicianInactive, Check(Day() with { IsActiveAccount = false }, At(8)));
        Assert.Equal(EligibilityReasons.TechnicianInactive, Check(Day() with { HasTechnicianRole = false }, At(8)));
        Assert.Equal(EligibilityReasons.SchedulingDisabled, Check(Day() with { IsSchedulingActive = false }, At(8)));
        // Inactive wins over everything else.
        Assert.Equal(EligibilityReasons.TechnicianInactive, Check(Day(Stop(8)) with { IsActiveAccount = false, IsSchedulingActive = false }, At(8, area: OtherArea)));
    }

    [Fact]
    public void Several_memberships_any_one_covers()
    {
        var day = Day() with { ZoneIds = new HashSet<Guid> { OtherArea, Area } };
        Assert.Null(Check(day, new EligibilityRequest(Local(2026, 10, 20, 8), Local(2026, 10, 20, 9, 30), [Guid.NewGuid(), Area])));
    }
}
