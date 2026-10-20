using System.Linq.Expressions;
using SmartSolar.Modules.Catalog.Entities;

namespace SmartSolar.Modules.Catalog.Models;

/// <summary>
/// The single entity-to-read-model mapping: translated to SQL by persistence
/// queries (the gallery is projected in the same query, so lists never issue
/// one image query per product), and compiled for entities already in memory
/// after a write (their Images must be loaded).
/// </summary>
public static class ProductProjection
{
    public static readonly Expression<Func<Product, ProductDto>> ToDto = p => new ProductDto(
        p.Id,
        p.Sku,
        p.ProductType,
        p.Category,
        p.Name,
        p.Brand,
        p.Model,
        p.Unit,
        p.UnitPrice,
        p.Currency,
        p.RatedPowerW,
        p.WidthMm,
        p.HeightMm,
        p.WarrantyMonth,
        p.Spec,
        p.Status,
        p.Images
            .OrderBy(i => i.SortOrder)
            .Select(i => new ProductImageDto(i.Id, i.Url, i.AltText, i.SortOrder, i.IsPrimary))
            .ToList(),
        p.CreatedAt,
        p.UpdatedAt);

    private static readonly Func<Product, ProductDto> Compiled = ToDto.Compile();

    public static ProductDto Map(Product product) => Compiled(product);
}
