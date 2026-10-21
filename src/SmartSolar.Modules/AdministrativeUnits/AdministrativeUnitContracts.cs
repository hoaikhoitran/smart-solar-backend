namespace SmartSolar.Modules.AdministrativeUnits;

/// <summary>A legacy (pre-2025) predecessor row joined with the current commune it maps to.</summary>
public sealed record LegacyMapRow(AdministrativeLegacyCommuneMap Legacy, AdministrativeCommune Commune);

/// <summary>
/// Read side of the official administrative catalog. "Active on <c>asOf</c>" = valid_from &lt;= asOf and
/// (valid_to is null or valid_to &gt; asOf).
/// </summary>
public interface IAdministrativeCatalogReader
{
    Task<IReadOnlyList<AdministrativeProvince>> ListActiveProvincesAsync(DateOnly asOf, CancellationToken cancellationToken);

    Task<AdministrativeProvince?> FindProvinceAsync(string code, CancellationToken cancellationToken);

    Task<AdministrativeCommune?> FindCommuneAsync(string code, CancellationToken cancellationToken);

    /// <summary>Active communes of the province, optionally whose search name contains <paramref name="searchKey"/> (normalized) and of a unit type, by name.</summary>
    Task<IReadOnlyList<AdministrativeCommune>> ListActiveCommunesAsync(string provinceCode, string? searchKey, string? unitType, DateOnly asOf,
        CancellationToken cancellationToken);

    /// <summary>Active communes with these codes (unknown or inactive codes are left out).</summary>
    Task<IReadOnlyList<AdministrativeCommune>> GetActiveCommunesAsync(IReadOnlyCollection<string> codes, DateOnly asOf, CancellationToken cancellationToken);

    /// <summary>
    /// Legacy rows whose legacy commune or legacy district search name contains <paramref name="searchKey"/>, or whose legacy
    /// commune/district code equals <paramref name="code"/>; only rows mapping to an ACTIVE current commune (of
    /// <paramref name="provinceCode"/> when given). At most <paramref name="limit"/> rows.
    /// </summary>
    Task<IReadOnlyList<LegacyMapRow>> FindLegacyRowsAsync(string searchKey, string? code, string? provinceCode, DateOnly asOf, int limit,
        CancellationToken cancellationToken);
}

/// <summary>Business date of the catalog: the calendar date in Vietnam (UTC+07:00, no daylight saving).</summary>
public static class AdministrativeCatalogClock
{
    public static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);

    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(time.GetUtcNow().ToOffset(VietnamOffset).DateTime);

    public static bool IsActive(DateOnly validFrom, DateOnly? validTo, DateOnly asOf) => validFrom <= asOf && (validTo is null || validTo > asOf);
}

/// <summary>Why a (province, commune) pair cannot be stored on a site: field name, stable code and a safe message.</summary>
public sealed record LocationProblem(string Field, string Code, string Message);

/// <summary>
/// Validation of a site's administrative location against the ACTIVE catalog. Formats first (province two digits, commune five
/// digits, leading zeros kept; a commune requires its province), then existence, membership (the commune belongs to the
/// province) and validity: a code closed by a newer dataset is refused for new values (ADMINISTRATIVE_LOCATION_SUPERSEDED).
/// </summary>
public static class AdministrativeLocationRules
{
    /// <summary>SmartSolar serves the whole (merged) Ho Chi Minh City — province 79 — and nothing else.</summary>
    public static readonly IReadOnlySet<string> ServiceProvinceCodes = new HashSet<string>(StringComparer.Ordinal) { "79" };

    /// <summary>Format-only check (validation layer, 400): null when consistent, otherwise (field, message).</summary>
    public static (string Field, string Message)? CheckFormat(string? provinceCode, string? communeCode, bool communeRequired)
    {
        if (provinceCode is null && communeCode is null && !communeRequired) return null;
        if (provinceCode is null)
            return ("provinceCode", "'provinceCode' is required when a commune code is given.");
        if (!AdministrativeCodeFormats.IsProvinceCode(provinceCode))
            return ("provinceCode", "'provinceCode' must be a two-digit official province code such as \"79\".");
        if (communeCode is null)
            return communeRequired ? ("communeCode", "'communeCode' is required.") : null;
        if (!AdministrativeCodeFormats.IsCommuneCode(communeCode))
            return ("communeCode", "'communeCode' must be a five-digit official commune code such as \"26740\".");
        return null;
    }

    /// <summary>Catalog check of already well-formed codes: null when both exist, are active on <paramref name="asOf"/> and belong together.</summary>
    public static async Task<LocationProblem?> CheckCatalogAsync(IAdministrativeCatalogReader reader, string? provinceCode, string? communeCode, DateOnly asOf,
        CancellationToken cancellationToken)
    {
        if (provinceCode is null) return null;
        // Out of scope first: a site elsewhere in Vietnam is not a temporarily uncovered area, it is outside the service area.
        if (!ServiceProvinceCodes.Contains(provinceCode))
            return new("provinceCode", AdministrativeUnitErrorCodes.LocationOutOfServiceArea,
                $"Province code '{provinceCode}' is outside SmartSolar's service area (Ho Chi Minh City, province 79).");
        var province = await reader.FindProvinceAsync(provinceCode, cancellationToken);
        if (province is null)
            return new("provinceCode", AdministrativeUnitErrorCodes.LocationUnknown, $"Province code '{provinceCode}' is not in the official catalog.");
        if (!AdministrativeCatalogClock.IsActive(province.ValidFrom, province.ValidTo, asOf))
            return new("provinceCode", AdministrativeUnitErrorCodes.LocationSuperseded, $"Province code '{provinceCode}' is no longer in force.");
        if (communeCode is null) return null;
        var commune = await reader.FindCommuneAsync(communeCode, cancellationToken);
        if (commune is null || commune.ProvinceCode != provinceCode)
            return new("communeCode", AdministrativeUnitErrorCodes.LocationUnknown,
                $"Commune code '{communeCode}' is not an official commune of province '{provinceCode}'.");
        if (!AdministrativeCatalogClock.IsActive(commune.ValidFrom, commune.ValidTo, asOf))
            return new("communeCode", AdministrativeUnitErrorCodes.LocationSuperseded,
                $"Commune code '{communeCode}' is no longer in force. Choose a current commune (the legacy lookup helps to find it).");
        return null;
    }
}
