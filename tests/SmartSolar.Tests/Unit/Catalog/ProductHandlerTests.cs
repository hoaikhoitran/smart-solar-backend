using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Common.Paging;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Catalog;

public class ProductHandlerTests
{
    private static CreateProductCommand NewPanelCommand(string sku = "PNL-1") => new(
        sku, "solar_panel", "PANEL", " Mono 550 ", "Jinko", null, "PCS", 2_500_000m, "vnd",
        550m, 1134m, 2278m, 144, "{\"cells\":144}", null, null);

    private static UpdateProductCommand UpdateCommandFor(Product product, string? sku = null, string name = "Renamed") => new(
        product.Id, sku ?? product.Sku, product.ProductType, product.Category, name, product.Brand,
        product.Model, product.Unit, 999m, product.Currency, product.RatedPowerW, product.WidthMm,
        product.HeightMm, product.WarrantyMonth, product.Spec, product.ImageUrl);

    // ---- cache-aside read ----

    [Fact]
    public async Task A_cache_hit_is_returned_without_reading_the_database()
    {
        await using var ctx = new CatalogTestContext();

        // Only the cache knows this product, so a database read would return null.
        var cached = ProductProjection.Map(CatalogTestContext.NewPanel("CACHED-ONLY"));
        ctx.Cache.Seed(CatalogCacheKeys.Product(cached.Id), cached);

        var result = await ctx.CreateGetHandler().HandleAsync(cached.Id, CancellationToken.None);

        Assert.Equal(cached, result);
        Assert.Empty(ctx.Cache.Sets);
    }

