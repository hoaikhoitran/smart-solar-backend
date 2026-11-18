using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.PostgresRace;
using static SmartSolar.Tests.TestSupport.SimplifiedFlowFixture;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// MIGRATION 20261118102011_TechnicianLedOnSiteSales on a disposable PostgreSQL database: an installation-quotation payment line is constrained to its
/// target (exactly one target, the type names it, FK to quotation, one settled payment per quotation), the internally approved substitution status
/// is accepted, Down refuses (changing nothing) while rows use the new values, and Down / Up round-trips on a database without them.
/// </summary>
public sealed class PostgresTechnicianLedSalesMigrationTests
{
    private const string Previous = "20261117185657_AfterSalesTechnicianQuotation";
    private const string Migration = "20261118102011_TechnicianLedOnSiteSales";

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
    public async Task MIGRATION_constraints_hold_and_Down_refuses_while_installation_quotation_payments_exist()
    {
        var (database, ctx) = await WorldAsync();
        await using var _ = database;
        await using var __ = ctx;
        var cart = await IssuedAsync(ctx);
        await AgreedAsync(ctx, cart);
        Assert.True((await ctx.OnSitePayments().StartAsync(cart.TechnicianId, cart.Quotation.QuotationId, "CASH", CancellationToken.None)).Succeeded);
        await using (var read = database.NewContext())
            Assert.Equal(cart.Quotation.QuotationId, (await read.PaymentAttemptItems.SingleAsync()).InstallationQuotationId);

        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, "UPDATE payment_attempt_item SET item_type = 'CONTRACT'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, "UPDATE payment_attempt_item SET installation_quotation_id = NULL"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, "UPDATE payment_attempt_item SET item_type = 'GIFT'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, await SqlStateAsync(database, "UPDATE payment_attempt_item SET installation_quotation_id = gen_random_uuid()"));
        // Two settled lines for one installation quotation are impossible (exactly one contract per paid quotation).
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database, """
            UPDATE payment_attempt_item SET status = 'SETTLED';
            INSERT INTO payment_attempt_item (id, payment_attempt_id, item_type, installation_quotation_id, amount, status)
            SELECT gen_random_uuid(), payment_attempt_id, item_type, installation_quotation_id, amount, 'SETTLED' FROM payment_attempt_item;
            """));
        Assert.Null(await SqlStateAsync(database, "UPDATE installation_equipment_substitution SET status = 'INTERNALLY_APPROVED' WHERE false"));

        await using var db = database.NewContext();
        var refused = await Record.ExceptionAsync(() => db.GetService<IMigrator>().MigrateAsync(Previous));
        Assert.NotNull(refused);
        await using var after = database.NewContext();
        Assert.Contains(Migration, await after.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await after.PaymentAttemptItems.CountAsync(i => i.InstallationQuotationId == cart.Quotation.QuotationId));
    }

    [PostgresFact]
    public async Task MIGRATION_Down_and_Up_round_trip_without_new_flow_data()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var db = database.NewContext();
        await db.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'payment_attempt_item' AND column_name = 'installation_quotation_id') AS \"Value\"").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.True(await db.Database.SqlQueryRaw<bool>(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'payment_attempt_item' AND column_name = 'installation_quotation_id') AS \"Value\"").SingleAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }
}
