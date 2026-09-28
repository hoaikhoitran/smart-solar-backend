namespace SmartSolar.Modules.Catalog.Constants;

public static class CatalogCacheKeys
{
    public static readonly TimeSpan ProductTtl = TimeSpan.FromMinutes(10);

    public static string Product(Guid productId) => $"catalog:product:{productId}";
}
