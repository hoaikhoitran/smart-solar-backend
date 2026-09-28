using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Paging;

namespace SmartSolar.Modules.Catalog.Contracts.Persistence;

/// <summary>
/// Persistence boundary for the Catalog module. Implemented by Infrastructure
/// so module code never depends on EF Core types.
/// </summary>
public interface ICatalogUnitOfWork
{
    /// <summary>
    /// Read-only page of ACTIVE, non-deleted products; filtering, counting,
    /// sorting and paging all run in the database.
    /// </summary>
    Task<PagedResult<ProductDto>> ListActiveProductsAsync(
        ProductSearchCriteria criteria,
        CancellationToken cancellationToken);

    /// <summary>Read-only lookup of an ACTIVE, non-deleted product; must not track.</summary>
    Task<ProductDto?> FindActiveProductAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// Read-only check across every row, soft-deleted ones included, because
    /// the unique SKU index covers them too.
    /// </summary>
    Task<bool> SkuExistsAsync(string sku, Guid? excludingProductId, CancellationToken cancellationToken);

    /// <summary>Tracked lookup of a non-deleted product in any status, for management writes.</summary>
    Task<Product?> FindProductForUpdateAsync(Guid productId, CancellationToken cancellationToken);

    void AddProduct(Product product);

    /// <summary>
    /// Persists tracked changes. Throws <see cref="DuplicateSkuException"/> when
    /// the unique SKU index is violated.
    /// </summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
