using System.Text.Json;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Models;

namespace SmartSolar.Api.Contracts.Catalog;

/// <summary>Query-string filters for GET /api/products; everything is optional.</summary>
public sealed class ProductListRequest
{
    public string? Search { get; init; }

    public string? ProductType { get; init; }

    public string? Category { get; init; }

    public string? Brand { get; init; }

    public decimal? MinPower { get; init; }

    public decimal? MaxPower { get; init; }

    public decimal? MinPrice { get; init; }

    public decimal? MaxPrice { get; init; }

    public int? Page { get; init; }

    public int? PageSize { get; init; }

    public string? SortBy { get; init; }

    public string? SortDirection { get; init; }
}

/// <summary>
/// Status is optional and defaults to ACTIVE. Images seeds the gallery in order,
/// the first image being primary. ImageUrl is deprecated: accepted as the single
/// primary image when Images is absent, otherwise it must equal Images[0].url.
/// </summary>
public sealed record CreateProductRequest(
    string? Sku,
    string? ProductType,
    string? Category,
    string? Name,
    string? Brand,
    string? Model,
    string? Unit,
    decimal? UnitPrice,
    string? Currency,
    decimal? RatedPowerW,
    decimal? WidthMm,
    decimal? HeightMm,
    int? WarrantyMonth,
    JsonElement? Spec,
    string? ImageUrl,
    string? Status,
    IReadOnlyList<ProductImageRequest>? Images = null);

public sealed record ProductImageRequest(string? Url, string? AltText);

/// <summary>
/// The editable fields only. There is no id, status, timestamp or image here, so a
/// client cannot overwrite them through this endpoint (a legacy imageUrl property
/// is ignored; the gallery has its own endpoints).
/// </summary>
public sealed record UpdateProductRequest(
    string? Sku,
    string? ProductType,
    string? Category,
    string? Name,
    string? Brand,
    string? Model,
    string? Unit,
    decimal? UnitPrice,
    string? Currency,
    decimal? RatedPowerW,
    decimal? WidthMm,
    decimal? HeightMm,
    int? WarrantyMonth,
    JsonElement? Spec);

public sealed record ChangeProductStatusRequest(string? Status);

public sealed record AddProductImageRequest(string? Url, string? AltText, bool? MakePrimary);

/// <summary>Must list exactly the product's current image ids, in the new order.</summary>
public sealed record ReorderProductImagesRequest(IReadOnlyList<Guid>? ImageIds);

public sealed record ProductImageResponse(Guid Id, string Url, string? AltText, int SortOrder, bool IsPrimary)
{
    public static ProductImageResponse From(ProductImageDto image)
        => new(image.Id, image.Url, image.AltText, image.SortOrder, image.IsPrimary);
}

public sealed record ProductResponse(
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
    JsonElement? Spec,
    string Status,
    [property: Obsolete("Deprecated: use Images; this is the primary image's URL.")] string? ImageUrl,
    IReadOnlyList<ProductImageResponse> Images,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ProductResponse From(ProductDto product) => new(
        product.Id,
        product.Sku,
        product.ProductType,
        product.Category,
        product.Name,
        product.Brand,
        product.Model,
        product.Unit,
        product.UnitPrice,
        product.Currency,
        product.RatedPowerW,
        product.WidthMm,
        product.HeightMm,
        product.WarrantyMonth,
        SpecJson.ToElement(product.Spec),
        ProductStatusCodes.ToCode(product.Status),
        product.ImageUrl,
        (product.Images ?? []).OrderBy(i => i.SortOrder).Select(ProductImageResponse.From).ToList(),
        product.CreatedAt,
        product.UpdatedAt);
}

public sealed record ProductDeletedResponse(Guid Id, bool Deleted);

internal static class SpecJson
{
    /// <summary>
    /// JSON null and an absent property both mean "no spec"; anything else is
    /// passed on as raw text for the validator to check.
    /// </summary>
    public static string? ToRaw(JsonElement? spec)
        => spec is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } value
            ? value.GetRawText()
            : null;

    public static JsonElement? ToElement(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
