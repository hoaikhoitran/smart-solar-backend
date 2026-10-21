using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Persistence;

/// <summary>
/// Runs the AfterSalesServices migration Up and Down on disposable local databases (never a shared one) and compares the
/// resulting catalog with a database that never had the migration: Down must restore exactly the previous schema, and must
/// refuse, changing nothing, while service-only payments exist.
/// </summary>
public sealed class PostgresAfterSalesMigrationTests
{
    private const string Previous = "20261020051352_AdministrativeLocationsZonesDispatchAndSettings";
    private const string AfterSales = "20261021023128_AfterSalesServices";

    /// <summary>Columns (type, nullability, default), constraints, indexes and row-level security of the public schema, one line each.</summary>
    private static async Task<List<string>> SchemaAsync(AppDbContext db) => await db.Database.SqlQueryRaw<string>("""
        SELECT 'column ' || table_name || '.' || column_name || ' ' || data_type || ' ' || coalesce(character_maximum_length::text, '')
               || ' ' || coalesce(numeric_precision::text, '') || ',' || coalesce(numeric_scale::text, '') || ' null=' || is_nullable
               || ' default=' || coalesce(column_default, '') AS "Value"
        FROM information_schema.columns WHERE table_schema = 'public' AND table_name <> '__EFMigrationsHistory'
        UNION ALL
        SELECT 'constraint ' || conrelid::regclass::text || ' ' || conname || ' ' || pg_get_constraintdef(oid)
        FROM pg_constraint WHERE connamespace = 'public'::regnamespace
        UNION ALL
        SELECT 'index ' || indexdef FROM pg_indexes WHERE schemaname = 'public'
        UNION ALL
        SELECT 'rls ' || relname || ' ' || relrowsecurity FROM pg_class WHERE relnamespace = 'public'::regnamespace AND relkind = 'r'
        """).OrderBy(x => x).ToListAsync();

    private static Task MigrateToAsync(AppDbContext db, string migration) => db.GetService<IMigrator>().MigrateAsync(migration);

    [PostgresFact]
    public async Task Down_restores_the_previous_schema_exactly_and_Up_can_run_again()
    {
        // Arrange: one database that never had the migration, one with every migration.
        await using var before = await PostgresTestDatabase.CreateAsync(upToMigration: Previous);
        await using var full = await PostgresTestDatabase.CreateAsync();
        await using var expectedDb = before.NewContext();
        var expected = await SchemaAsync(expectedDb);
        await using var db = full.NewContext();
        Assert.Contains("rls service_request true", await SchemaAsync(db));

        // Act
        await MigrateToAsync(db, Previous);
        var afterDown = await SchemaAsync(db);
        await db.Database.MigrateAsync();

        // Assert: identical catalog (no leftover Guid.Empty default, constraint or index), and the round trip works.
        Assert.Equal(expected, afterDown);
        Assert.Contains("column payment_attempt.contract_id uuid  , null=NO default=", afterDown);
        Assert.Contains(AfterSales, await db.Database.GetAppliedMigrationsAsync());
    }

    [PostgresFact]
    public async Task Down_refuses_while_service_only_payments_exist_and_changes_nothing()
    {
        // Arrange: a package paid in a service-only checkout (payment_attempt.contract_id is NULL).
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var ctx = AfterSalesTestContext.CreateOn(database.NewContext());
        var h = await ctx.HandedOverAsync();
        var term = await ctx.ActiveTermAsync(h, await ctx.PackageAsync(h.AdminId, ServicePackageKinds.Maintenance));
        await using var db = database.NewContext();
        var schemaBefore = await SchemaAsync(db);

        // Act
        var refused = await Record.ExceptionAsync(() => MigrateToAsync(db, Previous));

        // Assert: the guard's message, the migration still recorded, the schema and the payment data untouched.
        Assert.NotNull(refused);
        Assert.Contains("Down refused", refused.ToString());
        await using var check = database.NewContext();
        Assert.Contains(AfterSales, await check.Database.GetAppliedMigrationsAsync());
        Assert.Equal(schemaBefore, await SchemaAsync(check));
        Assert.Equal(SubscriptionStatuses.Active, (await check.ServiceSubscriptions.AsNoTracking().SingleAsync(s => s.Id == term.Id)).Status);
        Assert.Equal(1, await check.PaymentAttempts.CountAsync(a => a.ContractId == null));
        Assert.Equal(1, await check.PaymentAttemptItems.CountAsync(i => i.SubscriptionId == term.Id));
    }
}
