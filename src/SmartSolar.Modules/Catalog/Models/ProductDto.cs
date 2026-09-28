using SmartSolar.Modules.Catalog.Enums;

namespace SmartSolar.Modules.Catalog.Models;

/// <summary>
/// Read model for a product. Also the value cached under
/// catalog:product:{id}, so it must stay JSON round-trippable.
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
    string? ImageUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
