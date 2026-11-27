using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// MIGRATION 20261124173442_PerPanelServicePackages and the per-panel rules on a disposable PostgreSQL database (never Supabase / production): Up–Down–Up,
/// historical rows keep their meaning, Down refuses while per-panel data exists, check / unique / exclusion constraints on real data, and concurrent
/// purchases of the same panel unit (TC18/TC22 under real row locks).
/// </summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class PostgresPerPanelPackageTests
{
    private const string Previous = "20261123140208_SharedKnowledgePublicationScope";
    private const string Migration = "20261124173442_PerPanelServicePackages";
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<T> ScalarAsync<T>(AppDbContextFactory db, string sql) => await db().Database.SqlQueryRaw<T>(sql).SingleAsync();

    private delegate SmartSolar.Infrastructure.Persistence.AppDbContext AppDbContextFactory();

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        try
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    [PostgresFact]
    public async Task MIGRATION_up_down_up_keeps_existing_rows_with_their_historical_meaning()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(upToMigration: Previous);
        await using (var before = database.NewContext())
        {
            Assert.Equal([Migration, "20261125094516_SchedulingEffectiveBusyAfterCompletion", "20261125104043_SchedulingOvertimeConflicts", "20261127021802_TechnicianCashReceipts", "20261127124918_UnifiedCashReceipts"], await before.Database.GetPendingMigrationsAsync());
            // A package of the previous schema (no pricing unit column yet).
            await before.Database.ExecuteSqlRawAsync("""
                INSERT INTO service_package (id, kind, name, price, duration_months, visit_months, is_active, created_at, updated_at, revision)
                VALUES ('11111111-1111-1111-1111-111111111111', 'MAINTENANCE', 'Legacy care', 2000000, 12, '6,12', true, now(), now(), 0)
                """);
        }

        await using (var up = database.NewContext()) await up.Database.MigrateAsync();
        AppDbContextFactory db = database.NewContext;
        Assert.Equal("PER_INSTALLATION_LEGACY", await ScalarAsync<string>(db, "SELECT pricing_unit AS \"Value\" FROM service_package"));
        Assert.Equal(3, await ScalarAsync<int>(db, """
            SELECT count(*)::int AS "Value" FROM pg_class
            WHERE relname IN ('quotation_service_package', 'contract_service_package', 'service_coverage_allocation') AND relrowsecurity
            """));
        Assert.Contains("coverage_scope", await ScalarAsync<string>(db,
            "SELECT pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conname = 'ex_service_subscription_no_overlap'"));
        Assert.Equal("'INSTALLATION'::character varying", await ScalarAsync<string>(db,
            "SELECT column_default AS \"Value\" FROM information_schema.columns WHERE table_name = 'service_subscription' AND column_name = 'coverage_scope'"));
        Assert.Equal("0", (await ScalarAsync<string>(db,
            "SELECT column_default AS \"Value\" FROM information_schema.columns WHERE table_name = 'quotation' AND column_name = 'service_packages_total'")).Split('.')[0]);

        // Down (no per-panel data yet) restores the previous exclusion; Up again.
        await using (var down = database.NewContext()) await down.GetService<IMigrator>().MigrateAsync(Previous);
        Assert.False(await ScalarAsync<bool>(db, "SELECT to_regclass('contract_service_package') IS NOT NULL AS \"Value\""));
        Assert.DoesNotContain("coverage_scope", await ScalarAsync<string>(db,
            "SELECT pg_get_constraintdef(oid) AS \"Value\" FROM pg_constraint WHERE conname = 'ex_service_subscription_no_overlap'"));
        Assert.Equal("Legacy care", await ScalarAsync<string>(db, "SELECT name AS \"Value\" FROM service_package"));
        await using (var again = database.NewContext()) await again.Database.MigrateAsync();
        Assert.Contains(Migration, await database.NewContext().Database.GetAppliedMigrationsAsync());
        Assert.Empty(await database.NewContext().Database.GetPendingMigrationsAsync());
        Assert.Equal("PER_INSTALLATION_LEGACY", await ScalarAsync<string>(db, "SELECT pricing_unit AS \"Value\" FROM service_package"));
    }

    [PostgresFact]
    public async Task The_bundled_flow_runs_on_postgres_and_constraints_hold_on_real_data_and_down_is_refused()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var after = AfterSalesTestContext.CreateOn(database.NewContext());
        var (w, flow, projectId, bundled) = await PerPanelServicePackageTests.ProvisionedAsync(after);
        AppDbContextFactory db = database.NewContext;

        Assert.Equal(57_000_000m, await ScalarAsync<decimal>(db, $"SELECT total_amount AS \"Value\" FROM customer_contract WHERE id = '{flow.ContractId}'"));
        Assert.Equal((CoverageScopes.Panel, 10), (bundled.CoverageScope, bundled.CoveredUnits!.Value));
        var commitmentId = await ScalarAsync<Guid>(db, $"SELECT id AS \"Value\" FROM contract_service_package WHERE contract_id = '{flow.ContractId}'");

        // At most one subscription per commitment (a second provisioning is impossible even by hand).
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database, $"""
            INSERT INTO service_subscription (id, customer_user_id, contract_id, project_id, package_id, kind, status, package_name, price, duration_months,
                covered_disaster_types, coverage_percent, per_incident_limit, term_limit, coverage_scope, unit_price, covered_units, contract_service_package_id,
                paid_at, support_reserved, support_consumed, created_at, updated_at, revision)
            SELECT gen_random_uuid(), customer_user_id, contract_id, project_id, package_id, kind, 'PAID', package_name, price, duration_months, covered_disaster_types,
                coverage_percent, per_incident_limit, term_limit, coverage_scope, unit_price, covered_units, contract_service_package_id, paid_at, 0, 0, now(), now(), 0
            FROM service_subscription WHERE id = '{bundled.Id}'
            """));
        // Money invariants are database rules too.
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE contract_service_package SET deposit_allocation = deposit_allocation + 1 WHERE id = '{commitmentId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE customer_contract SET service_packages_total = service_packages_total + 1 WHERE id = '{flow.ContractId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database, $"UPDATE service_subscription SET covered_units = 11 WHERE id = '{bundled.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE contract_service_package SET status = 'COMMITTED' WHERE id = '{commitmentId}'"));   // a provisioned commitment keeps its subscription

        // Down is refused while per-panel data exists; the schema stays.
        await using (var down = database.NewContext())
            Assert.Contains("per-panel package data exists", (await Record.ExceptionAsync(() => down.GetService<IMigrator>().MigrateAsync(Previous)))?.ToString());
        Assert.Equal(Migration, (await database.NewContext().Database.GetAppliedMigrationsAsync()).Last());

        // Per-panel terms of one kind on different panels may overlap in time (the installation-wide exclusion does not apply to PANEL scope).
        var panels = await w.Ctx.Db.InstalledAssets.AsNoTracking().Where(a => a.ProjectId == projectId && a.AssetType == AssetTypes.Panel).ToListAsync();
        foreach (var panel in panels.Take(2))
        {
            var bought = await w.After.Packages().PurchaseAsync(flow.CustomerId, new PurchaseInput(flow.ContractId, w.Maintenance.Id, null, false, null, 1,
                [new AssetUnitsInput(panel.Id, 1)]), None);
            Assert.True(bought.Succeeded, bought.Message);
            await w.After.PayAsync(bought.Value!.Checkout);
        }
        Assert.Equal(2, await ScalarAsync<int>(db,
            "SELECT count(*)::int AS \"Value\" FROM service_subscription WHERE kind = 'MAINTENANCE' AND coverage_scope = 'PANEL' AND status = 'ACTIVE'"));
    }

    [PostgresFact]
    public async Task TC18_TC22_concurrent_purchases_of_the_same_panel_unit_sell_it_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var after = AfterSalesTestContext.CreateOn(database.NewContext());
        var (w, flow, projectId, _) = await PerPanelServicePackageTests.ProvisionedAsync(after);
        var panel = await w.Ctx.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == projectId && a.AssetType == AssetTypes.Panel);

        for (var round = 0; round < 3; round++)
        {
            var contexts = Enumerable.Range(0, 4).Select(_ => database.NewContext()).ToList();
            var attempts = await Task.WhenAll(contexts.Select(c => Task.Run(() => w.After.Packages(c).PurchaseAsync(flow.CustomerId,
                new PurchaseInput(flow.ContractId, w.Maintenance.Id, null, false, null, 1, [new AssetUnitsInput(panel.Id, 1)]), None))));
            foreach (var c in contexts) await c.DisposeAsync();
            if (round == 0) Assert.Single(attempts, a => a.Succeeded);
            Assert.All(attempts.Where(a => !a.Succeeded), a => Assert.Contains(a.ErrorCode, new[] { AfterSalesErrorCodes.TermOverlap, "PAYMENT_CHECKOUT_IN_PROGRESS" }));
        }
        await using var check = database.NewContext();
        Assert.Equal(1, await check.ServiceCoverageAllocations.CountAsync(a => a.InstalledAssetId == panel.Id && a.Kind == ServicePackageKinds.Maintenance));
        Assert.Equal(1, await check.ServiceSubscriptions.CountAsync(s => s.Kind == ServicePackageKinds.Maintenance && s.Status == SubscriptionStatuses.AwaitingPayment));
    }

    [PostgresFact]
    public async Task Concurrent_installation_wide_and_per_panel_purchases_of_one_kind_never_both_cover_the_panels()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var after = AfterSalesTestContext.CreateOn(database.NewContext());
        var (w, flow, projectId, _) = await PerPanelServicePackageTests.ProvisionedAsync(after);
        var legacy = await after.PackageAsync(w.AdminId, ServicePackageKinds.Maintenance, 2_000_000m);
        var panel = await w.Ctx.Db.InstalledAssets.AsNoTracking().FirstAsync(a => a.ProjectId == projectId && a.AssetType == AssetTypes.Panel);

        var contexts = Enumerable.Range(0, 4).Select(_ => database.NewContext()).ToList();
        var attempts = await Task.WhenAll(contexts.Select((c, i) => Task.Run(() => w.After.Packages(c).PurchaseAsync(flow.CustomerId, i % 2 == 0
            ? new PurchaseInput(flow.ContractId, legacy.Id, null, false)
            : new PurchaseInput(flow.ContractId, w.Maintenance.Id, null, false, null, 1, [new AssetUnitsInput(panel.Id, 1)]), None))));
        foreach (var c in contexts) await c.DisposeAsync();
        Assert.Contains(attempts, a => a.Succeeded);
        Assert.All(attempts.Where(a => !a.Succeeded), a => Assert.Contains(a.ErrorCode, new[] { AfterSalesErrorCodes.TermOverlap, "PAYMENT_CHECKOUT_IN_PROGRESS" }));
        foreach (var bought in attempts.Where(a => a.Succeeded)) await w.After.PayAsync(bought.Value!.Checkout);

        // Whatever won the race, the panels are never covered by both an installation-wide and a per-panel maintenance term.
        await using var check = database.NewContext();
        var scopes = await check.ServiceSubscriptions.AsNoTracking()
            .Where(s => s.Kind == ServicePackageKinds.Maintenance && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Paid))
            .Select(s => s.CoverageScope).Distinct().ToListAsync();
        Assert.Single(scopes);
    }

    [PostgresFact]
    public async Task TC17_TC18_webhook_and_reconciliation_racing_on_the_balance_apply_the_money_and_provision_exactly_once()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var after = AfterSalesTestContext.CreateOn(database.NewContext());
        var (w, flow) = await PerPanelServicePackageTests.AgreedBundleAsync(on: after);
        var ctx = w.Ctx;
        var projectId = await PerPanelServicePackageTests.CompletedWithAssetsAsync(w, flow, (panel, inverter) => PerPanelServicePackageTests.Serialised(panel, inverter, 10));
        await PaymentPlanFlow.AcceptAsync(ctx, flow, projectId);
        var opened = await ctx.InstallmentPayments().StartAsync(InstallmentPayer.Customer, flow.CustomerId, flow.ContractId, 2, "ONLINE", None);
        Assert.True(opened.Succeeded, opened.Message);
        var qr = opened.Value!.Current!;
        ctx.PayOs.Links[qr.OrderCode] = ("PAID", (long)qr.Amount, $"FT-RACE-{qr.OrderCode}");
        var body = ctx.PayOs.Webhook(qr.OrderCode, (long)qr.Amount, $"FT-RACE-{qr.OrderCode}", transactionAt: ctx.Clock.GetUtcNow());

        var contexts = Enumerable.Range(0, 4).Select(_ => database.NewContext()).ToList();
        await Task.WhenAll(
            Task.Run(() => ctx.PaymentsWithSchedule(contexts[0]).HandleWebhookAsync(body, None)),
            Task.Run(() => ctx.PaymentsWithSchedule(contexts[1]).HandleWebhookAsync(body, None)),
            Task.Run(() => ctx.PaymentsWithSchedule(contexts[2]).ReconcileAsync(qr.PaymentAttemptId, true, None)),
            Task.Run(() => ctx.PaymentsWithSchedule(contexts[3]).ReconcileAsync(qr.PaymentAttemptId, true, None)));
        foreach (var c in contexts) await c.DisposeAsync();

        await using var check = database.NewContext();
        Assert.Equal(ContractStatuses.Paid, (await check.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == flow.ContractId)).Status);
        Assert.Equal(1, await check.ServiceSubscriptions.CountAsync());
        Assert.Equal(10, await check.ServiceCoverageAllocations.SumAsync(a => a.UnitCount));
        Assert.Equal(57_000_000m, (await check.PaymentAttempts.AsNoTracking().Where(a => a.Status == "PAID" && a.CustomerUserId == flow.CustomerId).ToListAsync()).Sum(a => a.Amount));
        Assert.Equal(1, await check.PaymentAttemptItems.CountAsync(i => i.ContractInstallmentId != null && i.Status == "SETTLED" && i.PaymentAttemptId == qr.PaymentAttemptId));
    }
}
