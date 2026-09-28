using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Models;

namespace SmartSolar.Modules.Catalog.ManageProducts;

public sealed class ChangeProductStatusHandler
{
    private readonly ICatalogUnitOfWork _unitOfWork;
    private readonly ProductCacheInvalidator _cacheInvalidator;

    public ChangeProductStatusHandler(ICatalogUnitOfWork unitOfWork, ProductCacheInvalidator cacheInvalidator)
    {
        _unitOfWork = unitOfWork;
        _cacheInvalidator = cacheInvalidator;
    }

    /// <summary>Expects a command that already passed <see cref="ChangeProductStatusCommandValidator"/>.</summary>
    public async Task<ProductCommandResult> HandleAsync(
        ChangeProductStatusCommand command,
        CancellationToken cancellationToken)
    {
        ProductStatusCodes.TryParse(command.Status, out var status);

        var product = await _unitOfWork.FindProductForUpdateAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductCommandResult.NotFound();
        }

        if (product.Status == status)
        {
            // Already in the requested state: nothing to write or evict.
            return ProductCommandResult.Succeeded(ProductProjection.Map(product));
        }

        product.Status = status;
        product.UpdatedAt = DateTimeOffset.UtcNow;

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        await _cacheInvalidator.InvalidateAsync(product.Id);

        return ProductCommandResult.Succeeded(ProductProjection.Map(product));
    }
}
