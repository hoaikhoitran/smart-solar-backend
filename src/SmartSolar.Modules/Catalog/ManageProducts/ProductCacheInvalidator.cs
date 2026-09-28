using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Common.Caching;

namespace SmartSolar.Modules.Catalog.ManageProducts;

/// <summary>
/// Drops a product's cached read model. Callers invoke it only after the
/// database write has committed.
/// </summary>
public sealed class ProductCacheInvalidator
{
    private readonly ICacheStore _cache;
    private readonly ILogger<ProductCacheInvalidator> _logger;

    public ProductCacheInvalidator(ICacheStore cache, ILogger<ProductCacheInvalidator> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task InvalidateAsync(Guid productId)
    {
        var key = CatalogCacheKeys.Product(productId);

        try
        {
            // The write is already committed, so a client disconnect must not
            // cancel the eviction and leave a stale entry behind.
            await _cache.RemoveAsync(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The entry still expires with its TTL; the write itself succeeded.
            _logger.LogWarning(ex, "Cache invalidation failed for key {CacheKey}.", key);
        }
    }
}
