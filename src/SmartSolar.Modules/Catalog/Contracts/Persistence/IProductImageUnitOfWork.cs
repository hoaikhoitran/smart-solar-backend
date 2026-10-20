using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Catalog.Contracts.Persistence;

/// <summary>
/// Persistence boundary for product gallery management. Every write runs inside
/// <see cref="ITransactionalUnitOfWork.ExecuteInTransactionAsync{T}"/> after
/// <see cref="LockProductAsync"/>; unique-index violations surface as
/// <see cref="PersistenceConflictException"/>.
/// </summary>
public interface IProductImageUnitOfWork : ITransactionalUnitOfWork
{
    /// <summary>Unique index on (product_id, url).</summary>
    public const string UrlUniqueIndex = "ux_product_image_product_url";

    /// <summary>Read-only management view of a non-deleted product in any status, with its gallery.</summary>
    Task<ProductDto?> FindProductDetailsAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// Locks the product row for the rest of the transaction (PostgreSQL; SQLite
    /// serialises writers) and returns false when it is missing or soft-deleted.
    /// The product row itself is never modified.
    /// </summary>
    Task<bool> LockProductAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>Tracked images of the product, ordered by sort order.</summary>
    Task<List<ProductImage>> GetImagesAsync(Guid productId, CancellationToken cancellationToken);

    void AddImage(ProductImage image);

    void RemoveImage(ProductImage image);
}
