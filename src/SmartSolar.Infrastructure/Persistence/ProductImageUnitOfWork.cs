using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Models;

namespace SmartSolar.Infrastructure.Persistence;

public sealed class ProductImageUnitOfWork(AppDbContext db) : TransactionalUnitOfWork(db), IProductImageUnitOfWork
{
    public Task<ProductDto?> FindProductDetailsAsync(Guid productId, CancellationToken cancellationToken)
        => Db.Products
            .AsNoTracking()
            .Where(p => p.Id == productId && p.DeletedAt == null)
            .Select(ProductProjection.ToDto)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<bool> LockProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        // SQLite serialises writers itself; the test database never needs the lock.
        if (Db.Database.IsNpgsql())
        {
            await Db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM product WHERE id = {productId} AND deleted_at IS NULL FOR UPDATE",
                cancellationToken);
        }

        return await Db.Products
            .AsNoTracking()
            .AnyAsync(p => p.Id == productId && p.DeletedAt == null, cancellationToken);
    }

    public Task<List<ProductImage>> GetImagesAsync(Guid productId, CancellationToken cancellationToken)
        => Db.ProductImages
            .Where(i => i.ProductId == productId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync(cancellationToken);

    public void AddImage(ProductImage image) => Db.ProductImages.Add(image);

    public void RemoveImage(ProductImage image) => Db.ProductImages.Remove(image);
}
