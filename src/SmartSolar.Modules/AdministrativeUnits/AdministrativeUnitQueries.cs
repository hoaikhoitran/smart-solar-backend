using SmartSolar.Modules.Common.Results;

namespace SmartSolar.Modules.AdministrativeUnits;

public sealed record ProvinceView(string Code, string Name, string UnitType);

public sealed record CommuneView(string Code, string Name, string UnitType, string ProvinceCode);

public sealed record LegacyUnitView(string? Code, string Name);

/// <summary>A current commune that (wholly or partly) took over the legacy unit. The client must let the user confirm one.</summary>
public sealed record LegacyCandidateView(
    string Code, string Name, string UnitType, string ProvinceCode, string MergeType, LegacyUnitView LegacyDistrict, LegacyUnitView? LegacyCommune);

/// <summary>
/// One legacy unit matching the query: a former commune (LEGACY_COMMUNE) or a former district (LEGACY_DISTRICT, every predecessor
/// commune of it). <see cref="Ambiguous"/> = more than one distinct current commune; <see cref="RequiresConfirmation"/> is always true.
/// </summary>
public sealed record LegacyMatchView(
    string MatchType,
    LegacyUnitView? LegacyCommune,
    LegacyUnitView LegacyDistrict,
    LegacyUnitView LegacyProvince,
    IReadOnlyList<LegacyCandidateView> Candidates,
    bool Ambiguous,
    bool RequiresConfirmation);

/// <summary>Legacy lookup result. Nothing is ever selected for the user: <see cref="RequiresConfirmation"/> is always true.</summary>
public sealed record LegacyLookupView(
    string Query,
    string? ProvinceCode,
    IReadOnlyList<LegacyMatchView> Matches,
    bool Ambiguous,
    bool RequiresConfirmation,
    bool Truncated);

public static class LegacyMatchTypes
{
    public const string LegacyCommune = "LEGACY_COMMUNE";
    public const string LegacyDistrict = "LEGACY_DISTRICT";
}

