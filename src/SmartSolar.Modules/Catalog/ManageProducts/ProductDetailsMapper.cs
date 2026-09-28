using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Entities;

namespace SmartSolar.Modules.Catalog.ManageProducts;

/// <summary>Copies validated, normalized editable fields onto an entity.</summary>
internal static class ProductDetailsMapper
{
    public static string NormalizeSku(string sku) => sku.Trim();

    public static void Apply(Product product, IProductDetails details)
    {
        product.Sku = NormalizeSku(details.Sku);
        product.ProductType = ProductTypes.Normalize(details.ProductType);
        product.Category = NullIfBlank(details.Category);
        product.Name = details.Name.Trim();
        product.Brand = details.Brand.Trim();
        product.Model = NullIfBlank(details.Model);
        product.Unit = details.Unit.Trim();
        product.UnitPrice = details.UnitPrice!.Value;
        product.Currency = details.Currency.Trim().ToUpperInvariant();
        product.RatedPowerW = details.RatedPowerW;
        product.WidthMm = details.WidthMm;
        product.HeightMm = details.HeightMm;
        product.WarrantyMonth = details.WarrantyMonth;
        product.Spec = details.Spec;
        product.ImageUrl = details.ImageUrl;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
