using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Paging;

namespace SmartSolar.Modules.Catalog.ListProducts;

/// <summary>
/// Lists active catalog products. Results are deliberately not cached: the
/// filter and paging combinations are unbounded.
/// </summary>
public sealed class ListProductsHandler
{
    private readonly ICatalogUnitOfWork _unitOfWork;

    public ListProductsHandler(ICatalogUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>Expects a query that already passed <see cref="ListProductsQueryValidator"/>.</summary>
    public Task<PagedResult<ProductDto>> HandleAsync(
        ListProductsQuery query,
        CancellationToken cancellationToken)
    {
        // Without an explicit sort the newest products come first; an explicit
        // sort field defaults to ascending.
        var sortBy = query.SortBy is null
            ? ProductSortField.CreatedAt
            : ProductSortOptions.Fields[query.SortBy];

        var descending = query.SortDirection is null
            ? query.SortBy is null
            : string.Equals(query.SortDirection, ProductSortOptions.Descending, StringComparison.OrdinalIgnoreCase);

        var criteria = new ProductSearchCriteria(
            NullIfBlank(query.Search),
            NullIfBlank(query.ProductType) is { } productType ? ProductTypes.Normalize(productType) : null,
            NullIfBlank(query.Category),
            NullIfBlank(query.Brand),
            query.MinPower,
            query.MaxPower,
            query.MinPrice,
            query.MaxPrice,
            query.Page,
            query.PageSize,
            sortBy,
            descending);

        return _unitOfWork.ListActiveProductsAsync(criteria, cancellationToken);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
