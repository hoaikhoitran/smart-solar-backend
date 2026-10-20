namespace SmartSolar.Modules.Catalog.Constants;

public static class CatalogCacheKeys
{
    public static readonly TimeSpan ProductTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// v2: the cached read model carries the image gallery. The version bump
    /// keeps entries written by the previous shape from being read back.
    /// </summary>
    public static string Product(Guid productId) => $"catalog:product:v2:{productId}";
}
