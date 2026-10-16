using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SmartSolar.Infrastructure.Persistence;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Builds the Installation Flow + knowledge base schema review SQL from the EF model (PostgreSQL provider, offline: no
/// connection is opened). Used to (1) check that guides/sql/installation-workflow-and-knowledge-base-schema-review.sql has not drifted
/// from the model and (2) create the tables in a disposable PostgreSQL test database.
/// It is never executed against a real database by application code.
/// </summary>
public static class PendingSchemaScript
{
    public static readonly string[] Namespaces =
    [
        "SmartSolar.Modules.Files", "SmartSolar.Modules.OnSiteSurveys", "SmartSolar.Modules.Quotations", "SmartSolar.Modules.CustomerContracts",
        "SmartSolar.Modules.Payments", "SmartSolar.Modules.Installations", "SmartSolar.Modules.KnowledgeBase",
    ];

    public const string GeneratedBegin = "-- <generated-from-ef-model>";
    public const string GeneratedEnd = "-- </generated-from-ef-model>";

    /// <summary>Constraints EF cannot express; applied after the generated tables.</summary>
    public const string Manual = """
        -- Installation crews: one technician can never be in two overlapping ACTIVE installation shifts.
        -- (Survey appointments are serialised against installation planning through the technician_profile row lock.)
        CREATE EXTENSION IF NOT EXISTS btree_gist;
        ALTER TABLE installation_shift_assignment
            ADD CONSTRAINT ex_installation_shift_assignment_no_overlap
            EXCLUDE USING gist (technician_user_id WITH =, tstzrange(starts_at, ends_at, '[)') WITH &&)
            WHERE (status = 'ACTIVE');
        """;

    public static IReadOnlyList<string> PendingTables()
    {
        using var db = NewModelContext();
        return db.Model.GetEntityTypes()
            .Where(e => Namespaces.Any(ns => e.ClrType.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true))
            .Select(e => e.GetTableName()!).Distinct().OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every statement of the EF create script that targets an Installation Flow / knowledge-base table:
    /// CREATE TABLE, CREATE [UNIQUE] INDEX and ALTER TABLE (EF emits foreign keys between tables that
    /// reference each other as separate ALTER TABLE … ADD CONSTRAINT statements). A statement that
    /// mentions a pending table but cannot be classified fails loudly instead of being dropped.
    /// </summary>
    public static string GenerateTables()
    {
        using var db = NewModelContext();
        var tables = PendingTables().ToHashSet(StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var statement in db.Database.GenerateCreateScript()
                     .Split(";" + Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                     .Select(s => s.Trim())
                     .Where(s => s.Length > 0))
        {
            var target = TargetTable(statement);
            if (target is null)
            {
                if (tables.Any(t => Regex.IsMatch(statement, $@"\b{t}\b")))
                    throw new InvalidOperationException($"Unclassified statement touching a pending table: {statement[..Math.Min(120, statement.Length)]}");
                continue;
            }
            if (tables.Contains(target)) sb.Append(statement.ReplaceLineEndings("\n")).Append(";\n\n");
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static readonly Regex[] TargetPatterns =
    [
        new(@"^CREATE TABLE ""?(?<t>\w+)""? \(", RegexOptions.Compiled),
        new(@"^CREATE (UNIQUE )?INDEX ""?\w+""? ON ""?(?<t>\w+)""?", RegexOptions.Compiled),
        new(@"^ALTER TABLE ""?(?<t>\w+)""? ", RegexOptions.Compiled),
    ];

    public static string? TargetTable(string statement)
        => TargetPatterns.Select(p => p.Match(statement)).FirstOrDefault(m => m.Success)?.Groups["t"].Value;

    /// <summary>The EF model used for review (PostgreSQL provider, never connected).</summary>
    public static IReadOnlyList<Microsoft.EntityFrameworkCore.Metadata.IEntityType> PendingEntityTypes()
    {
        using var db = NewModelContext();
        // The design-time model keeps check constraints and other annotations the runtime model drops.
        return db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => Namespaces.Any(ns => e.ClrType.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true)).ToList();
    }

    public static string ReviewFilePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartSolar.sln")) && !Directory.Exists(Path.Combine(dir.FullName, "guides")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repository root not found."), "guides", "sql", "installation-workflow-and-knowledge-base-schema-review.sql");
    }

    /// <summary>Generated section of the committed review file.</summary>
    public static string CommittedTables()
    {
        var text = File.ReadAllText(ReviewFilePath()).ReplaceLineEndings("\n");
        var start = text.IndexOf(GeneratedBegin, StringComparison.Ordinal);
        var end = text.IndexOf(GeneratedEnd, StringComparison.Ordinal);
        return text[(start + GeneratedBegin.Length)..end].Trim() + "\n";
    }

    private static AppDbContext NewModelContext()
        => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options);
}