/// <summary>
/// Public (anonymous) reads of the official catalog: active provinces, active communes of a province, and the legacy lookup that
/// helps a user who only knows a pre-2025 address (old ward / district) find the current commune. Read-only; cache-friendly.
/// </summary>
public sealed class AdministrativeUnitQueries(IAdministrativeCatalogReader reader, TimeProvider time)
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 100;
    public const int MaxLegacyMatches = 50;

    /// <summary>Legacy rows read before grouping (the whole map of a province is a few hundred rows).</summary>
    private const int LegacyRowLimit = 2000;

    public async Task<IReadOnlyList<ProvinceView>> ProvincesAsync(CancellationToken cancellationToken)
        => (await reader.ListActiveProvincesAsync(AdministrativeCatalogClock.Today(time), cancellationToken))
            .Select(p => new ProvinceView(p.Code, p.Name, p.UnitType)).ToList();

    public async Task<OperationResult<IReadOnlyList<CommuneView>>> CommunesAsync(string? provinceCode, string? q, string? unitType, CancellationToken cancellationToken)
    {
        var province = AdministrativeCodeFormats.Normalize(provinceCode);
        if (province is null || !AdministrativeCodeFormats.IsProvinceCode(province))
            return Invalid<IReadOnlyList<CommuneView>>("provinceCode", "'provinceCode' is required and must be a two-digit province code such as \"79\".");
        var type = AdministrativeCodeFormats.Normalize(unitType)?.ToUpperInvariant();
        if (type is not null && !CommuneUnitTypes.All.Contains(type))
            return Invalid<IReadOnlyList<CommuneView>>("unitType", "'unitType' must be WARD, COMMUNE or SPECIAL_ZONE.");
        if (q is { Length: > MaxQueryLength })
            return Invalid<IReadOnlyList<CommuneView>>("q", $"'q' must be at most {MaxQueryLength} characters.");
        var key = VietnameseText.Normalize(q);
        var today = AdministrativeCatalogClock.Today(time);
        var communes = await reader.ListActiveCommunesAsync(province, key.Length == 0 ? null : key, type, today, cancellationToken);
        return OperationResult<IReadOnlyList<CommuneView>>.Ok(communes.Select(c => new CommuneView(c.Code, c.Name, c.UnitType, c.ProvinceCode)).ToList());
    }

    /// <summary>
    /// Matches legacy commune OR legacy district names (accent-insensitive "contains") or codes (exact), restricted to current
    /// communes of <paramref name="provinceCode"/> when given. Grouped per matched legacy unit (communes first, then districts),
    /// at most <see cref="MaxLegacyMatches"/> groups.
    /// </summary>
    public async Task<OperationResult<LegacyLookupView>> LegacyLookupAsync(string? q, string? provinceCode, CancellationToken cancellationToken)
    {
        var raw = q?.Trim() ?? "";
        var key = VietnameseText.Normalize(raw);
        if (key.Length < MinQueryLength)
            return Invalid<LegacyLookupView>("q", $"'q' must contain at least {MinQueryLength} characters.");
        if (raw.Length > MaxQueryLength)
            return Invalid<LegacyLookupView>("q", $"'q' must be at most {MaxQueryLength} characters.");
        var province = AdministrativeCodeFormats.Normalize(provinceCode);
        if (province is not null && !AdministrativeCodeFormats.IsProvinceCode(province))
            return Invalid<LegacyLookupView>("provinceCode", "'provinceCode' must be a two-digit province code such as \"79\".");

        var code = raw.All(char.IsAsciiDigit) ? raw : null;
        var rows = await reader.FindLegacyRowsAsync(key, code, province, AdministrativeCatalogClock.Today(time), LegacyRowLimit, cancellationToken);

        var communeGroups = rows
            .Where(r => (r.Legacy.LegacyCommuneSearchName?.Contains(key, StringComparison.Ordinal) ?? false)
                        || (code is not null && r.Legacy.LegacyCommuneCode == code))
            .GroupBy(r => (r.Legacy.LegacyProvinceCode, r.Legacy.LegacyDistrictCode, Commune: r.Legacy.LegacyCommuneCode ?? r.Legacy.LegacyCommuneName ?? ""))
            .Select(g => Group(LegacyMatchTypes.LegacyCommune, g.ToList()))
            .OrderBy(m => m.LegacyCommune?.Name, StringComparer.Ordinal).ThenBy(m => m.LegacyDistrict.Name, StringComparer.Ordinal);
        var districtGroups = rows
            .Where(r => r.Legacy.LegacyDistrictSearchName.Contains(key, StringComparison.Ordinal) || (code is not null && r.Legacy.LegacyDistrictCode == code))
            .GroupBy(r => (r.Legacy.LegacyProvinceCode, r.Legacy.LegacyDistrictCode))
            .Select(g => Group(LegacyMatchTypes.LegacyDistrict, g.ToList()))
            .OrderBy(m => m.LegacyDistrict.Name, StringComparer.Ordinal);

        var all = communeGroups.Concat(districtGroups).ToList();
        var matches = all.Take(MaxLegacyMatches).ToList();
        var distinctCandidates = matches.SelectMany(m => m.Candidates).Select(c => c.Code).Distinct().Count();
        return OperationResult<LegacyLookupView>.Ok(new LegacyLookupView(raw, province, matches, distinctCandidates > 1, RequiresConfirmation: true,
            Truncated: all.Count > MaxLegacyMatches || rows.Count >= LegacyRowLimit));
    }

    private static LegacyMatchView Group(string matchType, IReadOnlyList<LegacyMapRow> rows)
    {
        var first = rows[0].Legacy;
        var candidates = rows
            .Select(r => new LegacyCandidateView(r.Commune.Code, r.Commune.Name, r.Commune.UnitType, r.Commune.ProvinceCode, r.Legacy.MergeType,
                new LegacyUnitView(r.Legacy.LegacyDistrictCode, r.Legacy.LegacyDistrictName),
                r.Legacy.LegacyCommuneName is null && r.Legacy.LegacyCommuneCode is null ? null : new LegacyUnitView(r.Legacy.LegacyCommuneCode, r.Legacy.LegacyCommuneName ?? "")))
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal).ThenBy(c => c.LegacyCommune?.Code, StringComparer.Ordinal)
            .ToList();
        var legacyCommune = matchType == LegacyMatchTypes.LegacyCommune && (first.LegacyCommuneCode is not null || first.LegacyCommuneName is not null)
            ? new LegacyUnitView(first.LegacyCommuneCode, first.LegacyCommuneName ?? "")
            : null;
        return new LegacyMatchView(matchType, legacyCommune, new LegacyUnitView(first.LegacyDistrictCode, first.LegacyDistrictName),
            new LegacyUnitView(first.LegacyProvinceCode, first.LegacyProvinceName), candidates,
            candidates.Select(c => c.Code).Distinct().Count() > 1, RequiresConfirmation: true);
    }

    private static OperationResult<T> Invalid<T>(string field, string message)
        => OperationResult<T>.Fail(CommonErrorCodes.ValidationFailed, message, new Dictionary<string, string[]> { [field] = [message] });
}
