using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Catalog.ProductImages;

namespace SmartSolar.Modules.Catalog.ManageProducts;

public sealed class CreateProductHandler
{
    private readonly ICatalogUnitOfWork _unitOfWork;

    public CreateProductHandler(ICatalogUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    /// <summary>Expects a command that already passed <see cref="CreateProductCommandValidator"/>.</summary>
    public Task<ProductCommandResult> HandleAsync(
        CreateProductCommand command,
        CancellationToken cancellationToken)
        => HandleAsync(command, createdByUserId: null, cancellationToken);

    /// <summary>Expects a command that already passed <see cref="CreateProductCommandValidator"/>.</summary>
    public async Task<ProductCommandResult> HandleAsync(
        CreateProductCommand command,
        Guid? createdByUserId,
        CancellationToken cancellationToken)
    {
        var sku = ProductDetailsMapper.NormalizeSku(command.Sku);

        if (await _unitOfWork.SkuExistsAsync(sku, excludingProductId: null, cancellationToken))
        {
            return ProductCommandResult.DuplicateSku();
        }

        var now = DateTimeOffset.UtcNow;

        var product = new Product
        {
            Id = Guid.NewGuid(),
            Status = ProductStatusCodes.TryParse(command.Status, out var status) ? status : ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        ProductDetailsMapper.Apply(product, command);
        product.Images = InitialImages(product.Id, command, now, createdByUserId);

        // The gallery is inserted with the product, in the same SaveChanges.
        _unitOfWork.AddProduct(product);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateSkuException)
        {
            // Lost the race against the unique SKU index; same answer as the pre-check.
            return ProductCommandResult.DuplicateSku();
        }

        // A brand-new id cannot have a cache entry, so nothing is invalidated.
        return ProductCommandResult.Succeeded(ProductProjection.Map(product));
    }

    /// <summary>Images in request order, the first primary; the legacy ImageUrl alone becomes one primary image.</summary>
    private static List<ProductImage> InitialImages(
        Guid productId,
        CreateProductCommand command,
        DateTimeOffset now,
        Guid? createdByUserId)
    {
        IEnumerable<ProductImageInput> inputs = command.Images is { Count: > 0 } images
            ? images
            : string.IsNullOrWhiteSpace(command.ImageUrl)
                ? []
                : [new ProductImageInput(command.ImageUrl, null)];

        return inputs
            .Select((input, index) => new ProductImage
            {
                Id = Guid.NewGuid(),
                ProductId = productId,
                Url = ProductImageRules.NormalizeUrl(input.Url),
                AltText = ProductImageRules.NormalizeAltText(input.AltText),
                SortOrder = index,
                IsPrimary = index == 0,
                CreatedAt = now,
                CreatedByUserId = createdByUserId
            })
            .ToList();
    }
}
