using Microsoft.EntityFrameworkCore;
using Npgsql;
using SmartSolar.Api.Cli;
using SmartSolar.Infrastructure.Persistence.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.AdministrativeUnits;

/// <summary>
/// The controlled catalog import against a disposable PostgreSQL database (needs the migration that creates the
/// administrative_* tables): CLI dry run (nothing written), apply in one transaction, idempotent re-apply, and the database
/// checks (digit formats, match key, the composite site → commune FK).
/// </summary>
public sealed class PostgresAdministrativeCatalogImportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [PostgresFact]
    public async Task Dry_run_apply_and_idempotent_re_apply_through_the_cli()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var target = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        string[] confirm = ["--confirm-host", target.Host!, "--confirm-database", target.Database!, "--confirm-user", target.Username!];
        var dataset = AdministrativeCatalogTestData.DatasetDirectory;

        var dryOutput = new StringWriter();
        var dry = AdministrativeCatalogCli.Parse(["admin-catalog", "import", "--dataset", dataset, .. confirm]).Options!;
        Assert.Equal(0, await AdministrativeCatalogCli.RunAsync(dry, database.ConnectionString, dryOutput, TimeProvider.System, None));
        Assert.Contains("communes: 168 new", dryOutput.ToString());
        Assert.Contains("Dry run: nothing was written", dryOutput.ToString());
        Assert.DoesNotContain(new NpgsqlConnectionStringBuilder(database.ConnectionString).Password ?? "\u0000", dryOutput.ToString());
        await using (var db = database.NewContext())
        {
            Assert.Equal(0, await db.AdministrativeCommunes.CountAsync());
            Assert.Equal(0, await db.AdministrativeCatalogImports.CountAsync());
        }

        var apply = AdministrativeCatalogCli.Parse(["admin-catalog", "import", "--dataset", dataset, .. confirm, "--apply", "--operator", "ops"]).Options!;
        var applyOutput = new StringWriter();
        Assert.Equal(0, await AdministrativeCatalogCli.RunAsync(apply, database.ConnectionString, applyOutput, TimeProvider.System, None));
        Assert.Contains("APPLIED in one transaction", applyOutput.ToString());
        await using (var db = database.NewContext())
        {
            Assert.Equal(1, await db.AdministrativeProvinces.CountAsync());
            Assert.Equal(168, await db.AdministrativeCommunes.CountAsync());
            Assert.Equal(493, await db.AdministrativeLegacyCommuneMaps.CountAsync());
            var record = await db.AdministrativeCatalogImports.SingleAsync();
            Assert.Equal((CatalogImportStatuses.Applied, "ops", AdministrativeCatalogTestData.DatasetKey), (record.Status, record.AppliedBy, record.DatasetKey));
            Assert.Equal("dac khu con dao", (await db.AdministrativeCommunes.SingleAsync(c => c.Code == "26732")).SearchName);
        }

        var again = new StringWriter();
        Assert.Equal(0, await AdministrativeCatalogCli.RunAsync(apply, database.ConnectionString, again, TimeProvider.System, None));
        Assert.Contains("already applied with the same checksums", again.ToString());
        Assert.Contains("communes: 0 new, 0 changed, 0 closed", again.ToString());
        await using (var db = database.NewContext())
        {
            Assert.Equal(168, await db.AdministrativeCommunes.CountAsync());
            Assert.Single(await db.AdministrativeCatalogImports.ToListAsync());
        }

        // Wrong host: refused before touching the database.
        var wrong = AdministrativeCatalogCli.Parse(["admin-catalog", "import", "--dataset", dataset, "--confirm-host", "db.example.org", "--confirm-database", target.Database!, "--confirm-user", target.Username!, "--apply"]).Options!;
        Assert.Equal(2, await AdministrativeCatalogCli.RunAsync(wrong, database.ConnectionString, new StringWriter(), TimeProvider.System, None));
    }

    [PostgresFact]
    public async Task The_database_enforces_code_formats_and_the_site_commune_pair()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using (var db = database.NewContext())
            Assert.True((await new AdministrativeCatalogImporter(db, TimeProvider.System).RunAsync(AdministrativeCatalogTestData.Files(), true, "test", None)).Applied);

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        async Task<string?> FailureOf(string sql)
        {
            try
            {
                await using var command = new NpgsqlCommand(sql, connection);
                await command.ExecuteNonQueryAsync();
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.ConstraintName ?? ex.SqlState;
            }
        }

        Assert.Equal("ck_administrative_commune_code", await FailureOf(
            "INSERT INTO administrative_commune (code, province_code, name, search_name, unit_type, valid_from, dataset_key) VALUES ('2674A', '79', 'x', 'x', 'WARD', '2025-07-01', 'k')"));
        Assert.Equal("ck_administrative_legacy_commune_map_match_key", await FailureOf(
            "INSERT INTO administrative_legacy_commune_map (id, commune_code, legacy_district_code, legacy_district_name, legacy_district_search_name, legacy_province_code, legacy_province_name, merge_type, match_key, dataset_key) "
            + "VALUES (gen_random_uuid(), '26740', '760', 'Quận 1', 'quan 1', '79', 'HCM', 'WHOLE', 'wrong', 'k')"));

        // A site referencing a commune of another province is refused by the composite FK; a NULL commune is not checked.
        await using var ctx = SchedulingTestContext.CreateOn(database.NewContext());
        var request = await ctx.SeedSurveyRequestAsync(SchedulingTestContext.BinhTan);
        Assert.Equal("fk_property_site_administrative_commune", await FailureOf($"UPDATE property_site SET province_code = '01' WHERE id = '{request.Site.Id}'"));
        Assert.Null(await FailureOf($"UPDATE property_site SET commune_code = NULL, province_code = '01' WHERE id = '{request.Site.Id}'"));
        Assert.Equal("ck_property_site_commune_code", await FailureOf($"UPDATE property_site SET province_code = NULL, commune_code = '26740' WHERE id = '{request.Site.Id}'"));
    }
}
