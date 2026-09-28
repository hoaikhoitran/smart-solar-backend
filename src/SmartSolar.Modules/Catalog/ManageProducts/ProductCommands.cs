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

    string? ImageUrl { get; }
}

/// <summary>A new product; <paramref name="Status"/> defaults to ACTIVE when omitted.</summary>
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
    string? Status) : IProductDetails;

/// <summary>Replaces the editable fields. Status changes go through <see cref="ChangeProductStatusCommand"/>.</summary>
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
    string? Spec,
    string? ImageUrl) : IProductDetails;

public sealed record ChangeProductStatusCommand(Guid ProductId, string Status);
