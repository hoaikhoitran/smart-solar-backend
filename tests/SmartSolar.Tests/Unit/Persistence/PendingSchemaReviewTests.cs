using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>The manual migration script must describe exactly what the EF model expects.</summary>
public sealed class PendingSchemaReviewTests
{
    [Fact]
    public void Review_script_matches_the_ef_model()
    {
        var expected = PendingSchemaScript.GenerateTables();
        var committed = PendingSchemaScript.CommittedTables();
        Assert.True(expected == committed,
            "guides/sql/installation-workflow-and-knowledge-base-schema-review.sql is out of date with the EF model. Regenerate its generated section from PendingSchemaScript.GenerateTables().");
    }

    [Fact]
    public void Review_script_only_creates_new_tables_and_never_alters_existing_ones()
    {
        var text = File.ReadAllText(PendingSchemaScript.ReviewFilePath());
        var tables = PendingSchemaScript.PendingTables();
        Assert.Contains("PENDING_MANUAL_MIGRATION", text);
        Assert.DoesNotContain("DROP TABLE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", text, StringComparison.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n').Where(l => l.TrimStart().StartsWith("ALTER TABLE", StringComparison.OrdinalIgnoreCase)))
            Assert.Contains(tables, t => line.Contains($"ALTER TABLE {t}", StringComparison.Ordinal) || line.Contains($"ALTER TABLE \"{t}\"", StringComparison.Ordinal));
        Assert.Equal(tables.Count, tables.Count(t => text.Contains($"CREATE TABLE {t} (", StringComparison.Ordinal) || text.Contains($"CREATE TABLE \"{t}\" (", StringComparison.Ordinal)));
    }

    /// <summary>
    /// Independent of the generator: every foreign key (with its delete rule), index and check
    /// constraint of the EF model must be present in the committed review script, on its own table.
    /// </summary>
    [Fact]
    public void Review_script_contains_every_foreign_key_index_and_check_of_the_model()
    {
        var statements = PendingSchemaScript.CommittedTables()
            .Split(";\n", StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0)
            .Select(x => (Table: PendingSchemaScript.TargetTable(x), Sql: x)).ToList();
        var missing = new List<string>();
        var foreignKeys = 0;
        foreach (var entity in PendingSchemaScript.PendingEntityTypes())
        {
            var table = entity.GetTableName()!;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            bool OnTable(string text) => statements.Any(x => x.Table == table && x.Sql.Contains(text, StringComparison.Ordinal));

            foreach (var fk in entity.GetForeignKeys())
            {
                foreignKeys++;
                var principal = fk.PrincipalEntityType.GetTableName()!;
                var principalStore = StoreObjectIdentifier.Table(principal, fk.PrincipalEntityType.GetSchema());
                var columns = string.Join(", ", fk.Properties.Select(p => p.GetColumnName(store)));
                var principalColumns = string.Join(", ", fk.PrincipalKey.Properties.Select(p => p.GetColumnName(principalStore)));
                var rule = fk.DeleteBehavior switch
                {
                    DeleteBehavior.Cascade => " ON DELETE CASCADE",
                    DeleteBehavior.Restrict => " ON DELETE RESTRICT",
                    DeleteBehavior.SetNull => " ON DELETE SET NULL",
                    _ => "",
                };
                var text = $"FOREIGN KEY ({columns}) REFERENCES {principal} ({principalColumns}){rule}";
                if (!OnTable(text)) missing.Add($"{table}: {text}");
            }
            foreach (var index in entity.GetIndexes())
            {
                var name = index.GetDatabaseName(store);
                if (!OnTable($"INDEX {name} ON {table} ") && !OnTable($"INDEX \"{name}\" ON {table} ")) missing.Add($"{table}: index {name}");
            }
            foreach (var check in entity.GetCheckConstraints())
                if (!OnTable($"CONSTRAINT {check.Name} CHECK")) missing.Add($"{table}: check {check.Name}");
        }

        Assert.True(foreignKeys >= 80, $"Expected the model's foreign keys to be enumerated, got {foreignKeys}.");
        Assert.True(missing.Count == 0, "Missing from the review script:\n" + string.Join("\n", missing));
    }
}
