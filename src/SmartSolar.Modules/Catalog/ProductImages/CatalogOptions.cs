namespace SmartSolar.Modules.Catalog.ProductImages;

/// <summary>Catalog settings bound from the "Catalog" configuration section.</summary>
public sealed class CatalogOptions
{
    public const string SectionName = "Catalog";

    public const int DefaultMaxImagesPerProduct = 10;

    /// <summary>Upper bound of a product's gallery, enforced on create and on every add.</summary>
    public int MaxImagesPerProduct { get; set; } = DefaultMaxImagesPerProduct;

    /// <summary>
    /// When non-empty, image URLs must use one of these hosts (exact host match,
    /// case-insensitive). Empty means any https host is accepted.
    /// </summary>
    public string[] ImageAllowedHosts { get; set; } = [];
}
