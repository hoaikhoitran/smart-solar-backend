namespace SmartSolar.Modules.AdministrativeUnits;

/// <summary>
/// An official province-level unit (Quyết định 19/2025/QĐ-TTg codes: two digits, leading zeros kept, e.g. "01", "79").
/// Reference data loaded only by the controlled catalog import (CLI); never edited through the API.
/// <see cref="ValidTo"/> = null while the code is in force.
/// </summary>
public sealed class AdministrativeProvince
{
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string UnitType { get; set; } = null!;
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public string DatasetKey { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An official commune-level unit after the 2025 reorganisation (phường / xã / đặc khu): five-digit code, leading zeros kept.
/// There is no district level any more. A code that disappears from a newer dataset is closed (<see cref="ValidTo"/>), never deleted.
/// </summary>
public sealed class AdministrativeCommune
{
    public string Code { get; set; } = null!;
    public string ProvinceCode { get; set; } = null!;
    public string Name { get; set; } = null!;

    /// <summary>Accent-free, lower-case form of <see cref="Name"/> (see <see cref="VietnameseText.Normalize"/>) for search.</summary>
    public string SearchName { get; set; } = null!;

    public string UnitType { get; set; } = null!;
    public DateOnly ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }
    public string DatasetKey { get; set; } = null!;
}

/// <summary>
/// One predecessor of a current commune (official conversion table, old → new). A current commune usually has several
/// predecessors; a predecessor split between several current communes (PARTIAL) appears once per current commune.
/// <see cref="LegacyCommuneCode"/> = null for a district-level predecessor (huyện Côn Đảo → đặc khu Côn Đảo).
/// <see cref="MatchKey"/> = "{legacy_district_code}:{legacy_commune_code or '-'}" makes (commune_code, legacy district,
/// legacy commune) unique with NULL treated as a value (EF Core 8 has no NULLS NOT DISTINCT); a check constraint keeps it
/// consistent with the two columns.
/// </summary>
public sealed class AdministrativeLegacyCommuneMap
{
    public Guid Id { get; set; }
    public string CommuneCode { get; set; } = null!;
    public string? LegacyCommuneCode { get; set; }
    public string? LegacyCommuneName { get; set; }
    public string? LegacyCommuneSearchName { get; set; }
    public string LegacyDistrictCode { get; set; } = null!;
    public string LegacyDistrictName { get; set; } = null!;
    public string LegacyDistrictSearchName { get; set; } = null!;
    public string LegacyProvinceCode { get; set; } = null!;
    public string LegacyProvinceName { get; set; } = null!;
    public string MergeType { get; set; } = null!;
    public string? SourceNote { get; set; }
    public string MatchKey { get; set; } = null!;
    public string DatasetKey { get; set; } = null!;

    public static string KeyOf(string legacyDistrictCode, string? legacyCommuneCode) => $"{legacyDistrictCode}:{legacyCommuneCode ?? "-"}";
}

/// <summary>Audit of a catalog dataset import (dry runs are not recorded; an applied dataset key can never be re-applied with other files).</summary>
public sealed class AdministrativeCatalogImport
{
    public Guid Id { get; set; }
    public string DatasetKey { get; set; } = null!;
    public string SourceUrl { get; set; } = null!;
    public string SourceSha256 { get; set; } = null!;
    public DateOnly EffectiveFrom { get; set; }

    /// <summary>JSON object file name → sha256 (jsonb).</summary>
    public string FilesSha256 { get; set; } = null!;

    /// <summary>JSON object of the manifest counts and of what the apply changed (jsonb).</summary>
    public string Counts { get; set; } = null!;

    public string Status { get; set; } = null!;
    public DateTimeOffset ValidatedAt { get; set; }
    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Operator name given on the command line (free text, never a credential).</summary>
    public string? AppliedBy { get; set; }

    public string? Notes { get; set; }
}

public static class ProvinceUnitTypes
{
    public const string Province = "PROVINCE";
    public const string Municipality = "MUNICIPALITY";
    public static readonly IReadOnlyList<string> All = [Province, Municipality];
}

public static class CommuneUnitTypes
{
    public const string Ward = "WARD";
    public const string Commune = "COMMUNE";
    public const string SpecialZone = "SPECIAL_ZONE";
    public static readonly IReadOnlyList<string> All = [Ward, Commune, SpecialZone];
}

public static class LegacyMergeTypes
{
    /// <summary>The whole predecessor became part of the current commune.</summary>
    public const string Whole = "WHOLE";

    /// <summary>Only part of the predecessor: its territory is split between several current communes (always ambiguous).</summary>
    public const string Partial = "PARTIAL";

    /// <summary>Kept as is (possibly renamed).</summary>
    public const string Unchanged = "UNCHANGED";

    public static readonly IReadOnlyList<string> All = [Whole, Partial, Unchanged];
}

public static class CatalogImportStatuses
{
    public const string Validated = "VALIDATED";
    public const string Applied = "APPLIED";
    public const string Rejected = "REJECTED";
    public static readonly IReadOnlyList<string> All = [Validated, Applied, Rejected];
}

public static class AdministrativeUnitErrorCodes
{
    /// <summary>The province or commune code is not in the catalog, or the commune is not inside the province.</summary>
    public const string LocationUnknown = "ADMINISTRATIVE_LOCATION_UNKNOWN";

    /// <summary>The code exists but is no longer in force (closed by a newer dataset): refused for new values.</summary>
    public const string LocationSuperseded = "ADMINISTRATIVE_LOCATION_SUPERSEDED";

    /// <summary>A well-formed province outside SmartSolar's service area (Ho Chi Minh City, province 79): out of scope, not dispatchable.</summary>
    public const string LocationOutOfServiceArea = "ADMINISTRATIVE_LOCATION_OUT_OF_SERVICE_AREA";
}

/// <summary>Code formats of the official catalog (strings: leading zeros are significant).</summary>
public static class AdministrativeCodeFormats
{
    public const int ProvinceCodeLength = 2;
    public const int CommuneCodeLength = 5;
    public const int LegacyDistrictCodeLength = 3;

    public static bool IsProvinceCode(string? code) => IsDigits(code, ProvinceCodeLength);

    public static bool IsCommuneCode(string? code) => IsDigits(code, CommuneCodeLength);

    public static bool IsLegacyDistrictCode(string? code) => IsDigits(code, LegacyDistrictCodeLength);

    public static string? Normalize(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    private static bool IsDigits(string? code, int length) => code is not null && code.Length == length && code.All(char.IsAsciiDigit);
}
