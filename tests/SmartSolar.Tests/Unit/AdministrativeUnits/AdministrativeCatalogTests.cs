using System.Text;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Api.Cli;
using SmartSolar.Api.Controllers;
using SmartSolar.Infrastructure.Persistence.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits.Import;
using SmartSolar.Modules.Common.Results;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.AdministrativeUnits;

/// <summary>Official catalog: dataset validation, diff planning, the import (SQLite), public queries and the CLI guard.</summary>
public sealed class AdministrativeCatalogTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    // ------------------------------------------------------------------ helpers

    private static string Text(CatalogDatasetFiles files, string name) => Encoding.UTF8.GetString(files.Files[name]);

    /// <summary>Replaces a CSV and fixes its manifest checksum (so only the content rule under test fails).</summary>
    private static CatalogDatasetFiles WithCsv(CatalogDatasetFiles files, string name, string content, bool fixChecksum = true)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var map = files.Files.ToDictionary(p => p.Key, p => p.Value);
        map[name] = bytes;
        var manifest = files.ManifestJson;
        if (fixChecksum) manifest = manifest.Replace(CatalogDatasetFiles.Sha256(files.Files[name]), CatalogDatasetFiles.Sha256(bytes));
        return new CatalogDatasetFiles(manifest, map);
    }

    private static CatalogValidationReport Validate(CatalogDatasetFiles files) => CatalogDatasetValidator.Validate(files);

    // ------------------------------------------------------------------ validator

    [Fact]
    public void The_packaged_dataset_is_valid_and_matches_its_manifest()
    {
        var report = Validate(AdministrativeCatalogTestData.Files());

        Assert.True(report.IsValid, string.Join("; ", report.Errors));
        Assert.Equal(168, report.Communes.Count);
        Assert.Equal(113, report.Communes.Count(c => c.UnitType == CommuneUnitTypes.Ward));
        Assert.Equal(54, report.Communes.Count(c => c.UnitType == CommuneUnitTypes.Commune));
        Assert.Equal(1, report.Communes.Count(c => c.UnitType == CommuneUnitTypes.SpecialZone));
        Assert.Equal(493, report.Legacy.Count);
        Assert.Equal(("vn-2025-07-01-hcm-79", "79", new DateOnly(2025, 7, 1)), (report.Manifest!.DatasetKey, report.Manifest.ProvinceCode, report.Manifest.EffectiveFrom!.Value));
        var conDao = Assert.Single(report.Legacy, l => l.CommuneCode == "26732");
        Assert.Null(conDao.LegacyCommuneCode);                                       // district-level predecessor
        Assert.Equal("755:-", conDao.MatchKey);
        Assert.Contains(report.Legacy, l => l.SourceNote == "Nhập một phần diện tích, toàn bộ dân số");   // quoted CSV field with a comma
    }

    [Fact]
    public void A_csv_that_does_not_match_its_manifest_checksum_is_rejected()
    {
        var files = AdministrativeCatalogTestData.Files();
        var tampered = WithCsv(files, CatalogDatasetFiles.CommunesFileName, Text(files, CatalogDatasetFiles.CommunesFileName).Replace("Phường Sài Gòn", "Phường Sai Gon"), fixChecksum: false);

        var report = Validate(tampered);

        Assert.Contains(report.Errors, e => e.StartsWith("communes.csv: sha256") && e.Contains("does not match the manifest"));
    }

    [Fact]
    public void Counts_codes_leading_zeros_duplicates_and_unit_types_are_validated()
    {
        var files = AdministrativeCatalogTestData.Files();
        var communes = Text(files, CatalogDatasetFiles.CommunesFileName);

        // A code that lost its leading zero / has four digits, an unknown unit type, a duplicate, another province.
        var bad = communes.Replace("79,25747,Phường Thủ Dầu Một,WARD", "79,2574,Phường Thủ Dầu Một,WARD")
            .Replace("79,25750,Phường Phú Lợi,WARD", "79,25750,Phường Phú Lợi,TOWN")
            .Replace("79,25760,Phường Bình Dương,WARD", "79,25768,Phường Bình Dương,WARD")
            .Replace("79,25771,", "80,25771,");
        var report = Validate(WithCsv(files, CatalogDatasetFiles.CommunesFileName, bad));

        Assert.Contains(report.Errors, e => e.Contains("commune_code '2574' must be exactly five digits"));
        Assert.Contains(report.Errors, e => e.Contains("unit_type 'TOWN'"));
        Assert.Contains(report.Errors, e => e.Contains("duplicate commune_code '25768'"));
        Assert.Contains(report.Errors, e => e.Contains("province_code '80' must be the manifest province '79'"));
        Assert.False(report.IsValid);

        // Leading zeros are kept as text: "00123" is a well-formed five-digit code (reported only because it has no predecessor row).
        var withZeros = Validate(WithCsv(files, CatalogDatasetFiles.CommunesFileName, communes + "79,00123,Phường Thử,WARD\n"));
        Assert.DoesNotContain(withZeros.Errors, e => e.Contains("00123") && e.Contains("five digits"));
        Assert.Contains(withZeros.Errors, e => e.Contains("communes without any predecessor row: 00123"));
    }

    [Fact]
    public void Legacy_rows_must_reference_known_communes_and_use_known_merge_types()
    {
        var files = AdministrativeCatalogTestData.Files();
        var legacy = Text(files, CatalogDatasetFiles.LegacyMapFileName);
        var bad = legacy.Replace("25747,25741,Phường Hiệp Thành,718", "99999,25741,Phường Hiệp Thành,718")
            .Replace("25747,25747,Phường Phú Cường,718,Thành phố Thủ Dầu Một,74,Tỉnh Bình Dương,WHOLE", "25747,25747,Phường Phú Cường,718,Thành phố Thủ Dầu Một,74,Tỉnh Bình Dương,SPLIT")
            .Replace("25747,25753,Phường Phú Thọ,718", "25747,25753,Phường Phú Thọ,71");

        var report = Validate(WithCsv(files, CatalogDatasetFiles.LegacyMapFileName, bad));

        Assert.Contains(report.Errors, e => e.Contains("commune_code '99999' is not a commune of communes.csv"));
        Assert.Contains(report.Errors, e => e.Contains("merge_type 'SPLIT'"));
        Assert.Contains(report.Errors, e => e.Contains("legacy_district_code '71' must be exactly three digits"));
        var duplicated = Validate(WithCsv(files, CatalogDatasetFiles.LegacyMapFileName, legacy + "25747,25741,Phường Hiệp Thành,718,Thành phố Thủ Dầu Một,74,Tỉnh Bình Dương,PARTIAL,Nhập một phần\n"));
        Assert.Contains(duplicated.Errors, e => e.Contains("duplicate mapping of commune 25747 from legacy 718:25741"));
    }

    [Fact]
    public void Manifest_fields_and_headers_are_validated()
    {
        var files = AdministrativeCatalogTestData.Files();
        var report = Validate(files with { ManifestJson = files.ManifestJson.Replace("\"provinceCode\": \"79\"", "\"provinceCode\": \"7\"").Replace("\"effectiveFrom\": \"2025-07-01\"", "\"effectiveFrom\": \"July\"") });
        Assert.Contains(report.Errors, e => e.Contains("provinceCode '7' must be a two-digit string"));
        Assert.Contains(report.Errors, e => e.Contains("effectiveFrom is missing"));
        Assert.Single(Validate(files with { ManifestJson = "{ not json" }).Errors);
        var header = Validate(WithCsv(files, CatalogDatasetFiles.CommunesFileName, "code,name\n79,26740\n"));
        Assert.Contains(header.Errors, e => e.Contains("header must be exactly"));
    }

    // ------------------------------------------------------------------ diff planner (superseded codes)

    [Fact]
    public void Codes_missing_from_a_newer_dataset_are_closed_never_deleted_and_older_datasets_are_refused()
    {
        var report = AdministrativeCatalogTestData.Report;
        var stored = report.Communes.Select(c => new AdministrativeCommune
        {
            Code = c.Code, ProvinceCode = c.ProvinceCode, Name = c.Name, SearchName = VietnameseText.Normalize(c.Name), UnitType = c.UnitType,
            ValidFrom = new DateOnly(2025, 7, 1), DatasetKey = "x",
        }).ToList();
        stored.Add(new AdministrativeCommune { Code = "00001", ProvinceCode = "79", Name = "Superseded", SearchName = "superseded", UnitType = CommuneUnitTypes.Ward, ValidFrom = new DateOnly(2025, 7, 1), DatasetKey = "x" });
        stored.Single(c => c.Code == "26740").Name = "Old name";

        var diff = CatalogDiffPlanner.Compute(report, null, stored, []);

        Assert.True(diff.ProvinceIsNew);
        Assert.Equal(["00001"], diff.ClosedCommunes);
        Assert.Equal(("26740", "name", "Old name", "Phường Sài Gòn"), (diff.ChangedCommunes.Single().Code, diff.ChangedCommunes.Single().Field, diff.ChangedCommunes.Single().Before, diff.ChangedCommunes.Single().After));
        Assert.Empty(diff.NewCommunes);
        Assert.Equal(493, diff.NewLegacyRows);
        Assert.Empty(diff.Refusals);

        stored.Single(c => c.Code == "00001").ValidTo = new DateOnly(2026, 1, 1);   // closed by a newer dataset already applied
        Assert.Contains("older than catalog data", Assert.Single(CatalogDiffPlanner.Compute(report, null, stored, []).Refusals));
    }

    // ------------------------------------------------------------------ importer (SQLite) and catalog store

    [Fact]
    public async Task Applying_records_the_import_and_re_applying_is_idempotent_while_other_files_under_the_same_key_are_refused()
    {
        await using var ctx = SchedulingTestContext.Create();
        var importer = new AdministrativeCatalogImporter(ctx.Db, ctx.Clock);

        var dry = await importer.RunAsync(AdministrativeCatalogTestData.Files(), apply: false, "tester", None);
        Assert.True(dry.Succeeded);
        Assert.False(dry.Applied);
        Assert.Equal(168, dry.Diff!.NewCommunes.Count);
        Assert.Equal(0, await ctx.Db.AdministrativeCommunes.CountAsync());           // dry run writes nothing
        Assert.Equal(0, await ctx.Db.AdministrativeCatalogImports.CountAsync());

        var applied = await importer.RunAsync(AdministrativeCatalogTestData.Files(), apply: true, "tester", None);
        Assert.True(applied.Applied);
        Assert.Equal(168, await ctx.Db.AdministrativeCommunes.CountAsync());
        Assert.Equal(493, await ctx.Db.AdministrativeLegacyCommuneMaps.CountAsync());
        var record = await ctx.Db.AdministrativeCatalogImports.AsNoTracking().SingleAsync();
        Assert.Equal((CatalogImportStatuses.Applied, "tester"), (record.Status, record.AppliedBy));
        Assert.Contains("5ecc20deed2a05b22174f6ec4cf5d083a61172f8f0c48666a4266bd40d4c6a57", record.FilesSha256);
        Assert.Equal("phuong thu duc", (await ctx.Db.AdministrativeCommunes.AsNoTracking().SingleAsync(c => c.Code == "26824")).SearchName);

        var again = await importer.RunAsync(AdministrativeCatalogTestData.Files(), apply: true, "tester", None);
        Assert.True(again.Succeeded && again.AlreadyApplied);
        Assert.False(again.Diff!.HasChanges);
        Assert.Single(await ctx.Db.AdministrativeCatalogImports.ToListAsync());

        var files = AdministrativeCatalogTestData.Files();
        var changed = WithCsv(files, CatalogDatasetFiles.CommunesFileName, Text(files, CatalogDatasetFiles.CommunesFileName).Replace("Phường Sài Gòn", "Phường Sài Gòn Mới"));
        var refused = await importer.RunAsync(changed, apply: true, "tester", None);
        Assert.False(refused.Succeeded);
        Assert.Contains("already applied with different checksums", Assert.Single(refused.Refusals));
        Assert.Equal("Phường Sài Gòn", (await ctx.Db.AdministrativeCommunes.AsNoTracking().SingleAsync(c => c.Code == "26740")).Name);
    }

    [Fact]
    public async Task A_rejected_apply_is_recorded_once_and_nothing_is_written()
    {
        await using var ctx = SchedulingTestContext.Create();
        var files = AdministrativeCatalogTestData.Files();
        var tampered = WithCsv(files, CatalogDatasetFiles.CommunesFileName, Text(files, CatalogDatasetFiles.CommunesFileName) + "x", fixChecksum: false);

        var result = await new AdministrativeCatalogImporter(ctx.Db, ctx.Clock).RunAsync(tampered, apply: true, "tester", None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, await ctx.Db.AdministrativeCommunes.CountAsync());
        Assert.Equal(CatalogImportStatuses.Rejected, (await ctx.Db.AdministrativeCatalogImports.AsNoTracking().SingleAsync()).Status);
        Assert.True((await new AdministrativeCatalogImporter(ctx.Db, ctx.Clock).RunAsync(AdministrativeCatalogTestData.Files(), true, "tester", None)).Applied);
        Assert.Equal(CatalogImportStatuses.Applied, (await ctx.Db.AdministrativeCatalogImports.AsNoTracking().SingleAsync()).Status);
    }

    // ------------------------------------------------------------------ public queries

    private static async Task<(SchedulingTestContext Ctx, AdministrativeUnitQueries Queries)> QueriesAsync()
    {
        var ctx = SchedulingTestContext.Create();
        await ctx.EnsureCatalogAsync(withLegacyMap: true);
        return (ctx, new AdministrativeUnitQueries(ctx.Catalog, ctx.Clock));
    }

    [Fact]
    public async Task Provinces_and_communes_list_active_rows_with_validated_filters()
    {
        var (ctx, queries) = await QueriesAsync();
        await using var _ = ctx;

        var province = Assert.Single(await queries.ProvincesAsync(None));
        Assert.Equal(("79", "Thành phố Hồ Chí Minh", ProvinceUnitTypes.Municipality), (province.Code, province.Name, province.UnitType));
        Assert.Equal(168, (await queries.CommunesAsync("79", null, null, None)).Value!.Count);
        Assert.Equal(54, (await queries.CommunesAsync("79", null, "commune", None)).Value!.Count);
        Assert.Equal("26732", Assert.Single((await queries.CommunesAsync("79", null, "SPECIAL_ZONE", None)).Value!).Code);
        Assert.Equal("26824", Assert.Single((await queries.CommunesAsync("79", "thu duc", null, None)).Value!).Code);        // accent-insensitive
        Assert.Equal("26824", Assert.Single((await queries.CommunesAsync("79", "THỦ ĐỨC", null, None)).Value!).Code);
        Assert.Empty((await queries.CommunesAsync("01", null, null, None)).Value!);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await queries.CommunesAsync(null, null, null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await queries.CommunesAsync("7", null, null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await queries.CommunesAsync("79", null, "DISTRICT", None)).ErrorCode);

        await ctx.Db.AdministrativeCommunes.Where(c => c.Code == "26732").ExecuteUpdateAsync(x => x.SetProperty(c => c.ValidTo, new DateOnly(2026, 1, 1)));
        Assert.Equal(167, (await queries.CommunesAsync("79", null, null, None)).Value!.Count);                               // closed codes disappear
    }

    [Fact]
    public async Task Legacy_lookup_finds_partial_and_ambiguous_predecessors_without_selecting_anything()
    {
        var (ctx, queries) = await QueriesAsync();
        await using var _ = ctx;

        var result = (await queries.LegacyLookupAsync("hiep thanh", "79", None)).Value!;

        Assert.True(result.RequiresConfirmation);
        Assert.True(result.Ambiguous);
        var tdm = Assert.Single(result.Matches, m => m.LegacyDistrict.Code == "718");
        Assert.Equal((LegacyMatchTypes.LegacyCommune, "25741", "Phường Hiệp Thành"), (tdm.MatchType, tdm.LegacyCommune!.Code, tdm.LegacyCommune.Name));
        Assert.True(tdm.Ambiguous && tdm.RequiresConfirmation);
        Assert.Equal(["25747", "25750"], tdm.Candidates.Select(c => c.Code).Order().ToArray());
        Assert.All(tdm.Candidates, c => Assert.Equal(LegacyMergeTypes.Partial, c.MergeType));
        Assert.Equal("74", tdm.LegacyProvince.Code);
        var q12 = Assert.Single(result.Matches, m => m.LegacyDistrict.Code == "761");
        Assert.False(q12.Ambiguous);
        Assert.Equal(("26782", LegacyMergeTypes.Whole), (Assert.Single(q12.Candidates).Code, q12.Candidates[0].MergeType));
        Assert.Equal(result.Matches.Count, (await queries.LegacyLookupAsync("Phường HIỆP THÀNH", null, None)).Value!.Matches.Count);   // accents and case
        Assert.Equal(2, (await queries.LegacyLookupAsync("25741", null, None)).Value!.Matches.Single().Candidates.Count);           // code search
    }

    [Fact]
    public async Task Legacy_lookup_searches_former_districts_and_the_district_level_predecessor()
    {
        var (ctx, queries) = await QueriesAsync();
        await using var _ = ctx;

        var district = (await queries.LegacyLookupAsync("quan 1", "79", None)).Value!;
        var q1 = Assert.Single(district.Matches, m => m.MatchType == LegacyMatchTypes.LegacyDistrict && m.LegacyDistrict.Code == "760");
        Assert.Equal(13, q1.Candidates.Count);                                         // every predecessor commune of Quận 1
        Assert.Equal(["26737", "26740", "26743", "26758"], q1.Candidates.Select(c => c.Code).Distinct().Order().ToArray());
        Assert.True(q1.Ambiguous);
        Assert.Contains(district.Matches, m => m.LegacyDistrict.Name == "Quận 10");   // "contains" search

        var conDao = (await queries.LegacyLookupAsync("côn đảo", null, None)).Value!;
        var match = Assert.Single(conDao.Matches);
        Assert.Equal(LegacyMatchTypes.LegacyDistrict, match.MatchType);
        Assert.Null(match.LegacyCommune);
        var candidate = Assert.Single(match.Candidates);
        Assert.Equal(("26732", CommuneUnitTypes.SpecialZone), (candidate.Code, candidate.UnitType));
        Assert.False(conDao.Ambiguous);
        Assert.True(conDao.RequiresConfirmation);

        Assert.Equal(CommonErrorCodes.ValidationFailed, (await queries.LegacyLookupAsync("a", null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await queries.LegacyLookupAsync("  ", null, None)).ErrorCode);
        Assert.Equal(CommonErrorCodes.ValidationFailed, (await queries.LegacyLookupAsync("quan 1", "7", None)).ErrorCode);
        Assert.Empty((await queries.LegacyLookupAsync("quan 1", "01", None)).Value!.Matches);
        var many = (await queries.LegacyLookupAsync("phuong", null, None)).Value!;
        Assert.Equal(AdministrativeUnitQueries.MaxLegacyMatches, many.Matches.Count);
        Assert.True(many.Truncated);
        Assert.Equal(400, OperationResultControllerBase.StatusFor(CommonErrorCodes.ValidationFailed));
    }

    [Fact]
    public void Vietnamese_names_normalize_to_accent_free_lower_case()
    {
        Assert.Equal("phuong thu duc", VietnameseText.Normalize("  Phường   Thủ Đức "));
        Assert.Equal("dac khu con dao", VietnameseText.Normalize("ĐẶC KHU CÔN ĐẢO"));
        Assert.Equal("", VietnameseText.Normalize(null));
        Assert.Equal(new DateOnly(2026, 10, 20), AdministrativeCatalogClock.Today(new FixedTimeProvider(new DateTimeOffset(2026, 10, 19, 17, 30, 0, TimeSpan.Zero))));
    }

    // ------------------------------------------------------------------ CLI guard (no database is touched)

    [Fact]
    public async Task The_cli_requires_a_matching_host_database_and_user_and_never_prints_credentials()
    {
        string[] Args(params string[] extra) => ["admin-catalog", "import", "--dataset", "d", .. extra];
        string[] target = ["--confirm-host", "h", "--confirm-database", "db", "--confirm-user", "u"];
        Assert.NotNull(AdministrativeCatalogCli.Parse(Args(target)).Options);
        Assert.True(AdministrativeCatalogCli.Parse(Args([.. target, "--apply"])).Options!.Apply);
        Assert.Contains("--confirm-host is required", AdministrativeCatalogCli.Parse(Args()).Error);
        Assert.Contains("--confirm-database is required", AdministrativeCatalogCli.Parse(Args("--confirm-host", "h", "--confirm-user", "u")).Error);
        Assert.Contains("--confirm-user is required", AdministrativeCatalogCli.Parse(Args("--confirm-host", "h", "--confirm-database", "db")).Error);
        Assert.Contains("--dataset is required", AdministrativeCatalogCli.Parse(["admin-catalog", "import", .. target]).Error);
        Assert.NotNull(AdministrativeCatalogCli.Parse(["admin-catalog", "export"]).Error);
        Assert.NotNull(AdministrativeCatalogCli.Parse(Args([.. target, "--force"])).Error);
        Assert.True(AdministrativeCatalogCli.IsCommand(["admin-catalog"]));
        Assert.False(AdministrativeCatalogCli.IsCommand([]));

        // Shared pooler: same host and database, different project (user name) → refused.
        const string connection = "Host=pooler.example;Database=postgres;Username=postgres.devref;Password=s3cr3t-value";
        foreach (var (host, database, user, refused) in new[]
                 {
                     ("other.example", "postgres", "postgres.devref", "--confirm-host"),
                     ("pooler.example", "other", "postgres.devref", "--confirm-database"),
                     ("pooler.example", "postgres", "postgres.prodref", "--confirm-user"),
                 })
        {
            var output = new StringWriter();
            var options = AdministrativeCatalogCli.Parse(["admin-catalog", "import", "--dataset", AdministrativeCatalogTestData.DatasetDirectory,
                "--confirm-host", host, "--confirm-database", database, "--confirm-user", user, "--apply"]).Options!;

            Assert.Equal(2, await AdministrativeCatalogCli.RunAsync(options, connection, output, TimeProvider.System, None));
            Assert.Contains("host=pooler.example database=postgres user=postgres.devref", output.ToString());
            Assert.Contains($"Refusing to run: {refused}", output.ToString());
            Assert.DoesNotContain("s3cr3t-value", output.ToString());
        }
        var any = AdministrativeCatalogCli.Parse(Args(target)).Options!;
        Assert.Equal(2, await AdministrativeCatalogCli.RunAsync(any, null, new StringWriter(), TimeProvider.System, None));
    }

    [Fact]
    public async Task A_connection_failure_is_one_sanitized_line()
    {
        var output = new StringWriter();
        var options = AdministrativeCatalogCli.Parse(["admin-catalog", "import", "--dataset", AdministrativeCatalogTestData.DatasetDirectory,
            "--confirm-host", "127.0.0.1", "--confirm-database", "nothing", "--confirm-user", "nobody"]).Options!;

        var code = await AdministrativeCatalogCli.RunAsync(options, "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=s3cr3t-value;Timeout=2",
            output, TimeProvider.System, None);

        Assert.Equal(2, code);
        Assert.Contains("Database error", output.ToString());
        Assert.DoesNotContain("s3cr3t-value", output.ToString());
        Assert.DoesNotContain("   at ", output.ToString());
    }
}
