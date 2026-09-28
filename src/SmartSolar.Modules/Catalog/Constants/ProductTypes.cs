namespace SmartSolar.Modules.Catalog.Constants;

/// <summary>
/// Product types with catalog rules attached. product_type is free text so
/// other types can be added without a schema change; only the types listed
/// here carry extra validation.
/// </summary>
public static class ProductTypes
{
    public const string SolarPanel = "SOLAR_PANEL";

    /// <summary>Product types are compared and stored upper-cased.</summary>
    public static string Normalize(string productType) => productType.Trim().ToUpperInvariant();
}
