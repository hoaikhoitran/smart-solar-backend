namespace SmartSolar.Modules.Catalog.Installation;

/// <summary>Requirement keys allowed inside Product.Spec.installation.requirements.</summary>
public static class InstallationRequirementKeys
{
    public const string PanelGapMm = "panelGapMm";
    public const string RowGapMm = "rowGapMm";
    public const string EdgeSetbackMm = "edgeSetbackMm";
    public const string ObstacleClearanceMm = "obstacleClearanceMm";
    public const string RackLowEdgeClearanceMm = "rackLowEdgeClearanceMm";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PanelGapMm, RowGapMm, EdgeSetbackMm, ObstacleClearanceMm, RackLowEdgeClearanceMm
    };
}

/// <summary>
/// A minimum recorded from manufacturer documentation by catalog staff. SmartSolar
/// does not independently verify it; the document reference keeps it traceable.
/// </summary>
public sealed record DocumentedRequirement(
    decimal MinMm,
    string DocumentRef,
    string? Section,
    string? Url,
    string? Notes);

/// <summary>A documented physical dimension (not a minimum), e.g. module thickness.</summary>
public sealed record DocumentedDimension(
    decimal ValueMm,
    string DocumentRef,
    string? Section,
    string? Url,
    string? Notes);

public sealed record ProductInstallationSpec(
    int SchemaVersion,
    IReadOnlyDictionary<string, DocumentedRequirement> Requirements,
    DocumentedDimension? ModuleThicknessMm);

public sealed record InstallationSpecParseResult(
    bool Present,
    ProductInstallationSpec? Spec,
    IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
