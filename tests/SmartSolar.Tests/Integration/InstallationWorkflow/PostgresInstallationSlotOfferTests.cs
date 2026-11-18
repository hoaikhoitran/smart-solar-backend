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
/// E2E-04 / MIGRATION on a disposable PostgreSQL database (2026-11-18 customer installation-time choice): concurrent selections never allocate one
/// technician interval twice or create two live schedules for one contract; the table's constraints hold; RLS is on; Down refuses while offers exist.
/// </summary>
public sealed class PostgresInstallationSlotOfferTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Previous = "20261117131913_CloudRagCostControl";
    private const string Migration = "20261117171006_InstallationSlotOffers";
    private static readonly SlotInterval First = new(At(8, 0, Day.AddDays(1)), At(12, 0, Day.AddDays(2)));
    private static readonly SlotInterval Second = new(At(8, 0, Day.AddDays(3)), At(12, 0, Day.AddDays(3)));

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
    public async Task E2E_04_Concurrent_customers_choosing_the_same_technician_interval_allocate_it_once()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var start = ctx.Clock.Now;
        var contracts = new List<SimplifiedFlowFixture.Signed>();
        foreach (var name in new[] { "Customer A", "Customer B", "Customer C" })
        {
            ctx.Clock.Now = start;
            contracts.Add(await SimplifiedFlowFixture.SignedAsync(ctx, name));
        }
        var technician = contracts[0].Cart.TechnicianId;
        foreach (var report in await ctx.Db.OnSiteSurveyReports.ToListAsync()) report.TechnicianUserId = technician;
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        var offers = new List<InstallationSlotOfferView>();
        foreach (var c in contracts)
            offers.Add((await ctx.Schedules().PublishOffersAsync(technician, c.Contract.ContractId, [First], null, None)).Value!.Single());

        var results = await RaceAsync(database, contracts.Count, (db, i) =>
        {
            var w = InstallationWorkflowTestContext.CreateOn(db);
            w.Clock.Now = ctx.Clock.Now;
            return w.Schedules(db).SelectOfferAsync(contracts[i].Cart.CustomerId, offers[i].OfferId, None);
        });

        Assert.Single(results, r => r.Succeeded);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal(InstallationScheduleErrorCodes.OfferUnavailable, r.ErrorCode));
        await using var check = database.NewContext();
        Assert.Equal(1, await check.InstallationScheduleProposals.CountAsync(p => p.TechnicianUserId == technician && p.Status != InstallationScheduleStatuses.Cancelled));
        Assert.Equal(1, await check.InstallationSlotOffers.CountAsync(o => o.Status == InstallationSlotOfferStatuses.Selected));
    }

    [PostgresFact]
    public async Task Concurrent_selections_of_one_contract_leave_one_live_schedule_and_a_double_click_is_idempotent()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var contractId = signed.Contract.ContractId;
        var offers = (await ctx.Schedules().PublishOffersAsync(signed.Cart.TechnicianId, contractId, [First, Second], null, None)).Value!;

        // Same offer clicked 4 times + the other offer twice, all at once.
        var picks = new[] { offers[0], offers[0], offers[0], offers[0], offers[1], offers[1] };
        var results = await RaceAsync(database, picks.Length, (db, i) =>
        {
            var w = InstallationWorkflowTestContext.CreateOn(db);
            w.Clock.Now = ctx.Clock.Now;
            return w.Schedules(db).SelectOfferAsync(signed.Cart.CustomerId, picks[i].OfferId, None);
        });

        await using var check = database.NewContext();
        var live = await check.InstallationScheduleProposals.SingleAsync(p => p.ContractId == contractId && p.Status != InstallationScheduleStatuses.Cancelled);
        Assert.Equal(1, await check.InstallationScheduleProposals.CountAsync(p => p.ContractId == contractId));
        var selected = await check.InstallationSlotOffers.SingleAsync(o => o.Status == InstallationSlotOfferStatuses.Selected);
        Assert.Equal(live.Id, selected.SelectedScheduleId);
        Assert.Equal(InstallationSlotOfferStatuses.Withdrawn, (await check.InstallationSlotOffers.SingleAsync(o => o.Id != selected.Id)).Status);
        // Winners all report the same schedule; losers are refused (never a second schedule).
        Assert.All(results.Where(r => r.Succeeded), r => Assert.Equal(live.Id, r.Value!.ScheduleId));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode,
            new[] { InstallationScheduleErrorCodes.OfferNotOpen, InstallationScheduleErrorCodes.StaleRevision }));
    }

    [PostgresFact]
    public async Task MIGRATION_PostgreSQL_enforces_the_offer_constraints_RLS_is_on_and_Down_refuses_while_offers_exist()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
        var offer = (await ctx.Schedules().PublishOffersAsync(signed.Cart.TechnicianId, signed.Contract.ContractId, [First], null, None)).Value!.Single();

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
