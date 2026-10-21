using System.Text.Json.Serialization;
using SmartSolar.Modules.Catalog.Enums;

namespace SmartSolar.Modules.Catalog.Models;

/// <summary>
/// Read model for a product. Also the value cached under
/// catalog:product:v2:{id}, so it must stay JSON round-trippable.
/// <see cref="Images"/> is ordered by sort order.
/// </summary>
public sealed record ProductDto(
    Guid Id,
    string Sku,
    string ProductType,
    string? Category,
    string Name,
    string Brand,
    string? Model,
    string Unit,
    decimal UnitPrice,
    string Currency,
    decimal? RatedPowerW,
    decimal? WidthMm,
    decimal? HeightMm,
    int? WarrantyMonth,
    string? Spec,
    ProductStatus Status,
    IReadOnlyList<ProductImageDto> Images,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Deprecated single-image field: the primary image's URL, derived from <see cref="Images"/>.</summary>
    [JsonIgnore]
    public string? ImageUrl => Images?.FirstOrDefault(i => i.IsPrimary)?.Url;
}
