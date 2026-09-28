namespace SmartSolar.Modules.Catalog.ListProducts;

public sealed record ListProductsQuery(
    string? Search,
    string? ProductType,
    string? Category,
    string? Brand,
    decimal? MinPower,
    decimal? MaxPower,
    decimal? MinPrice,
    decimal? MaxPrice,
    int Page,
    int PageSize,
    string? SortBy,
    string? SortDirection)
{
    public const int DefaultPage = 1;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;
}

public enum ProductSortField
{
    Name = 1,
    Brand = 2,
    RatedPowerW = 3,
    UnitPrice = 4,
    CreatedAt = 5
}

/// <summary>
/// The sort whitelist. Client input is only ever used as a lookup key here,
/// never as a property or column name.
/// </summary>
public static class ProductSortOptions
{
    public const string Ascending = "asc";
    public const string Descending = "desc";

    public static readonly IReadOnlyDictionary<string, ProductSortField> Fields =
        new Dictionary<string, ProductSortField>(StringComparer.OrdinalIgnoreCase)
        {
            ["name"] = ProductSortField.Name,
            ["brand"] = ProductSortField.Brand,
            ["ratedPowerW"] = ProductSortField.RatedPowerW,
            ["unitPrice"] = ProductSortField.UnitPrice,
            ["createdAt"] = ProductSortField.CreatedAt
        };

    public static bool IsDirection(string? value)
        => string.Equals(value, Ascending, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, Descending, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Validated, normalized list criteria handed to persistence.</summary>
public sealed record ProductSearchCriteria(
    string? Search,
    string? ProductType,
    string? Category,
    string? Brand,
    decimal? MinPower,
    decimal? MaxPower,
    decimal? MinPrice,
    decimal? MaxPrice,
    int Page,
    int PageSize,
    ProductSortField SortBy,
    bool Descending);
