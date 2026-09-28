using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Catalog;

public class CatalogUnitOfWorkTests
{
    private static ListProductsQuery Query(
        string? search = null,
        string? productType = null,
        string? category = null,
        string? brand = null,
        decimal? minPower = null,
        decimal? maxPower = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        int page = 1,
        int pageSize = 20,
        string? sortBy = null,
        string? sortDirection = null)
        => new(search, productType, category, brand, minPower, maxPower, minPrice, maxPrice,
            page, pageSize, sortBy, sortDirection);

    [Fact]
    public async Task Pages_report_total_items_and_total_pages()
    {
        await using var ctx = new CatalogTestContext();
        for (var i = 0; i < 25; i++)
        {
            await ctx.SeedAsync(CatalogTestContext.NewPanel($"SKU-{i:00}", name: $"Panel {i:00}"));
        }

        var page = await ctx.CreateListHandler().HandleAsync(
            Query(page: 3, pageSize: 10, sortBy: "name"), CancellationToken.None);

        Assert.Equal(25, page.TotalItems);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(3, page.Page);
        Assert.Equal(10, page.PageSize);
        Assert.Equal(new[] { "Panel 20", "Panel 21", "Panel 22", "Panel 23", "Panel 24" },
            page.Items.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_keeps_the_totals()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("SKU-1"));

        var page = await ctx.CreateListHandler().HandleAsync(
            Query(page: int.MaxValue, pageSize: 100), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(1, page.TotalItems);
        Assert.Equal(1, page.TotalPages);
    }

    [Fact]
    public async Task Inactive_and_deleted_products_are_never_listed()
    {
        await using var ctx = new CatalogTestContext();
        var visible = await ctx.SeedAsync(CatalogTestContext.NewPanel("VISIBLE"));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("INACTIVE", status: ProductStatus.Inactive));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("DELETED", deletedAt: DateTimeOffset.UtcNow));

        var page = await ctx.CreateListHandler().HandleAsync(Query(), CancellationToken.None);

