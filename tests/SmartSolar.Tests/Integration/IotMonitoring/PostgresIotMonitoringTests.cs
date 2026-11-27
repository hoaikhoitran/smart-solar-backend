using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SmartSolar.Infrastructure.Persistence.IotMonitoring;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Integration.IotMonitoring;

/// <summary>
/// What only PostgreSQL proves: concurrent bindings decided by the partial unique indexes, the check constraints on raw rows, and the
/// IotEnergyMonitoring migration going down and up on a disposable database (never the application database).
/// </summary>
public sealed class PostgresIotMonitoringTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string PreviousMigration = "20261114130345_InstalledAssetLotsAndPackageImage";
    private const string IotMigration = "20261115153613_IotEnergyMonitoring";
    private const string OutboxMigration = "20261115153617_PasswordResetDispatchOutbox";
    private const string CommercialMigration = "20261117074228_SimplifiedCommercialFlow";
    private const string BookingMigration = "20261117092018_DirectBookingAndInstallationSchedule";
    private const string CompatibilityMigration = "20261117101818_SurveyRequestSiteCompatibility";
    private const string CostMigration = "20261117131913_CloudRagCostControl";
    private const string SlotOfferMigration = "20261117171006_InstallationSlotOffers";
    private const string TechnicianQuotationMigration = "20261117185657_AfterSalesTechnicianQuotation";
    private const string OnSiteSalesMigration = "20261118102011_TechnicianLedOnSiteSales";
    private const string PaymentPlanMigration = "20261118202100_PaymentPlan3070PaperSignatureCollection";
    private const string SharedKnowledgeMigration = "20261123140208_SharedKnowledgePublicationScope";
    private const string PerPanelMigration = "20261124173442_PerPanelServicePackages";
    private const string SchedulingMigration = "20261125094516_SchedulingEffectiveBusyAfterCompletion";
    private const string OvertimeMigration = "20261125104043_SchedulingOvertimeConflicts";
    private const string CashReceiptMigration = "20261127021802_TechnicianCashReceipts";
    private const string UnifiedCashReceiptMigration = "20261127124918_UnifiedCashReceipts";

    /// <summary>Two real handed-over-style journeys (paid contract + project) for two different customers.</summary>
    private static async Task<(Guid ProjectA, Guid CustomerA, Guid ProjectB, Guid CustomerB, Guid Manager)> TwoProjectsAsync(InstallationWorkflowTestContext w)
    {
        w.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        var a = await InstallationTests.PaidContractAsync(w);
        var projectA = (await w.Installations().CreateProjectAsync(a.ManagerId, a.ContractId, None)).Value!.ProjectId;
        w.Clock.Now = SchedulingTestContext.DefaultNow.ToUniversalTime();
        var b = await InstallationTests.PaidContractAsync(w);
        var projectB = (await w.Installations().CreateProjectAsync(b.ManagerId, b.ContractId, None)).Value!.ProjectId;
        return (projectA, a.CustomerId, projectB, b.CustomerId, a.ManagerId);
    }

    [PostgresFact]
    public async Task Concurrent_binds_create_exactly_one_active_binding_per_device_and_per_project()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        await using var iot = IotTestContext.CreateOn(database.NewContext());
        var (projectA, customerA, projectB, _, manager) = await TwoProjectsAsync(iot.Installation);
        var admin = await iot.AdminAsync();
        var d1 = await iot.DeviceAsync(admin, "ESP32-RACE-1");
        var d2 = await iot.DeviceAsync(admin, "ESP32-RACE-2");

        // One device, two projects at the same instant.
        var deviceRace = await PostgresRace.RaceAsync(database, 2, (db, i) =>
            new IotBindingHandlers(new IotMonitoringUnitOfWork(db), iot.Clock).BindAsync(manager, i == 0 ? projectA : projectB, d1.Code, null, false, None));
        Assert.Equal(1, deviceRace.Count(r => r.Succeeded));
        Assert.Equal(IotErrorCodes.DeviceAlreadyBound, deviceRace.Single(r => !r.Succeeded).ErrorCode);
        await using (var db = database.NewContext())
            Assert.Equal(1, await db.IotDeviceBindings.CountAsync(b => b.DeviceId == d1.Id && b.Status == IotBindingStatuses.Active));

        // Free the device, then two devices for one project at the same instant.
        var active = await iot.Bindings().ListAsync(null, d1.Code, IotBindingStatuses.Active, None);
        Assert.True((await iot.Bindings().ReleaseAsync(manager, active.Value!.Single().BindingId, "race reset", None)).Succeeded);
        var projectRace = await PostgresRace.RaceAsync(database, 2, (db, i) =>
            new IotBindingHandlers(new IotMonitoringUnitOfWork(db), iot.Clock).BindAsync(manager, projectA, i == 0 ? d1.Code : d2.Code, null, false, None));
        Assert.Equal(1, projectRace.Count(r => r.Succeeded));
        Assert.Equal(IotErrorCodes.ProjectAlreadyBound, projectRace.Single(r => !r.Succeeded).ErrorCode);
        await using (var db = database.NewContext())
            Assert.Equal(1, await db.IotDeviceBindings.CountAsync(b => b.ProjectId == projectA && b.Status == IotBindingStatuses.Active));

        // The whole flow on PostgreSQL: a reading is stored and read back by the customer.
        var bound = projectRace.Single(r => r.Succeeded).Value!;
        var secret = bound.DeviceId == d1.Code ? d1.Secret : d2.Secret;
        Assert.True((await iot.Telemetry().IngestAsync(bound.DeviceId, secret, 0.2145, None)).Succeeded);
        Assert.Equal(0.2145, (await iot.Telemetry().GetLatestAsync(customerA, false, projectA, None)).Value!.Reading!.CurrentAmps);
    }

    [PostgresFact]
    public async Task The_constraints_refuse_invalid_rows_written_outside_the_handlers()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        await using var iot = IotTestContext.CreateOn(database.NewContext());
        var (projectA, customerA, projectB, customerB, manager) = await TwoProjectsAsync(iot.Installation);
        var admin = await iot.AdminAsync();
        var d1 = await iot.DeviceAsync(admin, "ESP32-SQL");
        var bound = (await iot.Bindings().BindAsync(manager, projectA, d1.Code, null, false, None)).Value!;

        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"INSERT INTO iot_telemetry_reading (id, device_id, binding_id, current_amps, received_at) VALUES ('{Guid.NewGuid()}', '{d1.Id}', '{bound.BindingId}', 'NaN'::double precision, now())"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"INSERT INTO iot_telemetry_reading (id, device_id, binding_id, current_amps, received_at) VALUES ('{Guid.NewGuid()}', '{d1.Id}', '{bound.BindingId}', 'Infinity'::double precision, now())"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database,
            $"INSERT INTO iot_device_binding (id, device_id, project_id, customer_user_id, status, bound_at, bound_by_user_id) VALUES ('{Guid.NewGuid()}', '{d1.Id}', '{projectB}', '{customerB}', 'ACTIVE', now(), '{manager}')"));
        // A second ACTIVE device on the same project is refused by the database itself.
        var d2 = await iot.DeviceAsync(admin, "ESP32-SQL-2");
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database,
            $"INSERT INTO iot_device_binding (id, device_id, project_id, customer_user_id, status, bound_at, bound_by_user_id) VALUES ('{Guid.NewGuid()}', '{d2.Id}', '{projectA}', '{customerA}', 'ACTIVE', now(), '{manager}')"));
        // The customer column is not unique: the same customer may have another ACTIVE device on another project.
        Assert.Null(await SqlStateAsync(database,
            $"INSERT INTO iot_device_binding (id, device_id, project_id, customer_user_id, status, bound_at, bound_by_user_id) VALUES ('{Guid.NewGuid()}', '{d2.Id}', '{projectB}', '{customerA}', 'ACTIVE', now(), '{manager}')"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE iot_device_binding SET status = 'RELEASED' WHERE id = '{bound.BindingId}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE iot_device SET max_current_amps = 0 WHERE id = '{d1.Id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, await SqlStateAsync(database,
            $"UPDATE iot_device SET status = 'DISABLED' WHERE id = '{d1.Id}'"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, await SqlStateAsync(database,
            $"INSERT INTO iot_device (id, device_id, display_name, status, secret_hash, secret_version, secret_rotated_at, max_current_amps, created_by_user_id, created_at, updated_at, revision) VALUES ('{Guid.NewGuid()}', '{d1.Code}', 'dup', 'ACTIVE', 'x', 1, now(), 30, '{admin}', now(), now(), 1)"));
        Assert.Equal(0, await CountAsync(database, "iot_telemetry_reading"));
    }

    [PostgresFact]
    public async Task The_iot_migration_goes_down_and_up_on_a_disposable_database()
    {
        await using var database = await PostgresTestDatabase.CreateAsync(withPendingSchema: true);
        await using var db = database.NewContext();
        Assert.True(await TableExistsAsync(database, "iot_device"));

        await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        Assert.False(await TableExistsAsync(database, "iot_device"));
        Assert.False(await TableExistsAsync(database, "iot_device_binding"));
        Assert.False(await TableExistsAsync(database, "iot_telemetry_reading"));
        Assert.False(await TableExistsAsync(database, "password_reset_dispatch"));
        Assert.Equal([IotMigration, OutboxMigration, CommercialMigration, BookingMigration, CompatibilityMigration, CostMigration, SlotOfferMigration, TechnicianQuotationMigration, OnSiteSalesMigration, PaymentPlanMigration, SharedKnowledgeMigration, PerPanelMigration, SchedulingMigration, OvertimeMigration, CashReceiptMigration, UnifiedCashReceiptMigration], await db.Database.GetPendingMigrationsAsync());

        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.True(await TableExistsAsync(database, "iot_telemetry_reading"));
        Assert.True(await TableExistsAsync(database, "password_reset_dispatch"));
        Assert.Equal(UnifiedCashReceiptMigration, (await db.Database.GetAppliedMigrationsAsync()).Last());
    }

    private static async Task<string?> SqlStateAsync(PostgresTestDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private static async Task<long> CountAsync(PostgresTestDatabase database, string table)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TableExistsAsync(PostgresTestDatabase database, string table)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = @t)", connection);
        command.Parameters.AddWithValue("t", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
