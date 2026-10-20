using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>Every coverage status of <see cref="ZoneCoverageService"/> (the evaluation reused by dispatch), over the real catalog.</summary>
public sealed class ZoneCoverageTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<CoverageEvaluation> At9(SchedulingTestContext ctx, SeededSurveyRequest r, int hour = 9)
        => ctx.Coverage().EvaluateAsync(r.Site.Id, At(hour).ToUniversalTime(), At(hour).AddMinutes(90).ToUniversalTime(), cancellationToken: None);

    [Fact]
    public async Task Site_location_zone_and_serviceability_are_decided_before_any_technician()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var noLocation = await ctx.SeedSurveyRequestAsync(BinhTan, "No location", withCodes: false);
        var unmapped = await ctx.SeedSurveyRequestAsync(ThuDuc, "Unmapped");
        var covered = await ctx.SeedSurveyRequestAsync(BinhTan, "Covered");

        Assert.Equal(CoverageStatuses.SiteLocationRequired, (await ctx.Coverage().EvaluateAsync(noLocation.Site.Id, cancellationToken: None)).Status);
        Assert.Equal(CoverageStatuses.SiteLocationRequired, (await ctx.Coverage().EvaluateAsync(Guid.NewGuid(), cancellationToken: None)).Status);
        Assert.Equal(CoverageStatuses.NoZoneCoverage, (await ctx.Coverage().EvaluateAsync(unmapped.Site.Id, cancellationToken: None)).Status);

        // A commune of another province (wrong pair) is not a valid location.
        await ctx.Db.PropertySites.Where(p => p.Id == noLocation.Site.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ProvinceCode, "01"));
        Assert.Equal(CoverageStatuses.SiteLocationRequired, (await ctx.Coverage().EvaluateAsync(noLocation.Site.Id, cancellationToken: None)).Status);

        var ok = await ctx.Coverage().EvaluateAsync(covered.Site.Id, cancellationToken: None);
        Assert.True(ok.IsCovered);
        Assert.Equal(("Z-BINH-TAN", BinhTanWard), (ok.Site.Zone!.Code, ok.Site.CommuneCode));
        Assert.Equal("Tech", Assert.Single(ok.Eligible).FullName);

        await ctx.Db.OperationalZones.Where(z => z.Code == "Z-BINH-TAN").ExecuteUpdateAsync(x => x.SetProperty(z => z.IsServiceable, false));
        var unserviceable = await ctx.Coverage().EvaluateAsync(covered.Site.Id, cancellationToken: None);
        Assert.Equal(CoverageStatuses.ZoneNotServiceable, unserviceable.Status);
        Assert.Empty(unserviceable.Eligible);
        await ctx.Db.OperationalZones.Where(z => z.Code == "Z-BINH-TAN").ExecuteUpdateAsync(x => x.SetProperty(z => z.IsServiceable, true).SetProperty(z => z.IsActive, false));
        Assert.Equal(CoverageStatuses.NoZoneCoverage, (await ctx.Coverage().EvaluateAsync(covered.Site.Id, cancellationToken: None)).Status);
    }

    [Fact]
    public async Task A_zone_without_a_schedulable_member_is_no_zone_technician()
    {
        await using var ctx = Create();
        await ctx.EnsureZoneAsync("Z-D7");
        var site = await ctx.SeedSurveyRequestAsync(District7);
        Assert.Equal(CoverageStatuses.NoZoneTechnician, (await ctx.Coverage().EvaluateAsync(site.Site.Id, cancellationToken: None)).Status);

        var disabled = await ctx.SeedTechnicianAsync("Disabled", District7, active: false);
        var evaluation = await At9(ctx, site);
        Assert.Equal(CoverageStatuses.NoZoneTechnician, evaluation.Status);
        var member = Assert.Single(evaluation.Ineligible);
        Assert.Equal((disabled.Id, false, EligibilityReasons.SchedulingDisabled), (member.TechnicianUserId, member.IsSchedulable, member.Reason));
    }

    [Fact]
    public async Task Members_on_leave_for_the_whole_horizon_are_no_available_technician()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Away", BinhTan);
        var site = await ctx.SeedSurveyRequestAsync(BinhTan);
        ctx.Db.TechnicianUnavailablePeriods.Add(new Modules.Scheduling.Entities.TechnicianUnavailablePeriod
        {
            Id = Guid.NewGuid(), TechnicianUserId = tech.Id, StartsAt = DefaultNow.AddDays(-1).ToUniversalTime(), EndsAt = DefaultNow.AddDays(400).ToUniversalTime(),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.Db.SaveChangesAsync();

        var withoutInterval = await ctx.Coverage().EvaluateAsync(site.Site.Id, cancellationToken: None);
        var withInterval = await At9(ctx, site);

        Assert.Equal(CoverageStatuses.NoAvailableTechnician, withoutInterval.Status);
        Assert.Equal(ZoneCoverageService.NoAvailabilityReason, Assert.Single(withoutInterval.Ineligible).Reason);
        Assert.Equal(CoverageStatuses.NoAvailableTechnician, withInterval.Status);
        Assert.Equal(EligibilityReasons.OnLeave, Assert.Single(withInterval.Ineligible).Reason);
    }

    [Fact]
    public async Task Members_free_elsewhere_but_not_at_the_requested_time_are_a_schedule_conflict()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Busy at nine", BinhTan);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var appointment = await ctx.BookAsync(first, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);       // confirmed: never re-homed
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");

        var conflict = await At9(ctx, second);
        var later = await At9(ctx, second, hour: 14);

        Assert.Equal(CoverageStatuses.ScheduleConflict, conflict.Status);
        Assert.Equal(EligibilityReasons.ScheduleConflict, Assert.Single(conflict.Ineligible).Reason);
        Assert.True(later.IsCovered);
        Assert.False(Assert.Single(later.Eligible).RequiresHoldRehoming);
    }

    [Fact]
    public async Task A_member_blocked_only_by_their_own_pending_hold_is_eligible_with_rehoming()
    {
        await using var ctx = Create();
        var holder = await ctx.SeedTechnicianAsync("Holder", BinhTan);
        var first = await ctx.SeedSurveyRequestAsync(BinhTan, "First");
        var hold = await ctx.BookAsync(first, At(9));
        Assert.Equal(holder.Id, hold.ReservedTechnicianUserId);
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");

        var evaluation = await At9(ctx, second);

        Assert.True(evaluation.IsCovered);
        Assert.True(Assert.Single(evaluation.Eligible).RequiresHoldRehoming);
        var ignoringTheHold = await ctx.Coverage().EvaluateAsync(second.Site.Id, At(9).ToUniversalTime(), At(10, 30).ToUniversalTime(), new HashSet<Guid> { hold.Id }, None);
        Assert.False(Assert.Single(ignoringTheHold.Eligible).RequiresHoldRehoming);
        await Assert.ThrowsAsync<ArgumentException>(() => ctx.Coverage().EvaluateAsync(second.Site.Id, At(9), null, cancellationToken: None));
    }

    [Fact]
    public async Task Members_of_other_zones_never_count()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("District 7 tech", District7);
        await ctx.EnsureZoneAsync("Z-BINH-TAN");
        var site = await ctx.SeedSurveyRequestAsync(BinhTan);

        Assert.Equal(CoverageStatuses.NoZoneTechnician, (await At9(ctx, site)).Status);
        Assert.Equal(CoverageStatuses.All.Count, CoverageStatuses.All.Distinct().Count());
    }
}
