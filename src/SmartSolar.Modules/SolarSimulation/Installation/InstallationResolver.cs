using SmartSolar.Modules.Catalog.Installation;
using SmartSolar.Modules.SolarSimulation.Constants;
using SmartSolar.Modules.SolarSimulation.Options;

namespace SmartSolar.Modules.SolarSimulation.Installation;

/// <summary>
/// Resolves each installation value with this precedence:
/// <list type="number">
/// <item>A documented manufacturer minimum is a floor. A site value below it is rejected, never clamped.</item>
/// <item>A site value from the request is used when it meets the floor.</item>
/// <item>For rack row gap without a site value, the shading estimate is used when the location is known.</item>
/// <item>Otherwise the configured preliminary default is used, raised to the documented minimum when that is higher.</item>
/// </list>
/// Mounting-system requirements are not available in V1 and are reported as a warning elsewhere.
/// </summary>
public static class InstallationResolver
{
    public static InstallationResolution Resolve(
        InstallationRequest request,
        ProductInstallationSpec? productSpec,
        InstallationDefaultsOptions defaults,
        string mountingType,
        bool shadeEstimateAvailable)
    {
        var errors = new List<InstallationError>();
        var requirements = productSpec?.Requirements ?? new Dictionary<string, DocumentedRequirement>();
        var isRack = mountingType == MountingTypes.Rack;

        var panelGap = Resolve(InstallationRequirementKeys.PanelGapMm, request.PanelGapMm, defaults.PanelGapMm, requirements, errors);
        var edge = Resolve(InstallationRequirementKeys.EdgeSetbackMm, request.EdgeSetbackMm, defaults.EdgeSetbackMm, requirements, errors);
        var clearance = Resolve(InstallationRequirementKeys.ObstacleClearanceMm, request.ObstacleClearanceMm, defaults.ObstacleClearanceMm, requirements, errors);

        var lowEdge = isRack
            ? Resolve(InstallationRequirementKeys.RackLowEdgeClearanceMm, request.RackLowEdgeClearanceMm, defaults.RackLowEdgeClearanceMm, requirements, errors)
            : new ResolvedParameter(0m, InstallationSources.NotApplicable, null, null, null, null);

        var rowGap = ResolveRowGap(request.RowGapMm, defaults, requirements, isRack, shadeEstimateAvailable, errors);

        ResolvedParameter? thickness = null;
        if (productSpec?.ModuleThicknessMm is { } documented)
        {
            thickness = new ResolvedParameter(documented.ValueMm, InstallationSources.ManufacturerDocumented, null,
                documented.DocumentRef, documented.Section, documented.Url);
        }
        else if (defaults.ModuleThicknessMm is { } assumed)
        {
            thickness = new ResolvedParameter(assumed, InstallationSources.PreliminaryDefault, null, null, null, null);
        }
        else
        {
            errors.Add(new InstallationError(SimulationErrorCodes.InstallationParameterRequired, "moduleThicknessMm",
                "Module thickness is not documented for this product and no preliminary default is configured."));
        }

        if (errors.Count > 0)
        {
            return new InstallationResolution(null, errors);
        }

        return new InstallationResolution(
            new ResolvedInstallation(panelGap!, edge!, clearance!, lowEdge!, thickness!, rowGap!),
            errors);
    }

    private static ResolvedParameter? Resolve(
        string key,
        decimal? requested,
        decimal? preliminaryDefault,
        IReadOnlyDictionary<string, DocumentedRequirement> requirements,
        List<InstallationError> errors)
    {
        requirements.TryGetValue(key, out var documented);

        if (requested is { } value)
        {
            if (documented is not null && value < documented.MinMm)
            {
                errors.Add(new InstallationError(SimulationErrorCodes.InstallationBelowDocumentedMinimum, key,
                    $"'{key}' is {value} mm, below the documented minimum of {documented.MinMm} mm ({documented.DocumentRef})."));
                return null;
            }

            return new ResolvedParameter(value, InstallationSources.SiteSpecified,
                documented?.MinMm, documented?.DocumentRef, documented?.Section, documented?.Url);
        }

        return FromDefault(key, preliminaryDefault, documented, errors);
    }

    private static ResolvedParameter? FromDefault(
        string key, decimal? preliminaryDefault, DocumentedRequirement? documented, List<InstallationError> errors)
    {
        if (preliminaryDefault is { } fallback && (documented is null || fallback >= documented.MinMm))
        {
            return new ResolvedParameter(fallback, InstallationSources.PreliminaryDefault,
                documented?.MinMm, documented?.DocumentRef, documented?.Section, documented?.Url);
        }

        if (documented is not null)
        {
            return new ResolvedParameter(documented.MinMm, InstallationSources.ManufacturerDocumentedMinimum,
                documented.MinMm, documented.DocumentRef, documented.Section, documented.Url);
        }

        errors.Add(new InstallationError(SimulationErrorCodes.InstallationParameterRequired, key,
            $"'{key}' is required because no preliminary default is configured."));
        return null;
    }

    private static RowGapPolicy? ResolveRowGap(
        decimal? requested,
        InstallationDefaultsOptions defaults,
        IReadOnlyDictionary<string, DocumentedRequirement> requirements,
        bool isRack,
        bool shadeEstimateAvailable,
        List<InstallationError> errors)
    {
        const string key = InstallationRequirementKeys.RowGapMm;
        requirements.TryGetValue(key, out var documented);

        if (requested is null && isRack && shadeEstimateAvailable)
        {
            return new RowGapPolicy(null, null, documented?.MinMm, documented?.DocumentRef, documented?.Section, documented?.Url, true);
        }

        var resolved = requested is not null
            ? Resolve(key, requested, null, requirements, errors)
            : FromDefault(key, isRack ? defaults.RackRowGapFallbackMm : defaults.FlushRowGapMm, documented, errors);

        return resolved is null
            ? null
            : new RowGapPolicy(resolved.ValueMm, resolved.Source, resolved.DocumentedMinimumMm,
                resolved.DocumentRef, resolved.DocumentSection, resolved.DocumentUrl, false);
    }
}
