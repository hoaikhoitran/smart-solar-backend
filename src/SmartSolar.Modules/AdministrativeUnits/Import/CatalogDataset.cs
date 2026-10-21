using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartSolar.Modules.AdministrativeUnits.Import;

/// <summary>Counts declared by the manifest (null when missing; the validator reports missing counts).</summary>
public sealed record CatalogManifestCounts(
    int? Communes,
    int? Wards,
    int? RuralCommunes,
    int? SpecialZones,
    int? LegacyMappings,
    IReadOnlyDictionary<string, int> LegacyByMergeType,
    IReadOnlyDictionary<string, int> LegacyByProvince);

/// <summary>manifest.json of a packaged dataset (see ReferenceData/AdministrativeCatalog/&lt;datasetKey&gt;/manifest.json).</summary>
public sealed record CatalogManifest(
    string? DatasetKey,
    string? ProvinceCode,
    string? ProvinceName,
    string? ProvinceUnitType,
    DateOnly? EffectiveFrom,
    string? SourceUrl,
    string? SourceSha256,
    IReadOnlyDictionary<string, string> Files,
    CatalogManifestCounts Counts);

public sealed record CatalogCommuneRow(int Line, string ProvinceCode, string Code, string Name, string UnitType);

public sealed record CatalogLegacyRow(
    int Line,
    string CommuneCode,
    string? LegacyCommuneCode,
    string? LegacyCommuneName,
    string LegacyDistrictCode,
    string LegacyDistrictName,
    string LegacyProvinceCode,
    string LegacyProvinceName,
    string MergeType,
    string? SourceNote)
{
    public string MatchKey => AdministrativeLegacyCommuneMap.KeyOf(LegacyDistrictCode, LegacyCommuneCode);
}

/// <summary>The raw files of a dataset directory: manifest text and the bytes of every file (checksums are computed on the bytes).</summary>
public sealed record CatalogDatasetFiles(string ManifestJson, IReadOnlyDictionary<string, byte[]> Files)
{
    public const string ManifestFileName = "manifest.json";
    public const string CommunesFileName = "communes.csv";
    public const string LegacyMapFileName = "legacy-commune-map.csv";

    /// <summary>Reads manifest.json, communes.csv and legacy-commune-map.csv of <paramref name="directory"/> (missing CSVs are left out and reported by the validator).</summary>
    public static async Task<CatalogDatasetFiles> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException($"'{ManifestFileName}' was not found in the dataset directory.", manifestPath);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in new[] { CommunesFileName, LegacyMapFileName })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) files[name] = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        return new CatalogDatasetFiles(await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken), files);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public static class CatalogManifestParser
{
    /// <summary>Parses the manifest; throws <see cref="JsonException"/> on malformed JSON (reported as a validation error).</summary>
    public static CatalogManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var source = root.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        var counts = root.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object ? c : default;
        DateOnly? effective = Str(root, "effectiveFrom") is { } text && DateOnly.TryParseExact(text, "yyyy-MM-dd", out var date) ? date : null;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Object)
            foreach (var p in f.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String) files[p.Name] = p.Value.GetString()!;
        return new CatalogManifest(
            Str(root, "datasetKey"), Str(root, "provinceCode"), Str(root, "provinceName"), Str(root, "provinceUnitType"), effective,
            source.ValueKind == JsonValueKind.Object ? Str(source, "url") : null,
            source.ValueKind == JsonValueKind.Object ? Str(source, "sha256") : null,
            files,
            new CatalogManifestCounts(Int(counts, "communes"), Int(counts, "wards"), Int(counts, "ruralCommunes"), Int(counts, "specialZones"),
                Int(counts, "legacyMappings"), Map(counts, "legacyByMergeType"), Map(counts, "legacyByProvince")));
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static IReadOnlyDictionary<string, int> Map(JsonElement e, string name)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
            foreach (var p in v.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var i)) result[p.Name] = i;
        return result;
    }
}

/// <summary>Minimal RFC 4180 reader (UTF-8, optional BOM, LF or CRLF, quoted fields with "" escapes). Values are returned as written (codes stay strings).</summary>
public static class CsvReader
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else quoted = false;
                }
                else field.Append(ch);
                continue;
            }
            switch (ch)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    fieldStarted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = true;
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(field.ToString());
                    field.Clear();
                    rows.Add(row);
                    row = [];
                    fieldStarted = false;
                    break;
                default:
                    field.Append(ch);
                    fieldStarted = true;
                    break;
            }
        }
        if (quoted) throw new FormatException("Unterminated quoted field.");
        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
