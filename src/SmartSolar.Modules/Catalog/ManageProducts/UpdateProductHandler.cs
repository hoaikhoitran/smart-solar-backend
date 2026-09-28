using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Models;

namespace SmartSolar.Modules.Catalog.ManageProducts;

public sealed class UpdateProductHandler
{
    private readonly ICatalogUnitOfWork _unitOfWork;
    private readonly ProductCacheInvalidator _cacheInvalidator;

    public UpdateProductHandler(ICatalogUnitOfWork unitOfWork, ProductCacheInvalidator cacheInvalidator)
    {
        _unitOfWork = unitOfWork;
        _cacheInvalidator = cacheInvalidator;
    }

    /// <summary>Expects a command that already passed <see cref="UpdateProductCommandValidator"/>.</summary>
    public async Task<ProductCommandResult> HandleAsync(
        UpdateProductCommand command,
        CancellationToken cancellationToken)
    {
        var product = await _unitOfWork.FindProductForUpdateAsync(command.ProductId, cancellationToken);

        if (product is null)
        {
            return ProductCommandResult.NotFound();
        }

        var sku = ProductDetailsMapper.NormalizeSku(command.Sku);

        if (sku != product.Sku
            && await _unitOfWork.SkuExistsAsync(sku, product.Id, cancellationToken))
        {
            return ProductCommandResult.DuplicateSku();
        }

        ProductDetailsMapper.Apply(product, command);
        product.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateSkuException)
        {
            return ProductCommandResult.DuplicateSku();
        }

        await _cacheInvalidator.InvalidateAsync(product.Id);

        return ProductCommandResult.Succeeded(ProductProjection.Map(product));
    }
}
