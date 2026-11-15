using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Migrations;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Guards the IotEnergyMonitoring migration without touching any database: additive only (three new tables with their constraints and
/// indexes; nothing of the existing schema is altered), and a Down that only drops those tables.
/// </summary>
public sealed class IotEnergyMonitoringMigrationTests
{
    private static readonly string[] Tables = ["iot_device", "iot_device_binding", "iot_telemetry_reading"];

    private static IotEnergyMonitoring Migration()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
        return new IotEnergyMonitoring { ActiveProvider = db.Database.ProviderName! };
    }

    [Fact]
    public void Up_only_creates_the_three_iot_tables_with_their_constraints_and_indexes()
    {
        var up = Migration().UpOperations;

        Assert.Equal(Tables, up.OfType<CreateTableOperation>().Select(t => t.Name));
        Assert.Empty(up.Where(o => o is not CreateTableOperation and not CreateIndexOperation));
        Assert.All(up.OfType<CreateIndexOperation>(), i => Assert.Contains(i.Table, Tables));

        var device = up.OfType<CreateTableOperation>().Single(t => t.Name == "iot_device");
        Assert.Contains(device.CheckConstraints, c => c.Name == "ck_iot_device_status" && c.Sql == "status IN ('ACTIVE', 'DISABLED')");
        Assert.Contains(device.CheckConstraints, c => c.Name == "ck_iot_device_max_current");
        Assert.DoesNotContain(device.Columns, c => c.Name.Contains("secret") && c.Name != "secret_hash" && c.Name != "secret_version" && c.Name != "secret_rotated_at");
        Assert.Equal(64, device.Columns.Single(c => c.Name == "secret_hash").MaxLength);

        var binding = up.OfType<CreateTableOperation>().Single(t => t.Name == "iot_device_binding");
        Assert.Equal(new[] { "installation_project", "iot_device", "user_account" }, binding.ForeignKeys.Select(f => f.PrincipalTable).Distinct().Order());
        var indexes = up.OfType<CreateIndexOperation>().ToDictionary(i => i.Name, i => i);
        Assert.True(indexes["ux_iot_device_device_id"].IsUnique);
        Assert.Equal(("iot_device_binding", true, "status = 'ACTIVE'"), (indexes["ux_iot_device_binding_active_device"].Table, indexes["ux_iot_device_binding_active_device"].IsUnique, indexes["ux_iot_device_binding_active_device"].Filter));
        // One ACTIVE device per installation project (a customer may own several projects): no unique rule on the customer.
        var perProject = indexes["ux_iot_device_binding_active_project"];
        Assert.Equal(("iot_device_binding", true, "status = 'ACTIVE'"), (perProject.Table, perProject.IsUnique, perProject.Filter));
        Assert.Equal(new[] { "project_id" }, perProject.Columns);
        Assert.False(indexes["ix_iot_device_binding_customer"].IsUnique);
        Assert.DoesNotContain(indexes.Values, i => i.IsUnique && i.Columns.SequenceEqual(new[] { "customer_user_id" }));
        Assert.Equal(new[] { "binding_id", "received_at" }, indexes["ix_iot_telemetry_reading_binding_received_at"].Columns);
        Assert.Equal(new[] { "device_id", "received_at" }, indexes["ix_iot_telemetry_reading_device_received_at"].Columns);

        var reading = up.OfType<CreateTableOperation>().Single(t => t.Name == "iot_telemetry_reading");
        Assert.Equal("double precision", reading.Columns.Single(c => c.Name == "current_amps").ColumnType);
        Assert.Contains(reading.CheckConstraints, c => c.Name == "ck_iot_telemetry_reading_current" && c.Sql == "current_amps BETWEEN -10000 AND 10000");
        // Only what the firmware measures: no derived power / energy columns.
        Assert.Equal(new[] { "binding_id", "current_amps", "device_id", "id", "received_at" }, reading.Columns.Select(c => c.Name).Order());
    }

    [Fact]
    public void Down_only_drops_the_three_iot_tables()
    {
        var down = Migration().DownOperations;

        Assert.Equal(Tables.Order(), down.OfType<DropTableOperation>().Select(t => t.Name).Order());
        Assert.Empty(down.Where(o => o is not DropTableOperation));
    }
}
