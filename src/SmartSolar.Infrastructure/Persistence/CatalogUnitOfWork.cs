using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Paging;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class CatalogUnitOfWork : ICatalogUnitOfWork
{
    private const string PostgresUniqueViolation = "23505";
    private const int SqliteConstraintViolation = 19;

    private readonly AppDbContext _db;

    public CatalogUnitOfWork(AppDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<ProductDto>> ListActiveProductsAsync(
        ProductSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var query = Filter(ActiveProducts(), criteria);

        var totalItems = await query.CountAsync(cancellationToken);

        // Computed in 64 bits so an absurd page number cannot overflow into a
        // negative offset; such a page is simply past the end.
        var skip = (long)(criteria.Page - 1) * criteria.PageSize;

        if (skip >= totalItems)
        {
            return PagedResult<ProductDto>.Create([], criteria.Page, criteria.PageSize, totalItems);
        }

        var items = await Sort(query, criteria.SortBy, criteria.Descending)
            .Skip((int)skip)
            .Take(criteria.PageSize)
            .Select(ProductProjection.ToDto)
            .ToListAsync(cancellationToken);

        return PagedResult<ProductDto>.Create(items, criteria.Page, criteria.PageSize, totalItems);
    }

    public Task<ProductDto?> FindActiveProductAsync(Guid productId, CancellationToken cancellationToken)
        => ActiveProducts()
            .Where(p => p.Id == productId)
            .Select(ProductProjection.ToDto)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<bool> SkuExistsAsync(string sku, Guid? excludingProductId, CancellationToken cancellationToken)
        => _db.Products
            .AsNoTracking()
            .AnyAsync(
                p => p.Sku == sku && (excludingProductId == null || p.Id != excludingProductId),
                cancellationToken);

    public Task<Product?> FindProductForUpdateAsync(Guid productId, CancellationToken cancellationToken)
        => _db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAt == null, cancellationToken);

    public void AddProduct(Product product) => _db.Products.Add(product);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // sku is the only unique column besides the generated primary key.
            throw new DuplicateSkuException(ex);
        }
    }

    private IQueryable<Product> ActiveProducts()
        => _db.Products
            .AsNoTracking()
            .Where(p => p.Status == ProductStatus.Active && p.DeletedAt == null);

    private static IQueryable<Product> Filter(IQueryable<Product> query, ProductSearchCriteria criteria)
    {
        if (criteria.Search is { } search)
        {
            // Contains is sent as a parameter, never spliced into SQL, and is not
            // a LIKE pattern, so '%' and '_' in the search text match literally.
            var term = search.ToLowerInvariant();
            query = query.Where(p =>
                p.Sku.ToLower().Contains(term)
                || p.Name.ToLower().Contains(term)
                || p.Brand.ToLower().Contains(term)
                || (p.Model != null && p.Model.ToLower().Contains(term)));
        }

        if (criteria.ProductType is { } productType)
        {
            query = query.Where(p => p.ProductType == productType);
        }

        if (criteria.Category is { } category)
        {
            var normalized = category.ToLowerInvariant();
            query = query.Where(p => p.Category != null && p.Category.ToLower() == normalized);
        }

        if (criteria.Brand is { } brand)
        {
            var normalized = brand.ToLowerInvariant();
            query = query.Where(p => p.Brand.ToLower() == normalized);
        }

        if (criteria.MinPower is { } minPower)
        {
            query = query.Where(p => p.RatedPowerW >= minPower);
        }

        if (criteria.MaxPower is { } maxPower)
        {
            query = query.Where(p => p.RatedPowerW <= maxPower);
        }

        if (criteria.MinPrice is { } minPrice)
        {
            query = query.Where(p => p.UnitPrice >= minPrice);
        }

        if (criteria.MaxPrice is { } maxPrice)
        {
            query = query.Where(p => p.UnitPrice <= maxPrice);
        }

        return query;
    }

    /// <summary>
    /// Maps the whitelisted sort field to a compile-time expression. Id is the
    /// tie-breaker so paging stays stable when sort values repeat.
    /// </summary>
    private static IQueryable<Product> Sort(IQueryable<Product> query, ProductSortField sortBy, bool descending)
    {
        var ordered = (sortBy, descending) switch
        {
            (ProductSortField.Name, false) => query.OrderBy(p => p.Name),
            (ProductSortField.Name, true) => query.OrderByDescending(p => p.Name),
            (ProductSortField.Brand, false) => query.OrderBy(p => p.Brand),
            (ProductSortField.Brand, true) => query.OrderByDescending(p => p.Brand),
            (ProductSortField.RatedPowerW, false) => query.OrderBy(p => p.RatedPowerW),
            (ProductSortField.RatedPowerW, true) => query.OrderByDescending(p => p.RatedPowerW),
            (ProductSortField.UnitPrice, false) => query.OrderBy(p => p.UnitPrice),
            (ProductSortField.UnitPrice, true) => query.OrderByDescending(p => p.UnitPrice),
            (ProductSortField.CreatedAt, false) => query.OrderBy(p => p.CreatedAt),
            (ProductSortField.CreatedAt, true) => query.OrderByDescending(p => p.CreatedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy), sortBy, "Unsupported sort field.")
        };

        return ordered.ThenBy(p => p.Id);
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException switch
        {
            null => false,
            { } inner when inner.GetType().Name == "PostgresException"
                => GetProperty(inner, "SqlState") as string == PostgresUniqueViolation,
            { } inner when inner.GetType().Name == "SqliteException"
                => GetProperty(inner, "SqliteErrorCode") as int? == SqliteConstraintViolation
                    && inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static object? GetProperty(Exception exception, string propertyName)
        => exception.GetType().GetProperty(propertyName)?.GetValue(exception);
}
