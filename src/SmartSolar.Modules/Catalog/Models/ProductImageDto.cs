namespace SmartSolar.Modules.Catalog.Models;

/// <summary>One gallery image in the product read model.</summary>
public sealed record ProductImageDto(
    Guid Id,
    string Url,
    string? AltText,
    int SortOrder,
    bool IsPrimary);