        Assert.Equal(visible.Id, Assert.Single(page.Items).Id);
        Assert.Equal(1, page.TotalItems);
    }

    [Fact]
    public async Task Active_lookup_hides_inactive_and_deleted_products()
    {
        await using var ctx = new CatalogTestContext();
        var active = await ctx.SeedAsync(CatalogTestContext.NewPanel("A"));
        var inactive = await ctx.SeedAsync(CatalogTestContext.NewPanel("I", status: ProductStatus.Inactive));
        var deleted = await ctx.SeedAsync(CatalogTestContext.NewPanel("D", deletedAt: DateTimeOffset.UtcNow));

        Assert.NotNull(await ctx.UnitOfWork.FindActiveProductAsync(active.Id, CancellationToken.None));
        Assert.Null(await ctx.UnitOfWork.FindActiveProductAsync(inactive.Id, CancellationToken.None));
        Assert.Null(await ctx.UnitOfWork.FindActiveProductAsync(deleted.Id, CancellationToken.None));
    }

    [Theory]
    [InlineData("jnk-5")]    // sku
    [InlineData("HI-MO")]    // name
    [InlineData("longi")]    // brand
    [InlineData("x-tra")]    // model
    public async Task Search_matches_sku_name_brand_and_model_case_insensitively(string term)
    {
        await using var ctx = new CatalogTestContext();
        var match = await ctx.SeedAsync(CatalogTestContext.NewPanel(
            "JNK-550", name: "Hi-MO 6", brand: "LONGi", model: "X-TRA"));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("OTHER", name: "Other", brand: "Canadian", model: "CS6"));

        var page = await ctx.CreateListHandler().HandleAsync(Query(search: term), CancellationToken.None);

        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Search_treats_like_wildcards_literally()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("PLAIN", name: "Plain panel"));

        var page = await ctx.CreateListHandler().HandleAsync(Query(search: "%"), CancellationToken.None);

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Type_category_and_brand_filters_combine()
    {
        await using var ctx = new CatalogTestContext();
        var match = await ctx.SeedAsync(CatalogTestContext.NewPanel("M", brand: "Jinko", category: "Mono"));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("B", brand: "Longi", category: "Mono"));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("C", brand: "Jinko", category: "Poly"));
        var inverter = CatalogTestContext.NewPanel("INV", brand: "Jinko", category: "Mono");
        inverter.ProductType = "INVERTER";
        await ctx.SeedAsync(inverter);

        var page = await ctx.CreateListHandler().HandleAsync(
            Query(productType: "solar_panel", category: "mono", brand: "JINKO"), CancellationToken.None);

        Assert.Equal(match.Id, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task Power_and_price_ranges_are_inclusive()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("P400", ratedPowerW: 400m, unitPrice: 90m));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("P500", ratedPowerW: 500m, unitPrice: 100m));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("P550", ratedPowerW: 550m, unitPrice: 150m));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("P600", ratedPowerW: 600m, unitPrice: 200m));

        var byPower = await ctx.CreateListHandler().HandleAsync(
            Query(minPower: 500m, maxPower: 550m, sortBy: "ratedPowerW"), CancellationToken.None);
        var byPrice = await ctx.CreateListHandler().HandleAsync(
            Query(minPrice: 100m, maxPrice: 200m, sortBy: "unitPrice"), CancellationToken.None);

        Assert.Equal(new[] { "P500", "P550" }, byPower.Items.Select(p => p.Sku).ToArray());
        Assert.Equal(new[] { "P500", "P550", "P600" }, byPrice.Items.Select(p => p.Sku).ToArray());
    }

    [Theory]
    [InlineData("name", "asc", new[] { "A", "B", "C" })]
    [InlineData("name", "desc", new[] { "C", "B", "A" })]
    [InlineData("brand", null, new[] { "B", "C", "A" })]
    [InlineData("ratedPowerW", "desc", new[] { "C", "A", "B" })]
    [InlineData("unitPrice", "asc", new[] { "B", "A", "C" })]
    [InlineData("createdAt", "asc", new[] { "A", "B", "C" })]
    [InlineData(null, null, new[] { "C", "B", "A" })]
    public async Task Sorting_uses_the_whitelisted_field_and_direction(
        string? sortBy,
        string? sortDirection,
        string[] expectedSkus)
    {
        await using var ctx = new CatalogTestContext();
        var start = DateTimeOffset.UtcNow.AddDays(-3);
        await ctx.SeedAsync(CatalogTestContext.NewPanel("A", name: "Alpha", brand: "Zeta", ratedPowerW: 500m, unitPrice: 200m, createdAt: start));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("B", name: "Beta", brand: "Alpha", ratedPowerW: 400m, unitPrice: 100m, createdAt: start.AddDays(1)));
        await ctx.SeedAsync(CatalogTestContext.NewPanel("C", name: "Gamma", brand: "Mid", ratedPowerW: 600m, unitPrice: 300m, createdAt: start.AddDays(2)));

        var page = await ctx.CreateListHandler().HandleAsync(
            Query(sortBy: sortBy, sortDirection: sortDirection), CancellationToken.None);

        Assert.Equal(expectedSkus, page.Items.Select(p => p.Sku).ToArray());
    }

    [Fact]
    public async Task List_results_are_not_tracked()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("SKU"));

        await ctx.CreateListHandler().HandleAsync(Query(), CancellationToken.None);

        Assert.Empty(ctx.Db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task The_unique_sku_index_rejects_a_duplicate_as_DuplicateSkuException()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("DUP"));

        ctx.UnitOfWork.AddProduct(CatalogTestContext.NewPanel("DUP"));

        await Assert.ThrowsAsync<DuplicateSkuException>(() => ctx.UnitOfWork.SaveChangesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Sku_existence_includes_soft_deleted_rows_and_can_exclude_one_product()
    {
        await using var ctx = new CatalogTestContext();
        var deleted = await ctx.SeedAsync(CatalogTestContext.NewPanel("GONE", deletedAt: DateTimeOffset.UtcNow));

        Assert.True(await ctx.UnitOfWork.SkuExistsAsync("GONE", null, CancellationToken.None));
        Assert.False(await ctx.UnitOfWork.SkuExistsAsync("GONE", deleted.Id, CancellationToken.None));
        Assert.False(await ctx.UnitOfWork.SkuExistsAsync("NEW", null, CancellationToken.None));
    }

    [Fact]
    public async Task Status_is_stored_as_its_text_code()
    {
        await using var ctx = new CatalogTestContext();
        await ctx.SeedAsync(CatalogTestContext.NewPanel("TXT", status: ProductStatus.Inactive));

        await using var command = ctx.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT status FROM product";
        var stored = await command.ExecuteScalarAsync();

        Assert.Equal("INACTIVE", stored);
    }
}
