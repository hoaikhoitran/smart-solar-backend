namespace SmartSolar.Modules.Catalog.Constants;

/// <summary>
/// Product types with catalog rules attached. product_type is free text so
/// other types can be added without a schema change; only the types listed
/// here carry extra validation.
/// </summary>
public static class ProductTypes
{
    public const string SolarPanel = "SOLAR_PANEL";
    public const string Inverter = "INVERTER";

    // Priced services (catalog items with a unit price per unit such as JOB, KWP or TRIP). The automatic installation
    // quotation prices the technician's confirmed service items from these instead of anyone typing an amount.
    public const string InstallationLabor = "INSTALLATION_LABOR";
    public const string Transport = "TRANSPORT";
    public const string OtherService = "OTHER_SERVICE";

    public static readonly IReadOnlyList<string> ServiceTypes = [InstallationLabor, Transport, OtherService];

    /// <summary>True for a priced service type (labor, transport, other service); false for equipment.</summary>
    public static bool IsService(string? productType)
        => productType is not null && ServiceTypes.Contains(Normalize(productType));

    /// <summary>
    /// Physical equipment sold by whole units (2026-11-25): a quotation line of these types must have a positive integer quantity. Other types keep the
    /// existing decimal precision (services; equipment types not listed here need a catalog decision before being restricted).
    /// </summary>
    public static readonly IReadOnlyList<string> WholeUnitTypes = [SolarPanel, Inverter];

    public static bool RequiresWholeQuantity(string? productType)
        => productType is not null && WholeUnitTypes.Contains(Normalize(productType));

    /// <summary>Product types are compared and stored upper-cased.</summary>
    public static string Normalize(string productType) => productType.Trim().ToUpperInvariant();
}
