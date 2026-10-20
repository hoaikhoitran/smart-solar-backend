using SmartSolar.Modules.Identity.Constants;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.PreSurveyAppointments;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Unit.Scheduling;

/// <summary>
/// Operational zones (data, not code) over the REAL official catalog: exclusive commune mapping with history, technician
/// memberships (several zones per technician) and the admin guards around confirmed work.
/// </summary>
public sealed class ZoneAndAdminTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<OperationalZone> CreateZoneAsync(SchedulingTestContext ctx, string code, params string[] communes)
    {
        var created = await ctx.Zones().CreateAsync(new CreateZoneCommand(await ctx.ZoneAdminIdAsync(), code, $"Zone {code}", true, true, null), None);
        Assert.True(created.Succeeded, created.Message);
        if (communes.Length > 0)
        {
            var set = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(await ctx.ZoneAdminIdAsync(), created.Value!.ZoneId, communes, null, 0), None);
            Assert.True(set.Succeeded, set.Message);
        }
        return (await ctx.Db.OperationalZones.AsNoTracking().SingleAsync(z => z.Id == created.Value!.ZoneId));
    }

    [Fact]
    public async Task Zones_are_created_with_a_normalized_unique_code()
    {
        await using var ctx = Create();
        var zones = ctx.Zones();

        var created = await zones.CreateAsync(new CreateZoneCommand(await ctx.ZoneAdminIdAsync(), " khu-thu-duc ", "Khu Thủ Đức", true, true, null), None);
        var duplicate = await zones.CreateAsync(new CreateZoneCommand(await ctx.ZoneAdminIdAsync(), "KHU-THU-DUC", "Again", true, true, null), None);

        Assert.True(created.Succeeded, created.Message);
        Assert.Equal("KHU-THU-DUC", created.Value!.Code);
        Assert.True(created.Value.IsServiceable);
        Assert.Equal(0, created.Value.CommuneCount);
        Assert.Equal(SchedulingErrorCodes.ZoneCodeExists, duplicate.ErrorCode);
        var validator = new CreateZoneCommandValidator();
        Assert.False((await validator.ValidateAsync(new CreateZoneCommand(Guid.Empty, "-BAD", "x", true, true, null))).IsValid);
        Assert.False((await validator.ValidateAsync(new CreateZoneCommand(Guid.Empty, "A", "x", true, true, null))).IsValid);
        Assert.False((await validator.ValidateAsync(new CreateZoneCommand(Guid.Empty, "KHU THU DUC", "x", true, true, null))).IsValid);
        Assert.True((await validator.ValidateAsync(new CreateZoneCommand(Guid.Empty, "KHU-1", "x", true, true, null))).IsValid);
    }

    [Fact]
    public async Task Zone_updates_need_the_current_revision_and_a_real_manager()
    {
        await using var ctx = Create();
        var zone = await ctx.EnsureZoneAsync("Z-BINH-TAN");
        var notManager = await ctx.Inner.SeedUserAsync("Not a manager");

        var renamed = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), zone.Id, "Binh Tan District", true, null, null, zone.Revision), None);
        var stale = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), zone.Id, "Again", true, null, null, zone.Revision), None);
        var badManager = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), zone.Id, "Again", true, null, notManager.Id, zone.Revision + 1), None);

        Assert.True(renamed.Succeeded, renamed.Message);
        Assert.Equal(zone.Revision + 1, renamed.Value!.Revision);
        Assert.True(renamed.Value.IsServiceable);                                     // null keeps the flag
        Assert.Equal(SchedulingErrorCodes.ZoneStale, stale.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ResponsibleManagerInvalid, badManager.ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ZoneNotFound,
            (await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), Guid.NewGuid(), "x", null, null, null, 0), None)).ErrorCode);
    }

    [Fact]
    public async Task A_commune_belongs_to_one_active_zone_and_the_mapping_history_is_kept()
    {
        await using var ctx = Create();
        await ctx.EnsureCatalogAsync();
        var a = await CreateZoneAsync(ctx, "ZONE-A", SaiGon, BenThanh);
        var b = await CreateZoneAsync(ctx, "ZONE-B");
        var admin = await ctx.ZoneAdminIdAsync();

        var taken = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, b.Id, [SaiGon], null, b.Revision), None);
        Assert.Equal(SchedulingErrorCodes.CommuneAlreadyMapped, taken.ErrorCode);
        Assert.Contains("ZONE-A", System.Text.Json.JsonSerializer.Serialize(taken.Details));

        // Remove Sài Gòn from A (with a reason), then B may take it.
        var shrunk = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, a.Id, [BenThanh], "split the old district", a.Revision), None);
        Assert.True(shrunk.Succeeded, shrunk.Message);
        Assert.Equal([BenThanh], shrunk.Value!.Communes.Select(c => c.Code).ToArray());
        Assert.Equal("Phường Bến Thành", shrunk.Value.Communes[0].Name);
        var moved = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, b.Id, [SaiGon], null, b.Revision), None);
        Assert.True(moved.Succeeded, moved.Message);

        var history = await ctx.Db.OperationalZoneCommunes.AsNoTracking().Where(m => m.CommuneCode == SaiGon).ToListAsync();
        Assert.Equal(2, history.Count);
        var removed = Assert.Single(history, m => m.RemovedAt is not null);
        Assert.Equal((a.Id, admin, "split the old district"), (removed.ZoneId, removed.RemovedByUserId!.Value, removed.RemovalReason));
        Assert.Equal(b.Id, Assert.Single(history, m => m.RemovedAt is null).ZoneId);

        // Stale revision and the idempotent repeat.
        Assert.Equal(SchedulingErrorCodes.ZoneStale,
            (await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, b.Id, [SaiGon, TanMy], null, b.Revision), None)).ErrorCode);
        var same = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, b.Id, [SaiGon], null, b.Revision + 1), None);
        Assert.True(same.Succeeded);
        Assert.Equal(b.Revision + 1, same.Value!.Zone.Revision);                      // nothing changed: no new revision
    }

    [Fact]
    public async Task A_commune_held_by_an_inactive_zone_is_moved_with_history()
    {
        await using var ctx = Create();
        await ctx.EnsureCatalogAsync();
        var old = await CreateZoneAsync(ctx, "OLD", TanMy);
        var deactivated = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), old.Id, old.Name, false, null, null, old.Revision), None);
        Assert.True(deactivated.Succeeded, deactivated.Message);
        var fresh = await CreateZoneAsync(ctx, "NEW");

        var moved = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(await ctx.ZoneAdminIdAsync(), fresh.Id, [TanMy], "reorganised", fresh.Revision), None);

        Assert.True(moved.Succeeded, moved.Message);
        var leftover = await ctx.Db.OperationalZoneCommunes.AsNoTracking().SingleAsync(m => m.ZoneId == old.Id);
        Assert.NotNull(leftover.RemovedAt);
        Assert.Equal("MOVED_TO_ZONE NEW: reorganised", leftover.RemovalReason);
    }

    [Fact]
    public async Task Commune_codes_are_checked_against_the_active_catalog()
    {
        await using var ctx = Create();
        await ctx.EnsureCatalogAsync();
        var zone = await CreateZoneAsync(ctx, "ZONE-X");
        var admin = await ctx.ZoneAdminIdAsync();

        var unknown = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, zone.Id, ["99999"], null, zone.Revision), None);
        Assert.Equal(SchedulingErrorCodes.LocationUnknown, unknown.ErrorCode);
        await ctx.Db.AdministrativeCommunes.Where(c => c.Code == ConDao)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.ValidTo, new DateOnly(2026, 1, 1)));
        var superseded = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(admin, zone.Id, [ConDao], null, zone.Revision), None);
        Assert.Equal(SchedulingErrorCodes.LocationSuperseded, superseded.ErrorCode);

        var validator = new SetZoneCommunesCommandValidator();
        Assert.False((await validator.ValidateAsync(new SetZoneCommunesCommand(admin, zone.Id, ["2674"], null, 0))).IsValid);     // lost leading digit
        Assert.False((await validator.ValidateAsync(new SetZoneCommunesCommand(admin, zone.Id, ["79.769"], null, 0))).IsValid);
        Assert.False((await validator.ValidateAsync(new SetZoneCommunesCommand(admin, zone.Id, null, null, 0))).IsValid);
        Assert.True((await validator.ValidateAsync(new SetZoneCommunesCommand(admin, zone.Id, ["00001"], null, 0))).IsValid);    // leading zeros are digits
    }

    [Fact]
    public async Task Removing_a_commune_with_unfinished_work_is_refused_and_nothing_is_reassigned()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);
        var zone = await ctx.EnsureZoneAsync("Z-BINH-TAN");

        var refused = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(await ctx.ZoneAdminIdAsync(), zone.Id, [BinhTriDong], "shrink", zone.Revision), None);

        Assert.Equal(SchedulingErrorCodes.CommuneInUse, refused.ErrorCode);
        var details = System.Text.Json.JsonSerializer.Serialize(refused.Details);
        Assert.Contains(BinhTanWard, details);
        Assert.Contains("\"blockingAppointments\":1", details);
        Assert.Equal(2, await ctx.Db.OperationalZoneCommunes.CountAsync(m => m.ZoneId == zone.Id && m.RemovedAt == null));
        Assert.Equal(tech.Id, (await ctx.AppointmentAsync(appointment.Id)).AssignedTechnicianUserId);

        // A commune without work can go.
        var ok = await ctx.Zones().SetCommunesAsync(new SetZoneCommunesCommand(await ctx.ZoneAdminIdAsync(), zone.Id, [BinhTanWard], "shrink", zone.Revision), None);
        Assert.True(ok.Succeeded, ok.Message);
    }

    [Fact]
    public async Task Deactivating_or_marking_a_zone_unserviceable_is_refused_while_work_records_it()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var appointment = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan), At(9));
        var zone = await ctx.EnsureZoneAsync("Z-BINH-TAN");
        Assert.Equal(zone.Id, appointment.OperationalZoneId);

        Assert.Equal(SchedulingErrorCodes.ZoneInUse, (await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), zone.Id, zone.Name, false, null, null, zone.Revision), None)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ZoneInUse, (await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), zone.Id, zone.Name, null, false, null, zone.Revision), None)).ErrorCode);

        Assert.True((await ctx.Cancel().HandleAsync(new CancelSurveyAppointmentCommand(appointment.BookedByUserId, appointment.SurveyRequestId!.Value, null), None)).Succeeded);
        var unserviceable = await ctx.Zones().UpdateAsync(new UpdateZoneCommand(await ctx.ZoneAdminIdAsync(), zone.Id, zone.Name, null, false, null, zone.Revision), None);
        Assert.True(unserviceable.Succeeded, unserviceable.Message);
        Assert.False(unserviceable.Value!.IsServiceable);
        Assert.True(unserviceable.Value.IsActive);
    }

    [Fact]
    public async Task Coverage_report_lists_unmapped_communes_and_zones_without_schedulable_technicians()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Active", BinhTan);
        await ctx.SeedTechnicianAsync("Disabled", ThuDuc, active: false);

        var report = await ctx.Zones().CoverageAsync(Province, None);
        var zones = await ctx.Zones().ListAsync(None);

        Assert.Equal(168 - 3, report.UnmappedCommuneCount);                          // Bình Tân zone maps 2, Thủ Đức zone 1
        Assert.DoesNotContain(report.UnmappedCommunes, c => c.Code is BinhTanWard or BinhTriDong or ThuDucWard);
        Assert.Contains(report.UnmappedCommunes, c => c.Code == ConDao && c.UnitType == CommuneUnitTypes.SpecialZone);
        var gap = Assert.Single(report.ZonesWithoutSchedulableTechnicians);
        Assert.Equal("Z-THU-DUC", gap.Code);
        var thuDuc = zones.Single(z => z.Code == "Z-THU-DUC");
        Assert.Equal((1, 1, 0, false), (thuDuc.CommuneCount, thuDuc.MemberCount, thuDuc.SchedulableMemberCount, thuDuc.HasAvailableTechnician));
        Assert.True(zones.Single(z => z.Code == "Z-BINH-TAN").HasAvailableTechnician);
        Assert.Equal(2, zones.Single(z => z.Code == "Z-BINH-TAN").CommuneCount);
        Assert.Single(await ctx.Admin().ListAsync((await ctx.EnsureZoneAsync("Z-BINH-TAN")).Id, None));
        var members = (await ctx.Zones().TechniciansAsync((await ctx.EnsureZoneAsync("Z-THU-DUC")).Id, None)).Value!;
        Assert.False(Assert.Single(members).IsSchedulable);
        Assert.Equal(SchedulingErrorCodes.ZoneNotFound, (await ctx.Zones().TechniciansAsync(Guid.NewGuid(), None)).ErrorCode);
    }

    [Fact]
    public async Task Replacing_zones_keeps_history_and_refuses_to_strand_accepted_work()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianInZonesAsync("Tech", ["Z-BINH-TAN", "Z-THU-DUC"]);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var appointment = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, appointment.Id)).Succeeded);
        var binhTan = await ctx.EnsureZoneAsync("Z-BINH-TAN");
        var thuDuc = await ctx.EnsureZoneAsync("Z-THU-DUC");
        var district7 = await ctx.EnsureZoneAsync("Z-D7");
        var admin = await ctx.SeedStaffAsync("Admin", RoleCodes.Admin);

        var refused = await ctx.Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, tech.Id, [thuDuc.Id]), None);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, refused.ErrorCode);
        var refusedDetails = System.Text.Json.JsonSerializer.Serialize(refused.Details);
        Assert.Contains(appointment.Id.ToString(), refusedDetails);
        Assert.Contains(SchedulingErrorCodes.TechnicianOutsideZone, refusedDetails);

        var replaced = await ctx.Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, tech.Id, [binhTan.Id, district7.Id]), None);
        Assert.True(replaced.Succeeded, replaced.Message);
        Assert.Equal(["Z-BINH-TAN", "Z-D7"], replaced.Value!.Zones.Select(z => z.Code).ToArray());
        var history = await ctx.Db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.TechnicianUserId == tech.Id).ToListAsync();
        Assert.Equal(3, history.Count);
        var removed = Assert.Single(history, m => m.ZoneId == thuDuc.Id);
        Assert.NotNull(removed.RemovedAt);
        Assert.Equal(admin.Id, removed.RemovedByUserId);
        Assert.Equal(SchedulingErrorCodes.ZoneNotFound,
            (await ctx.Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, tech.Id, [Guid.NewGuid()]), None)).ErrorCode);
        // Re-adding a removed zone creates a new active membership; the old row stays as history.
        Assert.True((await ctx.Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, tech.Id, [binhTan.Id, district7.Id, thuDuc.Id]), None)).Succeeded);
        Assert.Equal(2, await ctx.Db.TechnicianZoneMemberships.CountAsync(m => m.TechnicianUserId == tech.Id && m.ZoneId == thuDuc.Id));
    }

    [Fact]
    public async Task A_multi_zone_technician_sees_and_serves_every_zone()
    {
        await using var ctx = Create();
        var both = await ctx.SeedTechnicianInZonesAsync("Both", ["Z-BINH-TAN", "Z-D7"]);
        var binhTan = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(BinhTan, "BT"), At(9));
        var district7 = await ctx.BookAsync(await ctx.SeedSurveyRequestAsync(District7, "D7"), At(13));

        var open = await ctx.Jobs().ListOpenAsync(both.Id, None);

        Assert.Equal([binhTan.Id, district7.Id], open.Select(o => o.AppointmentId).ToArray());
        Assert.Equal(["Z-BINH-TAN", "Z-D7"], open.Select(o => o.Zone!.Code).ToArray());
        Assert.True((await ctx.AcceptAsync(both.Id, binhTan.Id)).Succeeded);
        Assert.True((await ctx.AcceptAsync(both.Id, district7.Id)).Succeeded);
    }

    [Fact]
    public async Task Removing_a_zone_rehomes_pending_holds_or_escalates_them()
    {
        await using var ctx = Create();
        var leaving = await ctx.SeedTechnicianAsync("Leaving", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var held = await ctx.BookAsync(request, At(9));
        Assert.Equal(leaving.Id, held.ReservedTechnicianUserId);
        var other = await ctx.SeedTechnicianAsync("Other", BinhTan);
        var admin = await ctx.SeedStaffAsync("Admin", RoleCodes.Admin);

        var moved = await ctx.Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, leaving.Id, []), None);

        Assert.True(moved.Succeeded, moved.Message);
        Assert.Equal(other.Id, Assert.Single(moved.Value!.RehomedHolds).NewHolderUserId);
        Assert.Equal(other.Id, (await ctx.AppointmentAsync(held.Id)).ReservedTechnicianUserId);

        var escalated = await ctx.Admin().SetZonesAsync(new SetTechnicianZonesCommand(admin.Id, other.Id, []), None);
        Assert.True(escalated.Succeeded, escalated.Message);
        var stored = await ctx.AppointmentAsync(held.Id);
        Assert.Equal(AppointmentStatuses.Escalated, stored.Status);
        Assert.Null(stored.ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Disabling_scheduling_is_refused_with_accepted_work_and_moves_holds()
    {
        await using var ctx = Create();
        var busy = await ctx.SeedTechnicianAsync("Busy", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var accepted = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(busy.Id, accepted.Id)).Succeeded);

        var refused = await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(busy.Id, false, null), None);
        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, refused.ErrorCode);
        Assert.True((await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(busy.Id, true, 2), None)).Succeeded);

        var holder = await ctx.SeedTechnicianAsync("Holder only", District7);
        var other = await ctx.SeedTechnicianAsync("Other", District7);
        var second = await ctx.SeedSurveyRequestAsync(District7, "Second");
        var hold = await ctx.BookAsync(second, At(13));
        var holderId = hold.ReservedTechnicianUserId!.Value;
        var disabled = await ctx.Admin().UpsertProfileAsync(new UpsertTechnicianProfileCommand(holderId, false, null), None);
        Assert.True(disabled.Succeeded, disabled.Message);
        Assert.Equal(holderId == holder.Id ? other.Id : holder.Id, (await ctx.AppointmentAsync(hold.Id)).ReservedTechnicianUserId);
    }

    [Fact]
    public async Task Leave_over_accepted_work_is_refused_and_leave_over_a_hold_moves_it()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var accepted = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, accepted.Id)).Succeeded);
        var second = await ctx.SeedSurveyRequestAsync(BinhTan, "Second");
        var hold = await ctx.BookAsync(second, At(13));
        Assert.Equal(tech.Id, hold.ReservedTechnicianUserId);
        var other = await ctx.SeedTechnicianAsync("Other", BinhTan);

        var refused = await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), tech.Id, At(8), At(10), "Errand"), None);
        var afternoon = await ctx.Admin().AddUnavailablePeriodAsync(new AddUnavailablePeriodCommand(Guid.NewGuid(), tech.Id, At(13), At(17), "Errand"), None);

        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, refused.ErrorCode);
        Assert.True(afternoon.Succeeded, afternoon.Message);
        Assert.Equal(other.Id, (await ctx.AppointmentAsync(hold.Id)).ReservedTechnicianUserId);
        Assert.Equal(AppointmentStatuses.PendingAcceptance, (await ctx.AppointmentAsync(hold.Id)).Status);
    }

    [Fact]
    public async Task A_working_schedule_that_strands_accepted_work_is_refused()
    {
        await using var ctx = Create();
        var tech = await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan);
        var accepted = await ctx.BookAsync(request, At(9));
        Assert.True((await ctx.AcceptAsync(tech.Id, accepted.Id)).Succeeded);

        var result = await ctx.Admin().SetWorkingScheduleAsync(new SetWorkingScheduleCommand(tech.Id, false,
            [new WorkingPeriodInput(DayOfWeek.Tuesday, new TimeOnly(13, 0), new TimeOnly(17, 0))]), None);

        Assert.Equal(SchedulingErrorCodes.TechnicianHasConflictingAppointments, result.ErrorCode);
        Assert.Contains(Modules.Scheduling.Domain.EligibilityReasons.OutsideWorkingHours, System.Text.Json.JsonSerializer.Serialize(result.Details));
    }

    [Fact]
    public async Task Customers_set_the_site_location_against_the_active_catalog_but_not_while_a_booking_is_active()
    {
        await using var ctx = Create();
        await ctx.SeedTechnicianAsync("Tech", BinhTan);
        var request = await ctx.SeedSurveyRequestAsync(BinhTan, withCodes: false);
        var stranger = await ctx.SeedSurveyRequestAsync(BinhTan, "Stranger");
        var zones = ctx.Zones();
        Task<SchedulingResult<SiteLocationView>> Set(Guid customer, string? province, string? commune)
            => zones.SetSiteLocationAsync(new SetSiteLocationCommand(customer, request.Site.Id, province, commune), None);

        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await Set(request.Customer.Id, "79", null)).ErrorCode);          // commune required
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await Set(request.Customer.Id, null, BinhTanWard)).ErrorCode);
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await Set(request.Customer.Id, "79", "79.777")).ErrorCode);      // old dotted model
        Assert.Equal(SchedulingErrorCodes.ValidationFailed, (await Set(request.Customer.Id, "79", "2744")).ErrorCode);        // lost a digit
        Assert.Equal(SchedulingErrorCodes.LocationOutOfServiceArea, (await Set(request.Customer.Id, "01", BinhTanWard)).ErrorCode); // outside Ho Chi Minh City
        Assert.Equal(SchedulingErrorCodes.LocationUnknown, (await Set(request.Customer.Id, "79", "00001")).ErrorCode);        // unknown commune
        Assert.Equal(SchedulingErrorCodes.PropertySiteNotFound, (await zones.SetSiteLocationAsync(
            new SetSiteLocationCommand(stranger.Customer.Id, request.Site.Id, "79", BinhTanWard), None)).ErrorCode);
        await ctx.Db.AdministrativeCommunes.Where(c => c.Code == ConDao)
            .ExecuteUpdateAsync(x => x.SetProperty(c => c.ValidTo, new DateOnly(2026, 1, 1)));
        Assert.Equal(SchedulingErrorCodes.LocationSuperseded, (await Set(request.Customer.Id, "79", ConDao)).ErrorCode);

        var set = await Set(request.Customer.Id, "79", BinhTanWard);
        Assert.True(set.Succeeded, set.Message);
        Assert.Equal(("Phường Bình Tân", CoverageStatuses.Covered, "Z-BINH-TAN"), (set.Value!.CommuneName, set.Value.CoverageStatus, set.Value.Zone!.Code));
        Assert.Equal(CoverageStatuses.NoZoneCoverage, (await Set(request.Customer.Id, "79", BenThanh)).Value!.CoverageStatus);
        Assert.True((await Set(request.Customer.Id, "79", BinhTanWard)).Succeeded);

        await ctx.BookAsync(request, At(9));
        Assert.Equal(SchedulingErrorCodes.SiteHasActiveBooking, (await Set(request.Customer.Id, "79", ThuDucWard)).ErrorCode);
        Assert.True((await Set(request.Customer.Id, "79", BinhTanWard)).Succeeded);                                          // unchanged: allowed
        Assert.Equal(400, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.ValidationFailed));
        Assert.Equal(422, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.LocationUnknown));
        Assert.Equal(422, Api.Controllers.SchedulingControllerBase.StatusFor(SchedulingErrorCodes.LocationSuperseded));
    }
}
