using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartSolar.Modules.AdministrativeUnits.Import;

/// <summary>Outcome of validating a dataset (no database access). Rows are only meaningful when <see cref="IsValid"/>.</summary>
public sealed class CatalogValidationReport
{
    public CatalogManifest? Manifest { get; init; }
    public List<string> Errors { get; } = [];
    public List<CatalogCommuneRow> Communes { get; } = [];
    public List<CatalogLegacyRow> Legacy { get; } = [];

    /// <summary>File name → sha256 computed on the bytes read.</summary>
    public Dictionary<string, string> ComputedSha256 { get; } = new(StringComparer.Ordinal);

    public bool IsValid => Errors.Count == 0;

    /// <summary>Stable digest of the dataset content (source sha256 + every CSV checksum) used to refuse re-applying a key with other files.</summary>
    public string Fingerprint => string.Join(";", new[] { $"source={Manifest?.SourceSha256}" }
        .Concat(ComputedSha256.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")));
}

/// <summary>
/// Validates a packaged dataset before anything touches the database: manifest fields, sha256 of every CSV against the manifest,
/// exact CSV headers, code formats as strings (province 2 digits, commune 5 digits, legacy district 3 digits — a code that lost
/// its leading zero is rejected), unit and merge types, duplicates, province/commune consistency, legacy rows referencing known
/// communes, and every declared count. Errors carry the file and line.
/// </summary>
public static partial class CatalogDatasetValidator
{
    public static readonly string[] CommuneHeader = ["province_code", "commune_code", "name", "unit_type"];

    public static readonly string[] LegacyHeader =
    [
        "commune_code", "legacy_commune_code", "legacy_commune_name", "legacy_district_code", "legacy_district_name",
        "legacy_province_code", "legacy_province_name", "merge_type", "source_note",
    ];

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{2,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatasetKeyFormat();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Format();

    public static CatalogValidationReport Validate(CatalogDatasetFiles files)
    {
        CatalogManifest? manifest = null;
        string? parseError = null;
        try
        {
            manifest = CatalogManifestParser.Parse(files.ManifestJson);
        }
        catch (JsonException ex)
        {
            parseError = $"manifest.json is not valid JSON: {ex.Message}";
        }
        var report = new CatalogValidationReport { Manifest = manifest };
        if (manifest is null)
        {
            report.Errors.Add(parseError!);
            return report;
        }

        ValidateManifest(manifest, report);
        foreach (var (name, bytes) in files.Files) report.ComputedSha256[name] = CatalogDatasetFiles.Sha256(bytes);
        foreach (var name in new[] { CatalogDatasetFiles.CommunesFileName, CatalogDatasetFiles.LegacyMapFileName })
        {
            if (!files.Files.ContainsKey(name)) report.Errors.Add($"{name}: file is missing.");
            else if (!manifest.Files.TryGetValue(name, out var declared)) report.Errors.Add($"manifest.json: files.{name} (sha256) is missing.");
            else if (!string.Equals(declared, report.ComputedSha256[name], StringComparison.OrdinalIgnoreCase))
                report.Errors.Add($"{name}: sha256 {report.ComputedSha256[name]} does not match the manifest ({declared}).");
        }
        foreach (var name in manifest.Files.Keys.Where(n => !files.Files.ContainsKey(n) && n is not CatalogDatasetFiles.CommunesFileName and not CatalogDatasetFiles.LegacyMapFileName))
            report.Errors.Add($"manifest.json: lists '{name}', which is not part of a catalog dataset.");

        if (files.Files.TryGetValue(CatalogDatasetFiles.CommunesFileName, out var communes)) ReadCommunes(communes, manifest, report);
        if (files.Files.TryGetValue(CatalogDatasetFiles.LegacyMapFileName, out var legacy)) ReadLegacy(legacy, report);
        ValidateCounts(manifest.Counts, report);
        return report;
    }

    private static void ValidateManifest(CatalogManifest m, CatalogValidationReport report)
    {
        if (m.DatasetKey is null || !DatasetKeyFormat().IsMatch(m.DatasetKey))
            report.Errors.Add("manifest.json: datasetKey is missing or invalid (lower-case letters, digits and '-', 3–100 characters).");
        if (!AdministrativeCodeFormats.IsProvinceCode(m.ProvinceCode))
            report.Errors.Add($"manifest.json: provinceCode '{m.ProvinceCode}' must be a two-digit string (leading zeros kept).");
        if (string.IsNullOrWhiteSpace(m.ProvinceName)) report.Errors.Add("manifest.json: provinceName is missing.");
        if (m.ProvinceUnitType is null || !ProvinceUnitTypes.All.Contains(m.ProvinceUnitType))
            report.Errors.Add($"manifest.json: provinceUnitType '{m.ProvinceUnitType}' must be PROVINCE or MUNICIPALITY.");
        if (m.EffectiveFrom is null) report.Errors.Add("manifest.json: effectiveFrom is missing or not a yyyy-MM-dd date.");
        if (m.SourceUrl is null || !Uri.TryCreate(m.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            report.Errors.Add("manifest.json: source.url must be an absolute https URL.");
        if (m.SourceSha256 is null || !Sha256Format().IsMatch(m.SourceSha256))
            report.Errors.Add("manifest.json: source.sha256 must be 64 lower-case hexadecimal characters.");
    }

    private static List<IReadOnlyList<string>>? Rows(byte[] bytes, string file, string[] header, CatalogValidationReport report)
    {
        IReadOnlyList<IReadOnlyList<string>> rows;
        try
        {
            rows = CsvReader.Parse(bytes);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.DecoderFallbackException)
        {
            report.Errors.Add($"{file}: not a valid UTF-8 CSV ({ex.Message}).");
            return null;
        }
        if (rows.Count == 0 || !rows[0].SequenceEqual(header))
        {
            report.Errors.Add($"{file}: header must be exactly '{string.Join(",", header)}'.");
            return null;
        }
        return rows.Skip(1).ToList();
    }

    private static void ReadCommunes(byte[] bytes, CatalogManifest manifest, CatalogValidationReport report)
    {
        const string file = CatalogDatasetFiles.CommunesFileName;
        var rows = Rows(bytes, file, CommuneHeader, report);
        if (rows is null) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            var line = i + 2;
            var r = rows[i];
            if (r.Count != CommuneHeader.Length)
            {
                report.Errors.Add($"{file}:{line}: expected {CommuneHeader.Length} columns, found {r.Count}.");
                continue;
            }
            var (province, code, name, type) = (r[0], r[1], r[2].Trim(), r[3]);
            var ok = true;
            if (!AdministrativeCodeFormats.IsProvinceCode(province) || province != manifest.ProvinceCode)
            {
                report.Errors.Add($"{file}:{line}: province_code '{province}' must be the manifest province '{manifest.ProvinceCode}'.");
                ok = false;
            }
            if (!AdministrativeCodeFormats.IsCommuneCode(code))
            {
                report.Errors.Add($"{file}:{line}: commune_code '{code}' must be exactly five digits (leading zeros kept).");
                ok = false;
            }
            if (name.Length is 0 or > 150)
            {
                report.Errors.Add($"{file}:{line}: name must be 1–150 characters.");
                ok = false;
            }
            if (!CommuneUnitTypes.All.Contains(type))
            {
                report.Errors.Add($"{file}:{line}: unit_type '{type}' must be WARD, COMMUNE or SPECIAL_ZONE.");
                ok = false;
            }
            if (ok && !seen.Add(code))
            {
                report.Errors.Add($"{file}:{line}: duplicate commune_code '{code}'.");
                ok = false;
            }
            if (ok) report.Communes.Add(new CatalogCommuneRow(line, province, code, name, type));
        }
    }

    private static void ReadLegacy(byte[] bytes, CatalogValidationReport report)
    {
        const string file = CatalogDatasetFiles.LegacyMapFileName;
        var rows = Rows(bytes, file, LegacyHeader, report);
        if (rows is null) return;
        var communes = report.Communes.Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        for (var i = 0; i < rows.Count; i++)
        {
            var line = i + 2;
            var r = rows[i];
            if (r.Count != LegacyHeader.Length)
            {
                report.Errors.Add($"{file}:{line}: expected {LegacyHeader.Length} columns, found {r.Count}.");
                continue;
            }
            var legacyCode = Empty(r[1]);
            var legacyName = Empty(r[2]);
            var row = new CatalogLegacyRow(line, r[0], legacyCode, legacyName, r[3], r[4].Trim(), r[5], r[6].Trim(), r[7], Empty(r[8]));
            var ok = true;
            if (!AdministrativeCodeFormats.IsCommuneCode(row.CommuneCode))
            {
                report.Errors.Add($"{file}:{line}: commune_code '{row.CommuneCode}' must be exactly five digits.");
                ok = false;
            }
            else if (!communes.Contains(row.CommuneCode))
            {
                report.Errors.Add($"{file}:{line}: commune_code '{row.CommuneCode}' is not a commune of communes.csv.");
                ok = false;
            }
            if (legacyCode is not null && !AdministrativeCodeFormats.IsCommuneCode(legacyCode))
            {
                report.Errors.Add($"{file}:{line}: legacy_commune_code '{legacyCode}' must be empty or exactly five digits.");
                ok = false;
            }
            if ((legacyCode is null) != (legacyName is null))
            {
                report.Errors.Add($"{file}:{line}: legacy_commune_code and legacy_commune_name must both be set or both be empty (district-level predecessor).");
                ok = false;
            }
            if (!AdministrativeCodeFormats.IsLegacyDistrictCode(row.LegacyDistrictCode))
            {
                report.Errors.Add($"{file}:{line}: legacy_district_code '{row.LegacyDistrictCode}' must be exactly three digits.");
                ok = false;
            }
            if (row.LegacyDistrictName.Length == 0 || row.LegacyProvinceName.Length == 0)
            {
                report.Errors.Add($"{file}:{line}: legacy_district_name and legacy_province_name are required.");
                ok = false;
            }
            if (!AdministrativeCodeFormats.IsProvinceCode(row.LegacyProvinceCode))
            {
                report.Errors.Add($"{file}:{line}: legacy_province_code '{row.LegacyProvinceCode}' must be exactly two digits.");
                ok = false;
            }
            if (!LegacyMergeTypes.All.Contains(row.MergeType))
            {
                report.Errors.Add($"{file}:{line}: merge_type '{row.MergeType}' must be WHOLE, PARTIAL or UNCHANGED.");
                ok = false;
            }
            if (ok && !seen.Add((row.CommuneCode, row.MatchKey)))
            {
                report.Errors.Add($"{file}:{line}: duplicate mapping of commune {row.CommuneCode} from legacy {row.MatchKey}.");
                ok = false;
            }
            if (ok) report.Legacy.Add(row);
        }
        var orphans = report.Communes.Where(c => report.Legacy.All(l => l.CommuneCode != c.Code)).Select(c => c.Code).ToList();
        if (report.Legacy.Count > 0 && orphans.Count > 0)
            report.Errors.Add($"{file}: communes without any predecessor row: {string.Join(", ", orphans.Take(20))}.");
    }

    private static void ValidateCounts(CatalogManifestCounts counts, CatalogValidationReport report)
    {
        if (!report.IsValid) return;   // counts are meaningless while rows were dropped
        void Check(string name, int? declared, int actual)
        {
            if (declared is null) report.Errors.Add($"manifest.json: counts.{name} is missing.");
            else if (declared != actual) report.Errors.Add($"counts.{name}: manifest declares {declared}, dataset has {actual}.");
        }
        Check("communes", counts.Communes, report.Communes.Count);
        Check("wards", counts.Wards, report.Communes.Count(c => c.UnitType == CommuneUnitTypes.Ward));
        Check("ruralCommunes", counts.RuralCommunes, report.Communes.Count(c => c.UnitType == CommuneUnitTypes.Commune));
        Check("specialZones", counts.SpecialZones, report.Communes.Count(c => c.UnitType == CommuneUnitTypes.SpecialZone));
        Check("legacyMappings", counts.LegacyMappings, report.Legacy.Count);
        foreach (var type in LegacyMergeTypes.All)
        {
            var actual = report.Legacy.Count(l => l.MergeType == type);
            var declared = counts.LegacyByMergeType.TryGetValue(type, out var n) ? n : 0;
            if (declared != actual) report.Errors.Add($"counts.legacyByMergeType.{type}: manifest declares {declared}, dataset has {actual}.");
        }
        foreach (var group in report.Legacy.GroupBy(l => l.LegacyProvinceCode))
        {
            var declared = counts.LegacyByProvince.TryGetValue(group.Key, out var n) ? n : 0;
            if (declared != group.Count()) report.Errors.Add($"counts.legacyByProvince.{group.Key}: manifest declares {declared}, dataset has {group.Count()}.");
        }
        foreach (var (province, declared) in counts.LegacyByProvince.Where(p => report.Legacy.All(l => l.LegacyProvinceCode != p.Key) && p.Value != 0))
            report.Errors.Add($"counts.legacyByProvince.{province}: manifest declares {declared}, dataset has 0.");
    }

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
