using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SchedulingTestContext;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// MIGRATION on a disposable PostgreSQL database: the installation_slot_offer table (offers of the customer-selection flow, retired 2026-11-18 and kept as
/// history) keeps its constraints and RLS, and Down refuses while offers exist.
/// </summary>
public sealed class PostgresInstallationSlotOfferTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Previous = "20261117131913_CloudRagCostControl";
    private const string Migration = "20261117171006_InstallationSlotOffers";
    private static readonly SlotInterval First = new(At(8, 0, Day.AddDays(1)), At(12, 0, Day.AddDays(2)));

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, string sql)
    {
        await using var db = database.NewContext();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }

    [PostgresFact]
    public async Task MIGRATION_PostgreSQL_enforces_the_offer_constraints_RLS_is_on_and_Down_refuses_while_offers_exist()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        // Offers are history since 2026-11-18 (no customer selection): a legacy OPEN offer row, as production may still hold.
        var offer = new { OfferId = Guid.NewGuid() };
        await using (var seed = database.NewContext())
            await seed.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO installation_slot_offer (id, contract_id, technician_user_id, starts_at, ends_at, status, created_by_user_id, created_at, updated_at, revision)
                VALUES ({offer.OfferId}, {signed.Contract.ContractId}, {signed.Cart.TechnicianId}, {First.StartsAt.ToUniversalTime()}, {First.EndsAt.ToUniversalTime()}, 'OPEN', {signed.Cart.TechnicianId}, now(), now(), 0)
                """);

        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_slot_offer SET status = 'MAYBE' WHERE id = '{offer.OfferId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_slot_offer SET ends_at = starts_at WHERE id = '{offer.OfferId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_slot_offer SET status = 'SELECTED' WHERE id = '{offer.OfferId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE installation_slot_offer SET status = 'WITHDRAWN' WHERE id = '{offer.OfferId}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateAsync(database, $"UPDATE installation_slot_offer SET contract_id = gen_random_uuid() WHERE id = '{offer.OfferId}'"));
        // The same interval cannot be open twice for a contract.
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database, $"""
            INSERT INTO installation_slot_offer (id, contract_id, technician_user_id, starts_at, ends_at, status, created_by_user_id, created_at, updated_at, revision)
            SELECT gen_random_uuid(), contract_id, technician_user_id, starts_at, ends_at, 'OPEN', created_by_user_id, now(), now(), 0
            FROM installation_slot_offer WHERE id = '{offer.OfferId}'
            """));

        await using var db = database.NewContext();
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT relrowsecurity AS \"Value\" FROM pg_class WHERE relname = 'installation_slot_offer'").SingleAsync());
        Assert.Contains(Migration, await db.Database.GetAppliedMigrationsAsync());

        // Down refuses (and changes nothing) while offers exist.
        var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.NotNull(refused);
        await using var after = database.NewContext();
        Assert.Contains(Migration, await after.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await after.InstallationSlotOffers.CountAsync());
    }

    [PostgresFact]
    public async Task MIGRATION_Down_and_Up_round_trip_on_an_empty_table()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.installation_slot_offer') IS NOT NULL AS \"Value\"").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.True(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('public.installation_slot_offer') IS NOT NULL AS \"Value\"").SingleAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
