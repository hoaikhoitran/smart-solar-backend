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

/// <summary>Status is optional and defaults to ACTIVE.</summary>
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
    string? Status);

/// <summary>
/// The editable fields only. There is no id, status or timestamp here, so a
/// client cannot overwrite them through this endpoint.
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
    JsonElement? Spec,
    string? ImageUrl);

public sealed record ChangeProductStatusRequest(string? Status);

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
    string? ImageUrl,
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
