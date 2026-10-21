using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Migrations;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Guards the AfterSalesServices migration without touching any database: additive only (no drop or rename), RESTRICT foreign keys,
/// row-level security on every new table, the subscription term exclusion constraint, and a Down that refuses data the previous
/// schema cannot represent (service-only payments have no contract).
/// </summary>
public sealed class AfterSalesServicesMigrationTests
{
    private static readonly string[] NewTables =
    [
        "after_sales_event", "after_sales_notification", "disaster_claim", "maintenance_entitlement", "payment_attempt_item", "refund_record", "sales_task",
        "service_evidence", "service_item", "service_package", "service_quotation", "service_request", "service_subscription", "service_visit", "warranty_option",
    ];

    private static AfterSalesServices Migration()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
        return new AfterSalesServices { ActiveProvider = db.Database.ProviderName! };
    }

    [Fact]
    public void Up_is_additive_and_only_relaxes_the_payment_contract_reference()
    {
        var up = Migration().UpOperations;

        Assert.Empty(up.OfType<DropTableOperation>());
        Assert.Empty(up.OfType<DropColumnOperation>());
        Assert.Empty(up.OfType<RenameTableOperation>());
        Assert.Empty(up.OfType<RenameColumnOperation>());
        Assert.Empty(up.OfType<DropForeignKeyOperation>());
        Assert.Equal(NewTables, up.OfType<CreateTableOperation>().Select(t => t.Name).Order());
        Assert.Equal(new[] { ("payment_attempt", "contract_id", true), ("payment_event", "contract_id", true) },
            up.OfType<AlterColumnOperation>().Select(c => (c.Table, c.Name, c.IsNullable)).Order());
        // Existing rows stay valid: every added column is nullable or has a default.
        Assert.All(up.OfType<AddColumnOperation>(), c => Assert.True(c.IsNullable || c.DefaultValue is not null || c.DefaultValueSql is not null, $"{c.Table}.{c.Name}"));
        Assert.All(up.OfType<AddForeignKeyOperation>(), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
        Assert.All(up.OfType<CreateTableOperation>().SelectMany(t => t.ForeignKeys), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
    }

    [Fact]
    public void Up_enables_row_level_security_and_the_term_exclusion_constraint()
    {
        var sql = string.Join("\n", Migration().UpOperations.OfType<SqlOperation>().Select(o => o.Sql));

        Assert.All(NewTables, t => Assert.Contains($"ALTER TABLE public.{t} ENABLE ROW LEVEL SECURITY;", sql));
        Assert.Contains("ex_service_subscription_no_overlap", sql);
        Assert.Contains("EXCLUDE USING gist", sql);
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS btree_gist;", sql);
    }

    [Fact]
    public void Up_prevents_duplicate_payment_lines_and_double_settlement()
    {
        var indexes = Migration().UpOperations.OfType<CreateIndexOperation>().Where(i => i.Table == "payment_attempt_item" && i.IsUnique)
            .ToDictionary(i => i.Name, i => i.Filter);

        Assert.Equal("contract_id IS NOT NULL", indexes["ux_payment_attempt_item_attempt_contract"]);
        Assert.Equal("subscription_id IS NOT NULL", indexes["ux_payment_attempt_item_attempt_subscription"]);
        Assert.Equal("quotation_id IS NOT NULL", indexes["ux_payment_attempt_item_attempt_quotation"]);
        Assert.Equal("subscription_id IS NOT NULL AND status = 'SETTLED'", indexes["ux_payment_attempt_item_settled_subscription"]);
        Assert.Equal("quotation_id IS NOT NULL AND status = 'SETTLED'", indexes["ux_payment_attempt_item_settled_quotation"]);
        // A CONTRACT line must name its own checkout's contract, so the one-paid index on payment_attempt covers combined checkouts.
        Assert.Contains("FOREIGN KEY (payment_attempt_id, contract_id) REFERENCES payment_attempt (id, contract_id)", AfterSalesServices.UpManualSql);
    }

    [Fact]
    public void Up_enforces_the_stored_checkout_and_request_references()
    {
        var keys = Migration().UpOperations.OfType<AddForeignKeyOperation>().ToDictionary(f => f.Name, f => (f.Table, f.Columns.Single(), f.PrincipalTable));

        Assert.Equal(("service_subscription", "current_payment_attempt_id", "payment_attempt"), keys["fk_service_subscription_current_payment_attempt"]);
        Assert.Equal(("service_quotation", "current_payment_attempt_id", "payment_attempt"), keys["fk_service_quotation_current_payment_attempt"]);
        Assert.Equal(("maintenance_entitlement", "current_request_id", "service_request"), keys["fk_maintenance_entitlement_current_request"]);
    }

    [Fact]
    public void Down_first_refuses_payments_without_a_contract()
    {
        var down = Migration().DownOperations.ToList();

        var guard = Assert.IsType<SqlOperation>(down[0]);
        Assert.Equal(AfterSalesServices.DownGuardSql, guard.Sql);
        Assert.Contains("contract_id IS NULL", guard.Sql);
        Assert.Contains("RAISE EXCEPTION", guard.Sql);
        Assert.Equal(AfterSalesServices.DownManualSql, Assert.IsType<SqlOperation>(down[1]).Sql);
        // The entitlement -> request key is dropped before service_request (they reference each other).
        var dropKey = down.FindIndex(o => o is DropForeignKeyOperation { Name: "fk_maintenance_entitlement_current_request" });
        Assert.True(dropKey >= 0 && dropKey < down.FindIndex(o => o is DropTableOperation { Name: "service_request" }));
    }

    [Fact]
    public void Down_restores_contract_id_as_required_without_inventing_a_default()
    {
        var restored = Migration().DownOperations.OfType<AlterColumnOperation>().ToList();

        Assert.Equal(new[] { "payment_attempt", "payment_event" }, restored.Select(c => c.Table).Order());
        Assert.All(restored, c => Assert.Equal(("contract_id", false, (object?)null, (string?)null), (c.Name, c.IsNullable, c.DefaultValue, c.DefaultValueSql)));
    }
}
