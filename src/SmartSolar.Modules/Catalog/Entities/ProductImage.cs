namespace SmartSolar.Modules.Catalog.Entities;

/// <summary>
/// One gallery image of a product. A product has at most one primary image,
/// each sort position once and each URL once (all enforced by unique indexes).
/// </summary>
public sealed class ProductImage
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    /// <summary>Absolute https URL, at most 1000 characters.</summary>
    public string Url { get; set; } = null!;

    public string? AltText { get; set; }

    /// <summary>Zero-based display position; unique per product.</summary>
    public int SortOrder { get; set; }

    public bool IsPrimary { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }
}
