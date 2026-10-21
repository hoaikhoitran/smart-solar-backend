using SmartSolar.Modules.Catalog.ProductImages;

namespace SmartSolar.Modules.Catalog.ManageProducts;

/// <summary>
/// The client-editable product fields. Id, CreatedAt, UpdatedAt and DeletedAt
/// are deliberately absent: the backend owns them.
/// </summary>
public interface IProductDetails
{
    string Sku { get; }

    string ProductType { get; }

    string? Category { get; }

    string Name { get; }

    string Brand { get; }

    string? Model { get; }

    string Unit { get; }

    decimal? UnitPrice { get; }

    string Currency { get; }

    decimal? RatedPowerW { get; }

    decimal? WidthMm { get; }

    decimal? HeightMm { get; }

    int? WarrantyMonth { get; }

    /// <summary>Raw JSON text; must be a JSON object when present.</summary>
    string? Spec { get; }
}

/// <summary>
/// A new product; <paramref name="Status"/> defaults to ACTIVE when omitted.
/// <paramref name="Images"/> seeds the gallery (the first image is primary).
/// <paramref name="ImageUrl"/> is the deprecated single-image field: used as the
/// only (primary) image when <paramref name="Images"/> is empty, and otherwise it
/// must equal the first image's URL.
/// </summary>
public sealed record CreateProductCommand(
    string Sku,
    string ProductType,
    string? Category,
    string Name,
    string Brand,
    string? Model,
    string Unit,
    decimal? UnitPrice,
    string Currency,
    decimal? RatedPowerW,
    decimal? WidthMm,
    decimal? HeightMm,
    int? WarrantyMonth,
    string? Spec,
    string? ImageUrl,
    string? Status,
    IReadOnlyList<ProductImageInput>? Images = null) : IProductDetails;

/// <summary>
/// Replaces the editable fields. Status changes go through <see cref="ChangeProductStatusCommand"/>;
/// the gallery is managed only through the image operations.
/// </summary>
public sealed record UpdateProductCommand(
    Guid ProductId,
    string Sku,
    string ProductType,
    string? Category,
    string Name,
    string Brand,
    string? Model,
    string Unit,
    decimal? UnitPrice,
    string Currency,
    decimal? RatedPowerW,
    decimal? WidthMm,
    decimal? HeightMm,
    int? WarrantyMonth,
    string? Spec) : IProductDetails;

public sealed record ChangeProductStatusCommand(Guid ProductId, string Status);
