namespace SmartSolar.Modules.SolarSimulation.Constants;

public static class MountingTypes
{
    /// <summary>Panels parallel to the surface (rails with a small air gap).</summary>
    public const string Flush = "FLUSH";

    /// <summary>Panels on a tilted support structure with their own absolute tilt and azimuth.</summary>
    public const string Rack = "RACK";

    public static bool IsKnown(string? value) => value is Flush or Rack;
}

public static class SimulationStatusCodes
{
    public const string Completed = "COMPLETED";
    public const string PartiallyCompleted = "PARTIALLY_COMPLETED";
}

public static class ComponentStatusCodes
{
    public const string Succeeded = "SUCCEEDED";
    public const string NotApplicable = "NOT_APPLICABLE";
    public const string Unavailable = "UNAVAILABLE";
    public const string Failed = "FAILED";
}

public static class AlgorithmVersions
{
    /// <summary>Bump when layout, footprint, shading or energy-mapping logic changes, so fingerprints change.</summary>
    public const string Current = "layout-v1";

    /// <summary>Version of the canonical fingerprint document.</summary>
    public const string Fingerprint = "fp-v1";

    /// <summary>Version of the PVGIS mounting-place mapping (flush: building primary + free comparison; rack: free).</summary>
    public const string PvgisMountingMapping = "pvgis-mounting-v1";
}

public static class SimulationErrorCodes
{
    public const string ValidationFailed = "SIMULATION_VALIDATION_FAILED";
    public const string SurfaceNotDefined = "SURFACE_NOT_DEFINED";
    public const string ProductNotFound = "PRODUCT_NOT_FOUND";
    public const string ProductNotSolarPanel = "PRODUCT_NOT_SOLAR_PANEL";
    public const string ProductSpecIncomplete = "PRODUCT_SPEC_INCOMPLETE";
    public const string InstallationBelowDocumentedMinimum = "INSTALLATION_BELOW_DOCUMENTED_MINIMUM";
    public const string InstallationParameterRequired = "INSTALLATION_PARAMETER_REQUIRED";
    public const string PanelFacesIntoSurface = "PANEL_FACES_INTO_SURFACE";
    public const string PanelOrientationUnsupported = "PANEL_ORIENTATION_UNSUPPORTED";
    public const string LayoutLimitExceeded = "SIMULATION_LAYOUT_LIMIT_EXCEEDED";
    public const string SourceChanged = "SIMULATION_SOURCE_CHANGED";
    public const string SimulationNotFound = "SIMULATION_NOT_FOUND";
    public const string AccessDenied = "SIMULATION_ACCESS_DENIED";
}

/// <summary>Warning codes returned with every simulation so the frontend can show limitations.</summary>
public static class SimulationWarningCodes
{
    public const string StructureNotAssessed = "STRUCTURE_NOT_ASSESSED";
    public const string MountingSystemRequirementsUnavailable = "MOUNTING_SYSTEM_REQUIREMENTS_UNAVAILABLE";
    public const string BuildingFireCodeNotChecked = "BUILDING_FIRE_CODE_NOT_CHECKED";
    public const string PreliminaryInstallationValues = "PRELIMINARY_INSTALLATION_VALUES";
    public const string SiteValuesNotVerified = "SITE_VALUES_NOT_VERIFIED";
    public const string ManufacturerValuesNotIndependentlyVerified = "MANUFACTURER_VALUES_NOT_INDEPENDENTLY_VERIFIED";
    public const string ProductInstallationSpecInvalid = "PRODUCT_INSTALLATION_SPEC_INVALID";
    public const string ModuleThicknessAssumed = "MODULE_THICKNESS_ASSUMED";
    public const string RackOnInclinedSurface = "RACK_ON_INCLINED_SURFACE";
    public const string FootprintConservativeBoundingBox = "FOOTPRINT_CONSERVATIVE_BOUNDING_BOX";
    public const string RackSupportHeightAbovePreliminaryThreshold = "RACK_SUPPORT_HEIGHT_ABOVE_PRELIMINARY_THRESHOLD";
    public const string ShadeSpacingNotEvaluated = "SHADE_SPACING_NOT_EVALUATED";
    public const string RowGapBelowShadeEstimate = "ROW_GAP_BELOW_SHADE_ESTIMATE";
    public const string ObstacleShadingNotModeled = "OBSTACLE_SHADING_NOT_MODELED";
    public const string DeclaredAreaDiffers = "DECLARED_AREA_DIFFERS";
    public const string LocationMissing = "LOCATION_MISSING";
    public const string FlushMountThermalAssumption = "FLUSH_MOUNT_THERMAL_ASSUMPTION";
    public const string EnergyProviderFailed = "ENERGY_PROVIDER_FAILED";
    public const string ClimateProviderFailed = "CLIMATE_PROVIDER_FAILED";
}

public sealed record SimulationWarning(string Code, string Message);
