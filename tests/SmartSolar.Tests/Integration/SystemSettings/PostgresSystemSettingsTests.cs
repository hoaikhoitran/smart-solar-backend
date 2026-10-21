using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.SystemSettings;
using SmartSolar.Modules.SystemSettings;
using SmartSolar.Tests.TestSupport;
using K = SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys;

namespace SmartSolar.Tests.Integration.SystemSettings;

/// <summary>System settings on a disposable local PostgreSQL: persistence, database checks, and concurrent updates where exactly one writer wins.</summary>
public sealed class PostgresSystemSettingsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static SystemSettingsService Service(AppDbContext db)
    {
        var uow = new SystemSettingsUnitOfWork(db);
        var cache = new SchedulingPolicyCache(TimeSpan.Zero);
        return new SystemSettingsService(uow, SchedulingPolicyDefinitions.Default,
            new SchedulingPolicyProvider(uow, SchedulingPolicyDefinitions.Default, cache, TimeProvider.System), cache, TimeProvider.System,
            NullLogger<SystemSettingsService>.Instance);
    }

    private static async Task<Guid[]> AdminsAsync(PostgresTestDatabase database, int count)
    {
        await using var db = database.NewContext();
        var users = Enumerable.Range(0, count).Select(i => PreSurveyTestContext.NewUser($"Settings admin {i}")).ToList();
        db.UserAccounts.AddRange(users);
        await db.SaveChangesAsync();
        return users.Select(u => u.Id).ToArray();
    }

    private static SchedulingSettingsUpdateInput Input(int expectedVersion, string key, int value)
        => new([new SystemSettingChangeInput(key, value, null)], expectedVersion, "race");

    [PostgresFact]
    public async Task Updates_persist_and_the_database_rejects_invalid_rows()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var admin = (await AdminsAsync(database, 1))[0];

        await using (var db = database.NewContext())
        {
            var result = await Service(db).UpdateSchedulingAsync(admin, new SchedulingSettingsUpdateInput(
                [new(K.DispatchProposalResponseWindowMinutes, 720, null), new(K.InstallationMaxAssignmentDays, 10, "DAYS")], 0, "Shorter proposals"), None);
            Assert.True(result.Succeeded, result.Message);
        }

        await using (var db = database.NewContext())
        {
            var snapshot = await new SchedulingPolicyProvider(new SystemSettingsUnitOfWork(db), SchedulingPolicyDefinitions.Default,
                new SchedulingPolicyCache(TimeSpan.Zero), TimeProvider.System).GetAsync(None);
            Assert.Equal((1, 720, 10, 240), (snapshot.Version, snapshot.DispatchProposalResponseWindowMinutes, snapshot.InstallationMaxAssignmentDays,
                snapshot.DispatchReviewSlaMinutes));
            var revisions = await db.Set<SystemSettingRevision>().AsNoTracking().OrderBy(r => r.Key).ToListAsync();
            Assert.Equal([(K.DispatchProposalResponseWindowMinutes, 1440, 720, "MINUTES"), (K.InstallationMaxAssignmentDays, 14, 10, "DAYS")],
                revisions.Select(r => (r.Key, r.OldValue, r.NewValue, r.Unit)));
            Assert.All(revisions, r => Assert.Equal(("Shorter proposals", admin, 1), (r.Reason, r.ChangedByUserId, r.Version)));
            var version = await db.Set<SystemSettingsVersion>().AsNoTracking().SingleAsync();
            Assert.Equal((1, 1, admin), (version.Version, version.Revision, version.UpdatedByUserId));

            // Database checks: unit, singleton version row, unique (key, version) audit.
            Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(db,
                "INSERT INTO system_setting (key, value_int, unit, version, updated_at, revision) VALUES ('x.y', 1, 'WEEKS', 1, now(), 1)"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(db,
                "INSERT INTO system_settings_version (id, version, updated_at, revision) VALUES (2, 1, now(), 1)"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(db,
                $"INSERT INTO system_setting_revision (id, key, version, old_value, new_value, unit, changed_by, changed_at) VALUES ('{Guid.NewGuid()}', "
                + $"'{K.InstallationMaxAssignmentDays}', 1, 1, 2, 'DAYS', '{admin}', now())"));
        }
    }

    [PostgresFact]
    public async Task Concurrent_updates_from_the_same_version_have_exactly_one_winner()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var admins = await AdminsAsync(database, 6);

        // Bootstrap race (the version row does not exist yet), on different keys: the global version serialises them.
        string[] keys = [K.DispatchReviewSlaMinutes, K.AcceptanceWindowMinutes, K.CustomerChangeLeadMinutes, K.BufferBetweenAppointmentsMinutes, K.MinimumBookingLeadMinutes,
            K.DispatchEscalationDelayMinutes];
        var first = await PostgresRace.RaceAsync(database, 6, (db, i) => Service(db).UpdateSchedulingAsync(admins[i], Input(0, keys[i], 60 + i), None));
        Assert.Single(first, r => r.Succeeded);
        Assert.All(first.Where(r => !r.Succeeded), r => Assert.Equal(SystemSettingErrorCodes.VersionConflict, r.ErrorCode));

        // Update race on the existing row lock, same key.
        var second = await PostgresRace.RaceAsync(database, 6, (db, i) => Service(db).UpdateSchedulingAsync(admins[i], Input(1, K.BookingHorizonDays, 10 + i), None));
        Assert.Single(second, r => r.Succeeded);
        Assert.All(second.Where(r => !r.Succeeded), r => Assert.Equal(SystemSettingErrorCodes.VersionConflict, r.ErrorCode));
        var winner = second.Single(r => r.Succeeded).Value!;
        Assert.Equal(2, winner.Version);

        await using var check = database.NewContext();
        Assert.Equal(2, (await check.Set<SystemSettingsVersion>().AsNoTracking().SingleAsync()).Version);
        Assert.Equal(2, await check.Set<SystemSettingRevision>().CountAsync());
        Assert.Equal(winner.Settings.Single(s => s.Key == K.BookingHorizonDays).Value,
            (await check.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == K.BookingHorizonDays)).ValueInt);
        Assert.Equal(2, await check.Set<SystemSetting>().CountAsync());
    }

    private static async Task<string?> SqlStateAsync(AppDbContext db, string sql)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }
}
