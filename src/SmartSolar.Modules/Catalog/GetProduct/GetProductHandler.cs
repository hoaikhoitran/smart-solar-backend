using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Caching;

namespace SmartSolar.Modules.Catalog.GetProduct;

/// <summary>
/// Cache-aside read of one active product. PostgreSQL is the source of truth;
/// a cache failure of any kind falls back to it rather than failing the read.
/// </summary>
public sealed class GetProductHandler
{
    private readonly ICatalogUnitOfWork _unitOfWork;
    private readonly ICacheStore _cache;
    private readonly ILogger<GetProductHandler> _logger;

    public GetProductHandler(
        ICatalogUnitOfWork unitOfWork,
        ICacheStore cache,
        ILogger<GetProductHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Returns null when the product is missing, inactive or deleted.</summary>
    public async Task<ProductDto?> HandleAsync(Guid productId, CancellationToken cancellationToken)
    {
        var key = CatalogCacheKeys.Product(productId);

        var cached = await TryGetCachedAsync(key, cancellationToken);

        if (cached is not null)
        {
            return cached;
        }

        var product = await _unitOfWork.FindActiveProductAsync(productId, cancellationToken);

        if (product is not null)
        {
            await TrySetCachedAsync(key, product, cancellationToken);
        }

        return product;
    }

    // ICacheStore swallows Redis errors itself, but not every failure surfaces
    // as a RedisException (timeouts, bad payloads), so the read path guards too.
    private async Task<ProductDto?> TryGetCachedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetAsync<ProductDto>(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache read failed for key {CacheKey}. Falling back to database.", key);
            return null;
        }
    }

    private async Task TrySetCachedAsync(string key, ProductDto product, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.SetAsync(key, product, CatalogCacheKeys.ProductTtl, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Cache write failed for key {CacheKey}.", key);
        }
    }
}
