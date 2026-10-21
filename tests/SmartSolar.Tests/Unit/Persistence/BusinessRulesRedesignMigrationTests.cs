using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Migrations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Guards the BusinessRulesFlow1And2Redesign migration without touching any database. It must be NON-DESTRUCTIVE (no table
/// or column is dropped; obsolete ones stay for the approval-gated cleanup), run the data mapping between the schema
/// additions and the new check constraints, add the installation_assignment exclusion constraint, and its Down must refuse
/// data the previous schema cannot represent instead of losing it.
/// </summary>
public sealed class BusinessRulesRedesignMigrationTests
{
    private static BusinessRulesFlow1And2Redesign Migration()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
        return new BusinessRulesFlow1And2Redesign { ActiveProvider = db.Database.ProviderName! };
    }

    private static readonly string[] NewTables =
    [
        "installation_assignment", "installation_evidence", "product_image", "rag_configuration", "selected_simulation",
        "service_area", "survey_request_event", "technician_service_area",
    ];

    [Fact]
    public void Up_never_drops_a_table_or_a_column()
    {
        var up = Migration().UpOperations;

        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<RenameTableOperation>());
        Assert.Empty(up.OfType<RenameColumnOperation>());
        Assert.Equal(NewTables.Order(), up.OfType<CreateTableOperation>().Select(t => t.Name).Order());
        // The only column change on an existing column: the capacity holder becomes nullable (escalated without a holder).
        var altered = Assert.Single(up.OfType<AlterColumnOperation>());
        Assert.Equal(("technician_appointment", "reserved_technician_user_id", true), (altered.Table, altered.Name, altered.IsNullable));
        // Added columns are all nullable: existing rows stay valid until the data mapping runs.
        Assert.All(up.OfType<AddColumnOperation>(), c => Assert.True(c.IsNullable, $"{c.Table}.{c.Name} must be nullable"));
        // The one dropped foreign key is re-pointed at the compact simulation summary.
        Assert.Equal("fk_pre_survey_selected_simulation_same_pre_survey", Assert.Single(up.OfType<DropForeignKeyOperation>()).Name);
        Assert.Contains(up.OfType<AddForeignKeyOperation>(), f => f.Name == "fk_pre_survey_selected_simulation" && f.PrincipalTable == "selected_simulation");
        // No cascading deletes are introduced.
        Assert.All(up.OfType<AddForeignKeyOperation>(), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
        Assert.All(up.OfType<CreateTableOperation>().SelectMany(t => t.ForeignKeys), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
    }

    [Fact]
    public void Obsolete_tables_stay_in_the_database()
    {
        var up = Migration().UpOperations;
        var touched = up.Select(o => o switch
        {
            CreateTableOperation c => c.Name,
            DropTableOperation d => d.Name,
            ITableMigrationOperation t => t.Table,
            _ => null,
        }).Where(t => t is not null).ToHashSet();

        Assert.All(PendingSchemaScript.RetainedObsoleteTables, t => Assert.DoesNotContain(t, touched));
        Assert.All(PendingSchemaScript.RetainedObsoleteTables, t => Assert.DoesNotContain(t, PendingSchemaScript.ModelTables()));
    }

    [Fact]
    public void Data_mapping_runs_after_every_new_column_and_table_and_before_any_new_check_constraint()
    {
        var up = Migration().UpOperations.ToList();
        var sql = up.OfType<SqlOperation>().ToList();
        Assert.Equal(2, sql.Count);
        var data = up.IndexOf(sql[0]);
        var manual = up.IndexOf(sql[1]);

        Assert.Equal(BusinessRulesFlow1And2Redesign.UpDataSql, sql[0].Sql);
        Assert.All(up.Take(data), o => Assert.IsNotType<AddCheckConstraintOperation>(o));
        Assert.All(up.Skip(data), o => Assert.False(o is AddColumnOperation or CreateTableOperation or AlterColumnOperation));
        // The re-pointed FK is only added after the summaries were inserted.
        Assert.True(up.FindIndex(o => o is AddForeignKeyOperation { Name: "fk_pre_survey_selected_simulation" }) > data);
        Assert.Equal(up.Count - 1, manual);
        Assert.Equal(BusinessRulesFlow1And2Redesign.UpManualSql, sql[1].Sql);

        foreach (var text in new[] { "'PENDING_ACCEPTANCE'", "'LEGACY_SALES_ASSIGNMENT'", "INSERT INTO selected_simulation", "INSERT INTO product_image",
                     "status = 'Pending' WHERE status IN ('Assigned', 'Reviewing')", "RAISE EXCEPTION",
                     // A legacy request whose appointment is already confirmed becomes Scheduled, not Pending.
                     "SET status = 'Scheduled'" })
            Assert.Contains(text, BusinessRulesFlow1And2Redesign.UpDataSql);
        foreach (var forbidden in new[] { "DROP TABLE", "DROP COLUMN", "TRUNCATE", "DELETE FROM" })
            Assert.DoesNotContain(forbidden, BusinessRulesFlow1And2Redesign.UpDataSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ex_installation_assignment_no_overlap", BusinessRulesFlow1And2Redesign.UpManualSql);
        Assert.Contains("WHERE (status IN ('PENDING', 'ACCEPTED'))", BusinessRulesFlow1And2Redesign.UpManualSql);
    }

    [Fact]
    public void Down_refuses_unrepresentable_data_first_and_never_recreates_the_retained_tables()
    {
        var down = Migration().DownOperations.ToList();
        var guard = Assert.IsType<SqlOperation>(down[0]);

        Assert.Equal(BusinessRulesFlow1And2Redesign.DownGuardSql, guard.Sql);
        Assert.Contains("reserved_technician_user_id IS NULL", guard.Sql);
        Assert.Contains("installation_assignment", guard.Sql);
        Assert.Contains("solar_simulation", guard.Sql);
        Assert.Equal(NewTables.Order(), down.OfType<DropTableOperation>().Select(d => d.Name).Order());
        Assert.Empty(down.OfType<CreateTableOperation>());
        Assert.DoesNotContain(down.OfType<CreateIndexOperation>(), i => PendingSchemaScript.RetainedObsoleteTables.Contains(i.Table));
        // Statuses are mapped back before the previous check constraints and the NOT NULL holder are restored.
        var data = down.FindIndex(o => o is SqlOperation s && s.Sql == BusinessRulesFlow1And2Redesign.DownDataSql);
        Assert.True(data > 0);
        Assert.True(down.FindIndex(o => o is AlterColumnOperation) > data);
        Assert.All(down.OfType<AddCheckConstraintOperation>(), c => Assert.True(down.IndexOf(c) > data));
    }
}
