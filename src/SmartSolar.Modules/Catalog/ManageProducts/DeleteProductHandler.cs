using SmartSolar.Modules.Catalog.Contracts.Persistence;

namespace SmartSolar.Modules.Catalog.ManageProducts;

/// <summary>
/// Soft delete: the row stays in PostgreSQL with deleted_at set, and every
/// read and management lookup then treats it as missing.
/// </summary>
public sealed class DeleteProductHandler
{
    private readonly ICatalogUnitOfWork _unitOfWork;
    private readonly ProductCacheInvalidator _cacheInvalidator;

    public DeleteProductHandler(ICatalogUnitOfWork unitOfWork, ProductCacheInvalidator cacheInvalidator)
    {
        _unitOfWork = unitOfWork;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<ProductCommandResult> HandleAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await _unitOfWork.FindProductForUpdateAsync(productId, cancellationToken);

        if (product is null)
        {
            return ProductCommandResult.NotFound();
        }

        var now = DateTimeOffset.UtcNow;
        product.DeletedAt = now;
        product.UpdatedAt = now;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _cacheInvalidator.InvalidateAsync(product.Id);

        return ProductCommandResult.Succeeded(product: null);
    }
}
