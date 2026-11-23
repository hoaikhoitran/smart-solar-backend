using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartSolar.Modules.SolarSimulation.Regional;

/// <summary>Constants of the 2025 Ho Chi Minh City regional solar baseline.</summary>
public static class EstimateBases
{
    /// <summary>Area-weighted average over the verified 2025 territory of province 79, scaled to the site's installed capacity.</summary>
    public const string HcmcRegionalAreaWeighted = "HCMC_2025_REGIONAL_AREA_WEIGHTED";

    /// <summary>Results computed before the regional model (PVGIS / NASA POWER at the site's own coordinates).</summary>
    public const string LegacySitePoint = "SITE_POINT_PVGIS_LEGACY";

    /// <summary>The estimate basis of a stored result, from its algorithm version (results never stored a basis before the regional model).</summary>
    public static string ForAlgorithm(string? algorithmVersion)
        => algorithmVersion is not null && algorithmVersion.Contains(Constants.AlgorithmVersions.RegionalBaseline, StringComparison.Ordinal)
            ? HcmcRegionalAreaWeighted
            : LegacySitePoint;

    /// <summary>The user-facing label (Vietnamese). Never "dự báo chính xác tại mái nhà".</summary>
    public const string LabelVi = "Ước tính trung bình khu vực TP.HCM (sau sáp nhập)";
}

public static class RegionalQualities
{
    /// <summary>Every sample succeeded; weights used as published.</summary>
    public const string Complete = "COMPLETE";

    /// <summary>Some samples failed but the succeeded area weight met the configured threshold; weights renormalised over the succeeded samples.</summary>
    public const string Degraded = "DEGRADED";
}

public static class RegionalFailureCodes
{
    public const string CoverageInsufficient = "REGIONAL_COVERAGE_INSUFFICIENT";
    public const string Disabled = "REGIONAL_BASELINE_DISABLED";
    public const string RegionNotSupported = "REGION_NOT_SUPPORTED";
    public const string RegionUnknown = "REGION_UNKNOWN";
}

/// <summary>One representative sample: a point inside its equal-area cell, weighted by the clipped cell area (weights sum to 1).</summary>
public sealed record RegionalSample(string LocationId, decimal Latitude, decimal Longitude, decimal Weight, decimal AreaKm2);

/// <summary>Where the territory boundary came from (recorded verbatim from the manifest).</summary>
public sealed record BoundaryProvenance(
    string Publisher,
    string DatasetPage,
    string DownloadUrl,
    string? DownloadSha256,
    string FileSha256,
    string FeaturePcode,
    string? ValidOn,
    string? FeatureVersion,
    string License,
    string Crs);

public sealed record RegionalReferencePoint(decimal Latitude, decimal Longitude, bool DisplayOnly, string Method);

/// <summary>
/// A validated regional sample manifest (see guides/hcmc-regional-solar-baseline.md). Loading checks the canonical hashes, weights,
/// counts and that every sample lies inside the shipped boundary; an invalid dataset is never used (no fallback to a city-centre point).
/// </summary>
public sealed class RegionalDataset
{
    public const string ExpectedRegionCode = "79";

    private readonly IReadOnlyList<IReadOnlyList<(double Lon, double Lat)[]>> _boundary;

    private RegionalDataset(string datasetKey, string regionCode, string regionName, string weightMethod, string sourceDatasetVersion, string manifestSha256,
        string samplesSha256, BoundaryProvenance boundary, RegionalReferencePoint referencePoint, IReadOnlyList<RegionalSample> samples, decimal cellKm,
        IReadOnlyList<IReadOnlyList<(double Lon, double Lat)[]>> polygons)
    {
        DatasetKey = datasetKey;
        RegionCode = regionCode;
        RegionName = regionName;
        WeightMethod = weightMethod;
        SourceDatasetVersion = sourceDatasetVersion;
        ManifestSha256 = manifestSha256;
        SamplesSha256 = samplesSha256;
        Boundary = boundary;
        ReferencePoint = referencePoint;
        Samples = samples;
        CellKm = cellKm;
        _boundary = polygons;
    }