    [Fact]
    public async Task A_cache_miss_reads_the_database_then_caches_for_ten_minutes()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("MISS"));
        var key = $"catalog:product:{product.Id}";

        var result = await ctx.CreateGetHandler().HandleAsync(product.Id, CancellationToken.None);

        Assert.Equal(product.Id, result!.Id);
        Assert.Equal(new[] { key }, ctx.Cache.Sets);
        Assert.Equal(TimeSpan.FromMinutes(10), ctx.Cache.ExpirationOf(key));
    }

    [Fact]
    public async Task Missing_and_inactive_products_are_not_cached()
    {
        await using var ctx = new CatalogTestContext();
        var inactive = await ctx.SeedAsync(CatalogTestContext.NewPanel("OFF", status: ProductStatus.Inactive));

        Assert.Null(await ctx.CreateGetHandler().HandleAsync(inactive.Id, CancellationToken.None));
        Assert.Null(await ctx.CreateGetHandler().HandleAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(ctx.Cache.Sets);
    }

    [Fact]
    public async Task A_cache_outage_falls_back_to_the_database()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("OUTAGE"));
        ctx.Cache.Fail = true;

        var result = await ctx.CreateGetHandler().HandleAsync(product.Id, CancellationToken.None);

        Assert.Equal(product.Id, result!.Id);
    }

    // ---- create ----

    [Fact]
    public async Task Create_normalizes_input_and_controls_timestamps()
    {
        await using var ctx = new CatalogTestContext();
        var before = DateTimeOffset.UtcNow;

        var result = await ctx.CreateCreateHandler().HandleAsync(NewPanelCommand(" PNL-1 "), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.Succeeded, result.Outcome);
        var stored = await ctx.ReloadAsync(result.Product!.Id);
        Assert.NotNull(stored);
        Assert.Equal("PNL-1", stored!.Sku);
        Assert.Equal(ProductTypes.SolarPanel, stored.ProductType);
        Assert.Equal("Mono 550", stored.Name);
        Assert.Equal("VND", stored.Currency);
        Assert.Equal(ProductStatus.Active, stored.Status);
        Assert.True(stored.CreatedAt >= before);
        Assert.Equal(stored.CreatedAt, stored.UpdatedAt);
        Assert.Null(stored.DeletedAt);
    }

    [Fact]
    public async Task Create_honours_an_explicit_inactive_status()
    {
        await using var ctx = new CatalogTestContext();

        var result = await ctx.CreateCreateHandler().HandleAsync(
            NewPanelCommand() with { Status = "INACTIVE" }, CancellationToken.None);

        Assert.Equal(ProductStatus.Inactive, result.Product!.Status);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_sku_before_writing()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("PNL-1"));

        var result = await ctx.CreateCreateHandler().HandleAsync(NewPanelCommand("PNL-1"), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.DuplicateSku, result.Outcome);
    }

    [Fact]
    public async Task Create_reports_a_duplicate_when_it_loses_the_race_to_the_unique_index()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("RACE"));

        // The pre-check passes (as if the other insert had not committed yet),
        // so only the database index can catch the duplicate.
        var handler = ctx.CreateCreateHandler(new PreCheckMissesUnitOfWork(ctx.UnitOfWork));

        var result = await handler.HandleAsync(NewPanelCommand("RACE"), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.DuplicateSku, result.Outcome);
    }

    // ---- update / status / delete ----

    [Fact]
    public async Task Update_changes_fields_keeps_created_at_and_invalidates_the_cache()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("UPD", createdAt: DateTimeOffset.UtcNow.AddDays(-1)));
        var key = CatalogCacheKeys.Product(product.Id);
        ctx.Cache.Seed(key, ProductProjection.Map(product));

        var result = await ctx.CreateUpdateHandler().HandleAsync(UpdateCommandFor(product), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.Succeeded, result.Outcome);
        var stored = await ctx.ReloadAsync(product.Id);
        Assert.Equal("Renamed", stored!.Name);
        Assert.Equal(999m, stored.UnitPrice);
        Assert.Equal(product.CreatedAt, stored.CreatedAt);
        Assert.True(stored.UpdatedAt > product.UpdatedAt);
        Assert.Equal(new[] { key }, ctx.Cache.Removes);
        Assert.False(ctx.Cache.Contains(key));
    }

    [Fact]
    public async Task Update_rejects_a_sku_owned_by_another_product_without_invalidating()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("TAKEN"));
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("MINE"));

        var result = await ctx.CreateUpdateHandler().HandleAsync(
            UpdateCommandFor(product, sku: "TAKEN"), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.DuplicateSku, result.Outcome);
        Assert.Empty(ctx.Cache.Removes);
    }

    [Fact]
    public async Task Update_of_a_deleted_product_is_not_found()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("GONE", deletedAt: DateTimeOffset.UtcNow));

        var result = await ctx.CreateUpdateHandler().HandleAsync(UpdateCommandFor(product), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Status_change_persists_and_invalidates_the_cache()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("STATUS"));
        var key = CatalogCacheKeys.Product(product.Id);
        ctx.Cache.Seed(key, ProductProjection.Map(product));

        var result = await ctx.CreateChangeStatusHandler().HandleAsync(
            new ChangeProductStatusCommand(product.Id, "INACTIVE"), CancellationToken.None);

        Assert.Equal(ProductStatus.Inactive, result.Product!.Status);
        Assert.Equal(ProductStatus.Inactive, (await ctx.ReloadAsync(product.Id))!.Status);
        Assert.Equal(new[] { key }, ctx.Cache.Removes);
        Assert.Null(await ctx.CreateGetHandler().HandleAsync(product.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Soft_delete_keeps_the_row_hides_it_and_invalidates_the_cache()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("DEL"));
        var key = CatalogCacheKeys.Product(product.Id);
        ctx.Cache.Seed(key, ProductProjection.Map(product));

        var result = await ctx.CreateDeleteHandler().HandleAsync(product.Id, CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.Succeeded, result.Outcome);
        var stored = await ctx.ReloadAsync(product.Id);
        Assert.NotNull(stored);
        Assert.NotNull(stored!.DeletedAt);
        Assert.Equal(new[] { key }, ctx.Cache.Removes);
        Assert.Null(await ctx.CreateGetHandler().HandleAsync(product.Id, CancellationToken.None));
        Assert.Equal(ProductCommandOutcome.NotFound,
            (await ctx.CreateDeleteHandler().HandleAsync(product.Id, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_failed_invalidation_does_not_fail_a_committed_write()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("KEEP"));
        ctx.Cache.Fail = true;

        var result = await ctx.CreateDeleteHandler().HandleAsync(product.Id, CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.Succeeded, result.Outcome);
        Assert.NotNull((await ctx.ReloadAsync(product.Id))!.DeletedAt);
    }

    /// <summary>Simulates the pre-check racing a concurrent insert of the same SKU.</summary>
    private sealed class PreCheckMissesUnitOfWork : ICatalogUnitOfWork
    {
        private readonly ICatalogUnitOfWork _inner;

        public PreCheckMissesUnitOfWork(ICatalogUnitOfWork inner) => _inner = inner;

        public Task<bool> SkuExistsAsync(string sku, Guid? excludingProductId, CancellationToken cancellationToken)
            => Task.FromResult(false);

        public Task<PagedResult<ProductDto>> ListActiveProductsAsync(ProductSearchCriteria criteria, CancellationToken cancellationToken)
            => _inner.ListActiveProductsAsync(criteria, cancellationToken);

        public Task<ProductDto?> FindActiveProductAsync(Guid productId, CancellationToken cancellationToken)
            => _inner.FindActiveProductAsync(productId, cancellationToken);

        public Task<Product?> FindProductForUpdateAsync(Guid productId, CancellationToken cancellationToken)
            => _inner.FindProductForUpdateAsync(productId, cancellationToken);

        public void AddProduct(Product product) => _inner.AddProduct(product);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => _inner.SaveChangesAsync(cancellationToken);
    }
}
