using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Migrations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Guards the AdministrativeLocationsZonesDispatchAndSettings migration without touching any database. It must be
/// NON-DESTRUCTIVE: the legacy service-area tables, the district/ward columns and the service_area_id columns stay; the
/// zone columns are ADDED next to them (no rename). Every new table gets RLS (Supabase hardening policy), and Down drops
/// only what Up added, after refusing rows the previous schema cannot represent.
/// </summary>
public sealed class AdministrativeLocationsZonesMigrationTests
{
    private static AdministrativeLocationsZonesDispatchAndSettings Migration()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
        return new AdministrativeLocationsZonesDispatchAndSettings { ActiveProvider = db.Database.ProviderName! };
    }

    private static readonly string[] NewTables =
    [
        "administrative_catalog_import", "administrative_commune", "administrative_legacy_commune_map", "administrative_province",
        "cross_zone_authorization", "dispatch_event", "dispatch_notification", "dispatch_preferred_window", "dispatch_proposal",
        "dispatch_request", "operational_zone", "operational_zone_commune", "system_setting", "system_setting_revision",
        "system_settings_version", "technician_zone_membership",
    ];

    private static readonly (string Table, string Column)[] AddedColumns =
    [
        ("installation_project", "operational_zone_id"), ("property_site", "commune_code"),
        ("technician_appointment", "acceptance_reminder_sent_at"), ("technician_appointment", "acceptance_window_minutes"),
        ("technician_appointment", "cross_zone_authorization_id"), ("technician_appointment", "operational_zone_id"),
        ("technician_appointment", "settings_version"),
    ];

    [Fact]
    public void Up_never_drops_or_renames_a_table_column_or_foreign_key()
    {
        var up = Migration().UpOperations;

        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<DropForeignKeyOperation>());
        Assert.Empty(up.OfType<DropIndexOperation>());
        Assert.Empty(up.OfType<RenameTableOperation>());
        Assert.Empty(up.OfType<RenameColumnOperation>());
        Assert.Empty(up.OfType<RenameIndexOperation>());
        Assert.Empty(up.OfType<AlterColumnOperation>());
        Assert.Equal(NewTables.Order(), up.OfType<CreateTableOperation>().Select(t => t.Name).Order());
        Assert.Equal(AddedColumns.Order(), up.OfType<AddColumnOperation>().Select(c => (c.Table, c.Name)).Order());
        // Added columns are nullable: existing rows (sites without a commune, appointments without a zone) stay valid.
        Assert.All(up.OfType<AddColumnOperation>(), c => Assert.True(c.IsNullable, $"{c.Table}.{c.Name} must be nullable"));
        // No cascading deletes are introduced.
        Assert.All(up.OfType<AddForeignKeyOperation>(), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
        Assert.All(up.OfType<CreateTableOperation>().SelectMany(t => t.ForeignKeys), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
    }

    [Fact]
    public void Only_the_legacy_location_checks_and_the_widened_assignment_source_check_are_replaced()
    {
        var up = Migration().UpOperations;

        Assert.Equal(
            new[] { "ck_property_site_district_code", "ck_property_site_ward_code", "ck_technician_appointment_assignment_source" },
            up.OfType<DropCheckConstraintOperation>().Select(c => c.Name).Order());
        var source = Assert.Single(up.OfType<AddCheckConstraintOperation>(), c => c.Name == "ck_technician_appointment_assignment_source");
        Assert.Contains("'MANAGER_DISPATCH'", source.Sql);
        foreach (var kept in new[] { "'SELF_ACCEPTED'", "'SALES_ASSIGNED'", "'MANAGER_OVERRIDE'" })
            Assert.Contains(kept, source.Sql);
        Assert.Contains(up.OfType<AddCheckConstraintOperation>(), c => c.Name == "ck_property_site_commune_code");
    }

    [Fact]
    public void Legacy_service_area_objects_and_columns_stay_in_the_database()
    {
        var up = Migration().UpOperations;
        var touchedTables = up.Select(o => o switch
        {
            CreateTableOperation c => c.Name,
            ITableMigrationOperation t => t.Table,
            _ => null,
        }).Where(t => t is not null).ToHashSet();

        Assert.All(PendingSchemaScript.RetainedServiceAreaTables, t => Assert.DoesNotContain(t, touchedTables));
        Assert.All(PendingSchemaScript.RetainedServiceAreaTables, t => Assert.DoesNotContain(t, PendingSchemaScript.ModelTables()));
        Assert.DoesNotContain(up.OfType<ColumnOperation>(), c => c.Name is "service_area_id" or "district_code" or "ward_code");
    }

    [Fact]
    public void Every_new_table_gets_row_level_security()
    {
        var up = Migration().UpOperations.ToList();
        var rls = Assert.Single(up.OfType<SqlOperation>());

        Assert.Equal(up.Count - 1, up.IndexOf(rls));
        foreach (var table in NewTables)
            Assert.Contains($"ALTER TABLE public.{table} ENABLE ROW LEVEL SECURITY;", rls.Sql);
        // Supabase's default privileges grant new tables to the client roles: revoked at once when the roles exist.
        foreach (var role in new[] { "anon", "authenticated" })
        {
            Assert.Contains($"IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}')", rls.Sql);
            Assert.Contains($"FROM {role};", rls.Sql);
        }
        Assert.All(NewTables, t => Assert.Equal(2, CountOf(rls.Sql, $"public.{t},") + CountOf(rls.Sql, $"public.{t} FROM")));
        foreach (var forbidden in new[] { "DROP", "TRUNCATE", "DELETE", "DISABLE", "CREATE POLICY", " GRANT " })
            Assert.DoesNotContain(forbidden, rls.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void Down_refuses_manager_dispatch_rows_first_and_drops_only_what_up_added()
    {
        var down = Migration().DownOperations.ToList();
        var guard = Assert.IsType<SqlOperation>(down[0]);

        Assert.Equal(AdministrativeLocationsZonesDispatchAndSettings.DownGuardSql, guard.Sql);
        Assert.Contains("assignment_source = 'MANAGER_DISPATCH'", guard.Sql);
        Assert.Contains("RAISE EXCEPTION", guard.Sql);
        Assert.Equal(NewTables.Order(), down.OfType<DropTableOperation>().Select(d => d.Name).Order());
        Assert.Equal(AddedColumns.Order(), down.OfType<DropColumnOperation>().Select(c => (c.Table, c.Name)).Order());
        Assert.Empty(down.OfType<CreateTableOperation>());
        Assert.Empty(down.OfType<AddColumnOperation>());
        Assert.Empty(down.OfType<RenameColumnOperation>());
        Assert.DoesNotContain(down.OfType<CreateIndexOperation>(), i => PendingSchemaScript.RetainedServiceAreaTables.Contains(i.Table));
        Assert.DoesNotContain(down.OfType<AddForeignKeyOperation>(), f => f.PrincipalTable is "service_area");
    }
}
