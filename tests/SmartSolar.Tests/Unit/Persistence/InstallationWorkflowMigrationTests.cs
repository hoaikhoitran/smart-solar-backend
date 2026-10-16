using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Infrastructure.Persistence.Migrations;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Persistence;

/// <summary>
/// Guards the InstallationWorkflowAndKnowledgeBase migration without touching any database: it must create
/// exactly the reviewed schema, never alter existing tables, carry the manual exclusion constraint, roll
/// back cleanly, and the model snapshot must match the current EF model (no pending model changes).
/// </summary>
public sealed class InstallationWorkflowMigrationTests
{
    private static AppDbContext ModelContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);

    private static InstallationWorkflowAndKnowledgeBase Migration()
    {
        using var db = ModelContext();
        var migration = new InstallationWorkflowAndKnowledgeBase();
        migration.ActiveProvider = db.Database.ProviderName!;
        return migration;
    }

    [Fact]
    public void Up_creates_exactly_the_pending_tables_indexes_and_foreign_keys()
    {
        var up = Migration().UpOperations;
        var created = up.OfType<CreateTableOperation>().ToList();

        Assert.Equal(PendingSchemaScript.PendingTables().OrderBy(t => t), created.Select(t => t.Name).OrderBy(t => t));
        Assert.Equal(32, created.Count);
        Assert.Equal(105, up.OfType<CreateIndexOperation>().Count());
        // Foreign keys inside CREATE TABLE plus the ones EF adds afterwards to break table cycles
        // (knowledge_document <-> knowledge_document_version, chat_citation).
        var foreignKeys = created.SelectMany(t => t.ForeignKeys).Concat(up.OfType<AddForeignKeyOperation>()).ToList();
        Assert.Equal(83, foreignKeys.Count);
        Assert.Equal(2, foreignKeys.Count(f => f.OnDelete == ReferentialAction.Cascade));
        Assert.All(foreignKeys.Where(f => f.OnDelete != ReferentialAction.Cascade), f => Assert.Equal(ReferentialAction.Restrict, f.OnDelete));
        var newTables = created.Select(t => t.Name).ToHashSet();
        Assert.All(up.OfType<AddForeignKeyOperation>(), f => Assert.Contains(f.Table, newTables));
        Assert.All(up.OfType<CreateIndexOperation>(), i => Assert.Contains(i.Table, newTables));
        // Nothing else: no ALTER/DROP/RENAME of existing tables, only the manual SQL below.
        Assert.Empty(up.Where(o => o is not (CreateTableOperation or CreateIndexOperation or AddForeignKeyOperation or SqlOperation)));
        Assert.DoesNotContain(created, t => t.Name == "knowledge_chunk");
    }

    [Fact]
    public void Up_adds_the_manual_exclusion_constraint_exactly_as_reviewed()
    {
        var sql = string.Join("\n", Migration().UpOperations.OfType<SqlOperation>().Select(o => o.Sql));
        static string Normalise(string text) => System.Text.RegularExpressions.Regex.Replace(
            string.Join(" ", text.Split('\n').Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal))), @"\s+", " ").Trim();

        Assert.Equal(Normalise(PendingSchemaScript.Manual), Normalise(sql));
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS btree_gist", sql);
        Assert.DoesNotContain("vector", sql, StringComparison.OrdinalIgnoreCase);   // knowledge_chunk stays with the RAG service
    }

    [Fact]
    public void Down_drops_only_the_new_tables_and_the_exclusion_constraint()
    {
        var down = Migration().DownOperations;

        Assert.Equal(PendingSchemaScript.PendingTables().OrderBy(t => t), down.OfType<DropTableOperation>().Select(d => d.Name).OrderBy(t => t));
        var sql = Assert.Single(down.OfType<SqlOperation>()).Sql;
        Assert.Equal("ALTER TABLE installation_shift_assignment DROP CONSTRAINT IF EXISTS ex_installation_shift_assignment_no_overlap;", sql);
        Assert.All(down.OfType<DropForeignKeyOperation>(), f => Assert.Contains(f.Table, PendingSchemaScript.PendingTables()));
        Assert.Empty(down.Where(o => o is not (DropTableOperation or DropForeignKeyOperation or SqlOperation)));
        Assert.DoesNotContain("btree_gist", sql);   // the extension stays: Flow 1's exclusion constraint uses it
    }

    [Fact]
    public void The_model_snapshot_matches_the_current_model()
    {
        using var db = ModelContext();
        // The generated snapshot is internal; EF's own tooling also creates it by reflection.
        var snapshot = (ModelSnapshot)Activator.CreateInstance(
            typeof(AppDbContext).Assembly.GetTypes().Single(t => typeof(ModelSnapshot).IsAssignableFrom(t) && !t.IsAbstract), nonPublic: true)!;
        IModel snapshotModel = snapshot.Model;
        if (snapshotModel is IMutableModel mutable) snapshotModel = mutable.FinalizeModel();
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel, designTime: true);
        var current = db.GetService<IDesignTimeModel>().Model;

        var differences = db.GetService<IMigrationsModelDiffer>().GetDifferences(snapshotModel.GetRelationalModel(), current.GetRelationalModel());

        Assert.True(differences.Count == 0, "Pending model changes: " + string.Join(", ", differences.Select(d => d.GetType().Name)));
    }
}
