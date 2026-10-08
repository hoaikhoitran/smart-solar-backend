namespace SmartSolar.Modules.SolarSimulation.Installation;

/// <summary>Where an installation value came from. Shown to the frontend with every value.</summary>
public static class InstallationSources
{
    /// <summary>A documented manufacturer minimum recorded by catalog staff (not independently verified).</summary>
    public const string ManufacturerDocumentedMinimum = "MANUFACTURER_DOCUMENTED_MINIMUM";

    /// <summary>A documented manufacturer dimension, e.g. module thickness.</summary>
    public const string ManufacturerDocumented = "MANUFACTURER_DOCUMENTED";

    /// <summary>Entered for this site in the simulation request; not verified.</summary>
    public const string SiteSpecified = "SITE_SPECIFIED";

    /// <summary>Configurable preliminary assumption; not a certified or regulatory value.</summary>
    public const string PreliminaryDefault = "PRELIMINARY_DEFAULT";

    /// <summary>Advisory row gap from the solstice 09:00–15:00 shading estimate.</summary>
    public const string ComputedShadeEstimate = "COMPUTED_SHADE_ESTIMATE";

    public const string NotApplicable = "NOT_APPLICABLE";
}

/// <summary>Spacing values requested by the client, in millimetres. Null means "use the resolved default".</summary>
public sealed record InstallationRequest(
    decimal? PanelGapMm,
    decimal? RowGapMm,
    decimal? EdgeSetbackMm,
    decimal? ObstacleClearanceMm,
    decimal? RackLowEdgeClearanceMm);

public sealed record ResolvedParameter(
    decimal ValueMm,
    string Source,
    decimal? DocumentedMinimumMm,
    string? DocumentRef,
    string? DocumentSection,
    string? DocumentUrl);

/// <summary>
/// Row gap is resolved per layout orientation because the shading estimate depends on the
/// footprint. Either a fixed value (site or default) or the shading estimate is used.
/// </summary>
public sealed record RowGapPolicy(
    decimal? FixedValueMm,
    string? FixedSource,
    decimal? DocumentedMinimumMm,
    string? DocumentRef,
    string? DocumentSection,
    string? DocumentUrl,
    bool UseShadeEstimate);

public sealed record ResolvedInstallation(
    ResolvedParameter PanelGap,
    ResolvedParameter EdgeSetback,
    ResolvedParameter ObstacleClearance,
    ResolvedParameter RackLowEdgeClearance,
    ResolvedParameter ModuleThickness,
    RowGapPolicy RowGap);

public sealed record InstallationError(string Code, string Parameter, string Message);

public sealed record InstallationResolution(
    ResolvedInstallation? Installation,
    IReadOnlyList<InstallationError> Errors);
