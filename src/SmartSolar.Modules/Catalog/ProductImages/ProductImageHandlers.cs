using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.Catalog.ProductImages;

/// <summary>
/// Product gallery management. Each operation runs in one transaction that first
/// locks the product row, so concurrent operations on the same product are
/// serialised; the unique indexes (one primary, one row per sort position, one
/// row per URL) are the backstop and their violations are answered with 409.
/// Image operations never change product fields (price, spec, updated_at).
/// Writes that would pass through a transient duplicate (moving the primary
/// flag, renumbering sort positions) are split into ordered saves.
/// </summary>
public sealed class ProductImageHandlers
{
    private readonly IProductImageUnitOfWork _unitOfWork;
    private readonly ProductCacheInvalidator _cacheInvalidator;
    private readonly CatalogOptions _options;

    public ProductImageHandlers(
        IProductImageUnitOfWork unitOfWork,
        ProductCacheInvalidator cacheInvalidator,
        CatalogOptions options)
    {
        _unitOfWork = unitOfWork;
        _cacheInvalidator = cacheInvalidator;
        _options = options;
    }

    /// <summary>Management read: any status, but soft-deleted products are missing.</summary>
    public Task<ProductDto?> GetProductAsync(Guid productId, CancellationToken cancellationToken)
        => _unitOfWork.FindProductDetailsAsync(productId, cancellationToken);

    /// <summary>Expects a command that already passed <see cref="AddProductImageCommandValidator"/>.</summary>
    public Task<ProductImageResult> AddAsync(
        AddProductImageCommand command,
        Guid? actorUserId,
        CancellationToken cancellationToken)
        => RunAsync(command.ProductId, async (images, token) =>
        {
            if (images.Count >= Math.Max(_options.MaxImagesPerProduct, 0))
            {
                return ProductImageOutcome.LimitReached;
            }

            var url = ProductImageRules.NormalizeUrl(command.Url);

            if (images.Any(i => string.Equals(i.Url, url, StringComparison.Ordinal)))
            {
                return ProductImageOutcome.Duplicate;
            }

            var makePrimary = images.Count == 0 || command.MakePrimary;

            if (makePrimary && images.FirstOrDefault(i => i.IsPrimary) is { } currentPrimary)
            {
                // Clear the old primary first so the one-primary index never sees two.
                currentPrimary.IsPrimary = false;
                await _unitOfWork.SaveChangesAsync(token);
            }

            _unitOfWork.AddImage(new ProductImage
            {
                Id = Guid.NewGuid(),
                ProductId = command.ProductId,
                Url = url,
                AltText = ProductImageRules.NormalizeAltText(command.AltText),
                SortOrder = images.Count == 0 ? 0 : images.Max(i => i.SortOrder) + 1,
                IsPrimary = makePrimary,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedByUserId = actorUserId
            });
            await _unitOfWork.SaveChangesAsync(token);

            return ProductImageOutcome.Succeeded;
        }, cancellationToken);

    /// <summary>Removing the primary image promotes the remaining image with the lowest sort order.</summary>
    public Task<ProductImageResult> RemoveAsync(Guid productId, Guid imageId, CancellationToken cancellationToken)
        => RunAsync(productId, async (images, token) =>
        {
            if (images.FirstOrDefault(i => i.Id == imageId) is not { } image)
            {
                return ProductImageOutcome.ImageNotFound;
            }

            _unitOfWork.RemoveImage(image);
            await _unitOfWork.SaveChangesAsync(token);

            if (image.IsPrimary
                && images.Where(i => i.Id != imageId).OrderBy(i => i.SortOrder).FirstOrDefault() is { } next)
            {
                next.IsPrimary = true;
                await _unitOfWork.SaveChangesAsync(token);
            }

            return ProductImageOutcome.Succeeded;
        }, cancellationToken);