    public string DatasetKey { get; }
    public string RegionCode { get; }
    public string RegionName { get; }
    public string WeightMethod { get; }
    public string SourceDatasetVersion { get; }
    /// <summary>SHA-256 of the manifest text (line endings normalised to LF); part of every simulation fingerprint.</summary>
    public string ManifestSha256 { get; }
    /// <summary>SHA-256 of the canonical sample content (id|lat|lon|weight lines).</summary>
    public string SamplesSha256 { get; }
    public BoundaryProvenance Boundary { get; }
    public RegionalReferencePoint ReferencePoint { get; }
    public IReadOnlyList<RegionalSample> Samples { get; }
    public decimal CellKm { get; }

    /// <summary>Even-odd point-in-polygon against the simplified boundary (lon/lat plane; tolerance ≈ the simplification tolerance).</summary>
    public bool Contains(decimal latitude, decimal longitude)
    {
        double x = (double)longitude, y = (double)latitude;
        foreach (var polygon in _boundary)
        {
            if (!InRing(x, y, polygon[0])) continue;
            var inHole = false;
            for (var h = 1; h < polygon.Count && !inHole; h++) inHole = InRing(x, y, polygon[h]);
            if (!inHole) return true;
        }
        return false;
    }

    private static bool InRing(double x, double y, (double Lon, double Lat)[] ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Length - 1; i < ring.Length; j = i++)
        {
            var (xi, yi) = ring[i];
            var (xj, yj) = ring[j];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Loads and validates a dataset directory (manifest.json, samples.csv, boundary-simplified.geojson).</summary>
    public static RegionalDataset Load(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new RegionalDatasetException($"Regional dataset manifest not found: {manifestPath}.");
        var manifestText = Normalize(File.ReadAllText(manifestPath, Encoding.UTF8));
        using var manifest = JsonDocument.Parse(manifestText);
        var root = manifest.RootElement;

        string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!
                : throw new RegionalDatasetException($"Regional dataset manifest: '{name}' is missing.");
        string? Opt(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var regionCode = Str(root, "regionCode");
        if (regionCode != ExpectedRegionCode) throw new RegionalDatasetException($"Regional dataset is for region '{regionCode}', expected '{ExpectedRegionCode}'.");
        if (Str(root, "estimateBasis") != EstimateBases.HcmcRegionalAreaWeighted)
            throw new RegionalDatasetException("Regional dataset estimateBasis is not HCMC_2025_REGIONAL_AREA_WEIGHTED.");

        var b = root.TryGetProperty("boundary", out var bb) && bb.ValueKind == JsonValueKind.Object ? bb : throw new RegionalDatasetException("Regional dataset manifest: 'boundary' is missing.");
        var provenance = new BoundaryProvenance(Str(b, "publisher"), Str(b, "datasetPage"), Str(b, "downloadUrl"), Opt(b, "downloadSha256"), Str(b, "fileSha256"),
            Str(b, "featurePcode"), Opt(b, "validOn"), Opt(b, "featureVersion"), Str(b, "license"), Str(b, "crs"));
        if (provenance.FeaturePcode != "VN" + ExpectedRegionCode) throw new RegionalDatasetException("Regional dataset boundary is not feature VN79.");
        if (!IsSha256(provenance.FileSha256)) throw new RegionalDatasetException("Regional dataset boundary fileSha256 is not a SHA-256.");

        // Samples: canonical hash first (independent of line endings), then values.
        var samplesFile = Path.Combine(directory, Str(root, "samplesFile"));
        if (!File.Exists(samplesFile)) throw new RegionalDatasetException($"Regional samples file not found: {samplesFile}.");
        var lines = Normalize(File.ReadAllText(samplesFile, Encoding.UTF8)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        const string header = "locationId,latitude,longitude,areaWeight,areaKm2,cell,sourceDatasetVersion";
        if (lines.Length < 2 || lines[0] != header) throw new RegionalDatasetException($"Regional samples header must be '{header}'.");
        var rows = lines.Skip(1).Select(l => l.Split(',')).ToList();
        if (rows.Any(r => r.Length != 7)) throw new RegionalDatasetException("Regional samples rows must have 7 columns.");
        var canonical = string.Join('\n', rows.Select(r => $"{r[0]}|{r[1]}|{r[2]}|{r[3]}"));
        var canonicalSha = Sha256(canonical);
        if (canonicalSha != Str(root, "samplesCanonicalSha256"))
            throw new RegionalDatasetException("Regional samples do not match the manifest's samplesCanonicalSha256 (the file was changed).");
        var sourceVersion = Str(root, "sourceDatasetVersion");

        var samples = new List<RegionalSample>(rows.Count);
        foreach (var r in rows)
        {
            if (!decimal.TryParse(r[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                || !decimal.TryParse(r[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)
                || !decimal.TryParse(r[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
                || !decimal.TryParse(r[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var area))
                throw new RegionalDatasetException($"Regional sample '{r[0]}' has a non-numeric value.");
            if (lat is < 8m or > 12m || lon is < 105.5m or > 108m) throw new RegionalDatasetException($"Regional sample '{r[0]}' lies outside the expected HCMC extent.");
            if (weight < 0 || area < 0) throw new RegionalDatasetException($"Regional sample '{r[0]}' has a negative weight or area.");
            if (r[6] != sourceVersion) throw new RegionalDatasetException($"Regional sample '{r[0]}' has source version '{r[6]}', expected '{sourceVersion}'.");
            samples.Add(new RegionalSample(r[0], lat, lon, weight, area));
        }
        if (samples.Select(s => s.LocationId).Distinct(StringComparer.Ordinal).Count() != samples.Count)
            throw new RegionalDatasetException("Regional sample ids are not unique.");
        if (!root.TryGetProperty("sampleCount", out var countElement) || countElement.GetInt32() != samples.Count)
            throw new RegionalDatasetException("Regional samples count differs from the manifest sampleCount.");
        var sum = samples.Sum(s => s.Weight);
        if (Math.Abs(sum - 1m) > 0.000001m) throw new RegionalDatasetException($"Regional sample weights sum to {sum}, expected 1.");
        if (samples.All(s => s.Weight == 0)) throw new RegionalDatasetException("Regional sample weights are all zero.");

        // Boundary.
        var boundaryPath = Path.Combine(directory, Str(root, "boundaryFile"));
        if (!File.Exists(boundaryPath)) throw new RegionalDatasetException($"Regional boundary file not found: {boundaryPath}.");
        var boundaryText = Normalize(File.ReadAllText(boundaryPath, Encoding.UTF8));
        if (Sha256(boundaryText) != Str(root, "boundaryFileSha256"))
            throw new RegionalDatasetException("Regional boundary file does not match the manifest's boundaryFileSha256.");
        var polygons = ParseBoundary(boundaryText);

        var rp = root.TryGetProperty("referencePoint", out var rpe) ? rpe : throw new RegionalDatasetException("Regional dataset manifest: 'referencePoint' is missing.");
        var reference = new RegionalReferencePoint(rp.GetProperty("latitude").GetDecimal(), rp.GetProperty("longitude").GetDecimal(), true,
            Opt(rp, "method") ?? "Display only.");
        var cellKm = root.TryGetProperty("grid", out var grid) && grid.TryGetProperty("cellKm", out var ck) ? ck.GetDecimal() : 0m;

        var dataset = new RegionalDataset(Str(root, "datasetKey"), regionCode, Str(root, "regionName"), Str(root, "weightMethod"), sourceVersion,
            Sha256(manifestText), canonicalSha, provenance, reference, samples, cellKm, polygons);
        var outside = samples.Where(s => !dataset.Contains(s.Latitude, s.Longitude)).Select(s => s.LocationId).ToList();
        if (outside.Count > 0) throw new RegionalDatasetException($"Regional samples outside the boundary: {string.Join(", ", outside)}.");
        return dataset;
    }

    private static IReadOnlyList<IReadOnlyList<(double Lon, double Lat)[]>> ParseBoundary(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var features = doc.RootElement.GetProperty("features");
        if (features.GetArrayLength() != 1) throw new RegionalDatasetException("Regional boundary must contain exactly one feature.");
        var geometry = features[0].GetProperty("geometry");
        if (geometry.GetProperty("type").GetString() != "MultiPolygon") throw new RegionalDatasetException("Regional boundary must be a MultiPolygon.");
        var polygons = new List<IReadOnlyList<(double, double)[]>>();
        foreach (var polygon in geometry.GetProperty("coordinates").EnumerateArray())
        {
            var rings = polygon.EnumerateArray()
                .Select(ring => ring.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToArray())
                .ToList();
            if (rings.Count == 0 || rings[0].Length < 4) throw new RegionalDatasetException("Regional boundary has an invalid ring.");
            polygons.Add(rings);
        }
        if (polygons.Count == 0) throw new RegionalDatasetException("Regional boundary has no polygon.");
        return polygons;
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n");

    private static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}

public sealed class RegionalDatasetException(string message) : Exception(message);
