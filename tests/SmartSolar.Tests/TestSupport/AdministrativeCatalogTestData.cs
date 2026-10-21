using Microsoft.EntityFrameworkCore;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.AdministrativeUnits.Import;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// The REAL official dataset packaged under src/SmartSolar.Infrastructure/Persistence/ReferenceData/AdministrativeCatalog
/// (vn-2025-07-01-hcm-79: province 79, 168 communes, 493 legacy rows), read once per test run. Tests seed the catalog from it
/// instead of inventing codes.
/// </summary>
public static class AdministrativeCatalogTestData
{
    public const string DatasetKey = "vn-2025-07-01-hcm-79";
    public const string ProvinceCode = "79";
    public static readonly DateOnly EffectiveFrom = new(2025, 7, 1);

    private static readonly Lazy<string> Directory = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "SmartSolar.Infrastructure", "Persistence", "ReferenceData", "AdministrativeCatalog", DatasetKey);
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException($"Dataset {DatasetKey} was not found above {AppContext.BaseDirectory}.");
    });

    // Synchronous reads: blocking on async I/O inside Lazy under xUnit's limited-concurrency context can starve the test run.
    private static readonly Lazy<CatalogDatasetFiles> LazyFiles = new(() => new CatalogDatasetFiles(
        File.ReadAllText(Path.Combine(Directory.Value, CatalogDatasetFiles.ManifestFileName)),
        new Dictionary<string, byte[]>
        {
            [CatalogDatasetFiles.CommunesFileName] = File.ReadAllBytes(Path.Combine(Directory.Value, CatalogDatasetFiles.CommunesFileName)),
            [CatalogDatasetFiles.LegacyMapFileName] = File.ReadAllBytes(Path.Combine(Directory.Value, CatalogDatasetFiles.LegacyMapFileName)),
        }));

    private static readonly Lazy<CatalogValidationReport> LazyReport = new(() =>
    {
        var report = CatalogDatasetValidator.Validate(LazyFiles.Value);
        if (!report.IsValid) throw new InvalidOperationException("The packaged dataset is invalid: " + string.Join("; ", report.Errors));
        return report;
    });

    public static string DatasetDirectory => Directory.Value;

    /// <summary>The packaged files (fresh copy of the byte arrays, so a test may tamper with them).</summary>
    public static CatalogDatasetFiles Files() => new(LazyFiles.Value.ManifestJson, LazyFiles.Value.Files.ToDictionary(p => p.Key, p => p.Value.ToArray()));

    public static CatalogValidationReport Report => LazyReport.Value;

    public static IReadOnlyList<CatalogCommuneRow> Communes => Report.Communes;

    public static IReadOnlyList<CatalogLegacyRow> Legacy => Report.Legacy;

    public static string NameOf(string communeCode) => Communes.Single(c => c.Code == communeCode).Name;

    /// <summary>Province 79 and every commune of the dataset (active from 2025-07-01), unless already present.</summary>
    public static async Task SeedCatalogAsync(AppDbContext db, bool withLegacyMap = false)
    {
        if (!await db.AdministrativeProvinces.AnyAsync(p => p.Code == ProvinceCode))
        {
            db.AdministrativeProvinces.Add(new AdministrativeProvince
            {
                Code = ProvinceCode, Name = Report.Manifest!.ProvinceName!, UnitType = Report.Manifest.ProvinceUnitType!, ValidFrom = EffectiveFrom,
                DatasetKey = DatasetKey, CreatedAt = DateTimeOffset.UtcNow,
            });
            db.AdministrativeCommunes.AddRange(Communes.Select(c => new AdministrativeCommune
            {
                Code = c.Code, ProvinceCode = c.ProvinceCode, Name = c.Name, SearchName = VietnameseText.Normalize(c.Name), UnitType = c.UnitType,
                ValidFrom = EffectiveFrom, DatasetKey = DatasetKey,
            }));
            await db.SaveChangesAsync();
        }
        if (withLegacyMap && !await db.AdministrativeLegacyCommuneMaps.AnyAsync())
        {
            db.AdministrativeLegacyCommuneMaps.AddRange(Legacy.Select(l => new AdministrativeLegacyCommuneMap
            {
                Id = Guid.NewGuid(), CommuneCode = l.CommuneCode, LegacyCommuneCode = l.LegacyCommuneCode, LegacyCommuneName = l.LegacyCommuneName,
                LegacyCommuneSearchName = l.LegacyCommuneName is null ? null : VietnameseText.Normalize(l.LegacyCommuneName),
                LegacyDistrictCode = l.LegacyDistrictCode, LegacyDistrictName = l.LegacyDistrictName, LegacyDistrictSearchName = VietnameseText.Normalize(l.LegacyDistrictName),
                LegacyProvinceCode = l.LegacyProvinceCode, LegacyProvinceName = l.LegacyProvinceName, MergeType = l.MergeType, SourceNote = l.SourceNote,
                MatchKey = l.MatchKey, DatasetKey = DatasetKey,
            }));
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();
    }
}