    /// <summary>
    /// Renumbers the gallery 0..n-1 in the given order. The ids must be exactly
    /// the current set (no missing, extra or repeated ids).
    /// </summary>
    public Task<ProductImageResult> ReorderAsync(ReorderProductImagesCommand command, CancellationToken cancellationToken)
        => RunAsync(command.ProductId, async (images, token) =>
        {
            var ids = command.ImageIds;

            if (ids.Count != images.Count
                || ids.Distinct().Count() != ids.Count
                || !images.Select(i => i.Id).ToHashSet().SetEquals(ids))
            {
                return ProductImageOutcome.OrderMismatch;
            }

            var byId = images.ToDictionary(i => i.Id);

            if (ids.Select((id, index) => byId[id].SortOrder == index).All(same => same))
            {
                return ProductImageOutcome.Succeeded;
            }

            // Phase 1 parks every row above the current maximum, phase 2 writes the
            // final positions. n distinct non-negative positions mean max >= n - 1,
            // so the parked range never overlaps the old or the final positions and
            // the per-row unique check on (product_id, sort_order) never fires.
            var parkingBase = images.Max(i => i.SortOrder) + 1;

            for (var index = 0; index < ids.Count; index++)
            {
                byId[ids[index]].SortOrder = parkingBase + index;
            }
            await _unitOfWork.SaveChangesAsync(token);

            for (var index = 0; index < ids.Count; index++)
            {
                byId[ids[index]].SortOrder = index;
            }
            await _unitOfWork.SaveChangesAsync(token);

            return ProductImageOutcome.Succeeded;
        }, cancellationToken);

    public Task<ProductImageResult> SetPrimaryAsync(Guid productId, Guid imageId, CancellationToken cancellationToken)
        => RunAsync(productId, async (images, token) =>
        {
            if (images.FirstOrDefault(i => i.Id == imageId) is not { } image)
            {
                return ProductImageOutcome.ImageNotFound;
            }

            if (image.IsPrimary)
            {
                return ProductImageOutcome.Succeeded;
            }

            if (images.FirstOrDefault(i => i.IsPrimary) is { } currentPrimary)
            {
                currentPrimary.IsPrimary = false;
                await _unitOfWork.SaveChangesAsync(token);
            }

            image.IsPrimary = true;
            await _unitOfWork.SaveChangesAsync(token);

            return ProductImageOutcome.Succeeded;
        }, cancellationToken);

    private async Task<ProductImageResult> RunAsync(
        Guid productId,
        Func<List<ProductImage>, CancellationToken, Task<ProductImageOutcome>> work,
        CancellationToken cancellationToken)
    {
        ProductImageOutcome outcome;

        try
        {
            outcome = await _unitOfWork.ExecuteInTransactionAsync(async token =>
            {
                if (!await _unitOfWork.LockProductAsync(productId, token))
                {
                    return (ProductImageOutcome.ProductNotFound, false);
                }

                var images = await _unitOfWork.GetImagesAsync(productId, token);
                var result = await work(images, token);
                return (result, result == ProductImageOutcome.Succeeded);
            }, cancellationToken);
        }
        catch (PersistenceConflictException ex) when (ex.Kind == PersistenceConflictException.UniqueViolation)
        {
            // Only reachable when the lock is unavailable or another writer bypassed it.
            return ProductImageResult.Failed(IsUrlIndex(ex.Constraint)
                ? ProductImageOutcome.Duplicate
                : ProductImageOutcome.Conflict);
        }

        if (outcome != ProductImageOutcome.Succeeded)
        {
            return ProductImageResult.Failed(outcome);
        }

        await _cacheInvalidator.InvalidateAsync(productId);

        var product = await _unitOfWork.FindProductDetailsAsync(productId, cancellationToken);

        return product is null
            ? ProductImageResult.Failed(ProductImageOutcome.ProductNotFound)
            : new ProductImageResult(ProductImageOutcome.Succeeded, product);
    }

    // PostgreSQL reports the index name; SQLite reports "product_image.product_id, product_image.url".
    private static bool IsUrlIndex(string? constraint)
        => constraint is not null
           && (constraint.Contains(IProductImageUnitOfWork.UrlUniqueIndex, StringComparison.OrdinalIgnoreCase)
               || constraint.EndsWith("product_image.url", StringComparison.OrdinalIgnoreCase));
}
