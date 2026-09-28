using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// Real AppDbContext on an in-memory SQLite database, wired to the real
/// catalog unit of work and a recording fake cache.
/// </summary>
public sealed class CatalogTestContext : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public CatalogTestContext()
    {
        _connection = new SqliteConnection("Filename=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, SqliteCatalogModelCustomizer>()
            .Options;

        Db = new AppDbContext(options);
        Db.Database.EnsureCreated();

        UnitOfWork = new CatalogUnitOfWork(Db);
    }

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

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
