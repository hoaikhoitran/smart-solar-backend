using SmartSolar.Modules.Catalog.Enums;

namespace SmartSolar.Modules.Catalog.Entities;

public sealed class Product
{
    public Guid Id { get; set; }

    public string Sku { get; set; } = null!;

    public string ProductType { get; set; } = null!;

    public string? Category { get; set; }

    public string Name { get; set; } = null!;

    public string Brand { get; set; } = null!;

    public string? Model { get; set; }

    public string Unit { get; set; } = null!;

    public decimal UnitPrice { get; set; }

    public string Currency { get; set; } = null!;

    public decimal? RatedPowerW { get; set; }

    public decimal? WidthMm { get; set; }

    public decimal? HeightMm { get; set; }

    public int? WarrantyMonth { get; set; }

    /// <summary>Raw JSON object text, stored as jsonb.</summary>
    public string? Spec { get; set; }

    public ProductStatus Status { get; set; }

    public string? ImageUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
