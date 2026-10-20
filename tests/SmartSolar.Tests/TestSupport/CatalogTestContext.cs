using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Catalog.GetProduct;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.ProductImages;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Real AppDbContext on an in-memory SQLite database, wired to the real
/// catalog unit of work and a recording fake cache.
/// </summary>
public sealed class CatalogTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public CatalogTestContext(params IInterceptor[] interceptors)
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .AddInterceptors(interceptors)
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        UnitOfWork = new CatalogUnitOfWork(Db);
        ImageUnitOfWork = new ProductImageUnitOfWork(Db);
    }

    public ProductImageUnitOfWork ImageUnitOfWork { get; }

    public CatalogOptions Options { get; } = new();

    public ProductImageHandlers CreateImageHandlers(IProductImageUnitOfWork? unitOfWork = null)
        => new(unitOfWork ?? ImageUnitOfWork, CreateInvalidator(), Options);

    public AppDbContext Db { get; }

    public CatalogUnitOfWork UnitOfWork { get; }

    public FakeCacheStore Cache { get; } = new();

    public ListProductsHandler CreateListHandler() => new(UnitOfWork);

    public GetProductHandler CreateGetHandler() => new(UnitOfWork, Cache, NullLogger<GetProductHandler>.Instance);

    public CreateProductHandler CreateCreateHandler(ICatalogUnitOfWork? unitOfWork = null)
        => new(unitOfWork ?? UnitOfWork);

    public UpdateProductHandler CreateUpdateHandler() => new(UnitOfWork, CreateInvalidator());

    public ChangeProductStatusHandler CreateChangeStatusHandler() => new(UnitOfWork, CreateInvalidator());

    public DeleteProductHandler CreateDeleteHandler() => new(UnitOfWork, CreateInvalidator());

    private ProductCacheInvalidator CreateInvalidator()
        => new(Cache, NullLogger<ProductCacheInvalidator>.Instance);

    public static Product NewPanel(
        string sku,
        string name = "Mono 550",
        string brand = "Jinko",
        string? model = null,
        string? category = "PANEL",
        decimal ratedPowerW = 550m,
        decimal unitPrice = 100m,
        ProductStatus status = ProductStatus.Active,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? deletedAt = null)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;

        return new Product
        {
            Id = Guid.NewGuid(),
            Sku = sku,
            ProductType = ProductTypes.SolarPanel,
            Category = category,
            Name = name,
            Brand = brand,
            Model = model,
            Unit = "PCS",
            UnitPrice = unitPrice,
            Currency = "VND",
            RatedPowerW = ratedPowerW,
            WidthMm = 1134m,
            HeightMm = 2278m,
            WarrantyMonth = 144,
            Status = status,
            CreatedAt = created,
            UpdatedAt = created,
            DeletedAt = deletedAt
        };
    }

    public async Task<Product> SeedAsync(Product product)
    {
        Db.Products.Add(product);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return product;
    }

    public async Task<Product?> ReloadAsync(Guid productId)
        => await Db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == productId);

    /// <summary>Stored images of a product, ordered by sort order, read untracked.</summary>
    public async Task<List<ProductImage>> ImagesOfAsync(Guid productId)
        => await Db.ProductImages.AsNoTracking()
            .Where(i => i.ProductId == productId)
            .OrderBy(i => i.SortOrder)
            .ToListAsync();

    public async Task<ProductImage> SeedImageAsync(
        Guid productId, string url, int sortOrder, bool isPrimary, string? altText = null)
    {
        var image = new ProductImage
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            Url = url,
            AltText = altText,
            SortOrder = sortOrder,
            IsPrimary = isPrimary,
            CreatedAt = DateTimeOffset.UtcNow
        };
        Db.ProductImages.Add(image);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return image;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
