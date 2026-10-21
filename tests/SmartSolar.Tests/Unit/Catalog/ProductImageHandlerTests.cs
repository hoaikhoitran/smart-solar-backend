using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SmartSolar.Modules.Catalog.Constants;
using SmartSolar.Modules.Catalog.Contracts.Persistence;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Catalog.ListProducts;
using SmartSolar.Modules.Catalog.ManageProducts;
using SmartSolar.Modules.Catalog.Models;
using SmartSolar.Modules.Catalog.ProductImages;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Catalog;

public class ProductImageHandlerTests
{
    private const string UrlA = "https://cdn.example.com/a.jpg";
    private const string UrlB = "https://cdn.example.com/b.jpg";
    private const string UrlC = "https://cdn.example.com/c.jpg";

    private static CreateProductCommand NewPanelCommand(
        string sku = "PNL-IMG",
        string? imageUrl = null,
        IReadOnlyList<ProductImageInput>? images = null) => new(
        sku, "SOLAR_PANEL", "PANEL", "Mono 550", "Jinko", null, "PCS", 2_500_000m, "VND",
        550m, 1134m, 2278m, 144, "{\"cells\":144}", imageUrl, null, images);

    private static AddProductImageCommand Add(Guid productId, string url, bool makePrimary = false, string? altText = null)
        => new(productId, url, altText, makePrimary);

    /// <summary>Seeds a product with images at sort orders 0..n-1, the first one primary.</summary>
    private static async Task<Product> SeedWithImagesAsync(CatalogTestContext ctx, string sku, params string[] urls)
    {
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel(sku));
        for (var i = 0; i < urls.Length; i++)
        {
            await ctx.SeedImageAsync(product.Id, urls[i], i, isPrimary: i == 0);
        }
        return product;
    }

    private static async Task<Guid> SeedUserAsync(CatalogTestContext ctx)
    {
        var user = PreSurveyTestContext.NewUser("Catalog admin", email: $"catalog-{Guid.NewGuid():N}@example.com");
        ctx.Db.UserAccounts.Add(user);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();
        return user.Id;
    }

    private static void AssertProductFieldsUnchanged(Product before, Product after)
    {
        Assert.Equal(before.UnitPrice, after.UnitPrice);
        Assert.Equal(before.Spec, after.Spec);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.Null(after.DeletedAt);
    }

    // ---- create with a gallery ----

    [Fact]
    public async Task Create_without_images_has_an_empty_gallery_and_no_primary_url()
    {
        await using var ctx = new CatalogTestContext();

        var result = await ctx.CreateCreateHandler().HandleAsync(NewPanelCommand(), CancellationToken.None);

        Assert.Empty(result.Product!.Images);
        Assert.Null(result.Product.ImageUrl);
        Assert.Empty(await ctx.ImagesOfAsync(result.Product.Id));
    }

    [Fact]
    public async Task Create_with_images_keeps_their_order_and_makes_the_first_primary()
    {
        await using var ctx = new CatalogTestContext();
        var actor = await SeedUserAsync(ctx);

        var result = await ctx.CreateCreateHandler().HandleAsync(
            NewPanelCommand(images: [new(" " + UrlB + " ", " Back "), new(UrlA, null), new(UrlC, "")]),
            actor,
            CancellationToken.None);

        var stored = await ctx.ImagesOfAsync(result.Product!.Id);
        Assert.Equal(new[] { UrlB, UrlA, UrlC }, stored.Select(i => i.Url));
        Assert.Equal(new[] { 0, 1, 2 }, stored.Select(i => i.SortOrder));
        Assert.Equal(new[] { true, false, false }, stored.Select(i => i.IsPrimary));
        Assert.Equal("Back", stored[0].AltText);
        Assert.Null(stored[2].AltText);

        Assert.Equal(new[] { UrlB, UrlA, UrlC }, result.Product.Images.Select(i => i.Url));
        Assert.Equal(UrlB, result.Product.ImageUrl);
        Assert.All(stored, i => Assert.Equal(actor, i.CreatedByUserId));
    }

    [Fact]
    public async Task Create_with_the_legacy_image_url_stores_it_as_the_single_primary_image()
    {
        await using var ctx = new CatalogTestContext();

        var result = await ctx.CreateCreateHandler().HandleAsync(
            NewPanelCommand(imageUrl: UrlA), CancellationToken.None);

        var image = Assert.Single(await ctx.ImagesOfAsync(result.Product!.Id));
        Assert.Equal(UrlA, image.Url);
        Assert.True(image.IsPrimary);
        Assert.Equal(0, image.SortOrder);
        Assert.Equal(UrlA, result.Product.ImageUrl);
    }

    // ---- update / status never touch the gallery ----

    [Fact]
    public async Task Update_changes_fields_but_leaves_the_gallery_untouched()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "UPD-IMG", UrlA, UrlB);
        var before = await ctx.ImagesOfAsync(product.Id);

        var result = await ctx.CreateUpdateHandler().HandleAsync(new UpdateProductCommand(
            product.Id, product.Sku, product.ProductType, product.Category, "Renamed", product.Brand,
            product.Model, product.Unit, 999m, product.Currency, product.RatedPowerW, product.WidthMm,
            product.HeightMm, product.WarrantyMonth, product.Spec), CancellationToken.None);

        Assert.Equal(ProductCommandOutcome.Succeeded, result.Outcome);
        var after = await ctx.ImagesOfAsync(product.Id);
        Assert.Equal(
            before.Select(i => (i.Id, i.Url, i.SortOrder, i.IsPrimary)),
            after.Select(i => (i.Id, i.Url, i.SortOrder, i.IsPrimary)));
        Assert.Equal(new[] { UrlA, UrlB }, result.Product!.Images.Select(i => i.Url));
        Assert.Equal(UrlA, result.Product.ImageUrl);
    }

    // ---- add ----

    [Fact]
    public async Task The_first_added_image_becomes_primary_without_touching_product_fields()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("ADD-1"));
        var actor = await SeedUserAsync(ctx);

        var result = await ctx.CreateImageHandlers().AddAsync(Add(product.Id, UrlA, altText: " Front "), actor, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Succeeded, result.Outcome);
        var image = Assert.Single(await ctx.ImagesOfAsync(product.Id));
        Assert.True(image.IsPrimary);
        Assert.Equal(0, image.SortOrder);
        Assert.Equal("Front", image.AltText);
        Assert.Equal(actor, image.CreatedByUserId);
        Assert.Equal(UrlA, result.Product!.ImageUrl);
        AssertProductFieldsUnchanged(product, (await ctx.ReloadAsync(product.Id))!);
    }

    [Fact]
    public async Task Later_images_are_appended_and_make_primary_moves_the_primary_flag()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "ADD-2", UrlA);
        var handlers = ctx.CreateImageHandlers();

        await handlers.AddAsync(Add(product.Id, UrlB), null, CancellationToken.None);
        var result = await handlers.AddAsync(Add(product.Id, UrlC, makePrimary: true), null, CancellationToken.None);

        var stored = await ctx.ImagesOfAsync(product.Id);
        Assert.Equal(new[] { UrlA, UrlB, UrlC }, stored.Select(i => i.Url));
        Assert.Equal(new[] { 0, 1, 2 }, stored.Select(i => i.SortOrder));
        Assert.Equal(UrlC, Assert.Single(stored, i => i.IsPrimary).Url);
        Assert.Equal(UrlC, result.Product!.ImageUrl);
        Assert.Equal(new[] { UrlA, UrlB, UrlC }, result.Product.Images.Select(i => i.Url));
    }

    [Fact]
    public async Task A_duplicate_url_is_rejected_without_writing_or_invalidating()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "DUP-IMG", UrlA);

        var result = await ctx.CreateImageHandlers().AddAsync(
            Add(product.Id, "  HTTPS://" + UrlA["https://".Length..]), null, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Duplicate, result.Outcome);
        Assert.Single(await ctx.ImagesOfAsync(product.Id));
        Assert.Empty(ctx.Cache.Removes);
    }

    [Fact]
    public async Task Adding_beyond_the_configured_maximum_is_rejected()
    {
        await using var ctx = new CatalogTestContext();
        ctx.Options.MaxImagesPerProduct = 2;
        var product = await SeedWithImagesAsync(ctx, "MAX-IMG", UrlA, UrlB);

        var result = await ctx.CreateImageHandlers().AddAsync(Add(product.Id, UrlC), null, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.LimitReached, result.Outcome);
        Assert.Equal(2, (await ctx.ImagesOfAsync(product.Id)).Count);
    }

    [Fact]
    public async Task Images_of_missing_or_soft_deleted_products_are_not_found()
    {
        await using var ctx = new CatalogTestContext();
        var deleted = await ctx.SeedAsync(CatalogTestContext.NewPanel("GONE-IMG", deletedAt: DateTimeOffset.UtcNow));
        var image = await ctx.SeedImageAsync(deleted.Id, UrlA, 0, isPrimary: true);
        var handlers = ctx.CreateImageHandlers();

        Assert.Equal(ProductImageOutcome.ProductNotFound,
            (await handlers.AddAsync(Add(deleted.Id, UrlB), null, CancellationToken.None)).Outcome);
        Assert.Equal(ProductImageOutcome.ProductNotFound,
            (await handlers.AddAsync(Add(Guid.NewGuid(), UrlB), null, CancellationToken.None)).Outcome);
        Assert.Equal(ProductImageOutcome.ProductNotFound,
            (await handlers.RemoveAsync(deleted.Id, image.Id, CancellationToken.None)).Outcome);
        Assert.Equal(ProductImageOutcome.ProductNotFound,
            (await handlers.ReorderAsync(new(deleted.Id, [image.Id]), CancellationToken.None)).Outcome);
        Assert.Equal(ProductImageOutcome.ProductNotFound,
            (await handlers.SetPrimaryAsync(deleted.Id, image.Id, CancellationToken.None)).Outcome);
        Assert.Null(await handlers.GetProductAsync(deleted.Id, CancellationToken.None));
        Assert.Single(await ctx.ImagesOfAsync(deleted.Id));
        Assert.Empty(ctx.Cache.Removes);
    }

    [Fact]
    public async Task Inactive_products_can_be_read_and_managed_by_staff()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("OFF-IMG", status: ProductStatus.Inactive));
        var handlers = ctx.CreateImageHandlers();

        var result = await handlers.AddAsync(Add(product.Id, UrlA), null, CancellationToken.None);
        var detail = await handlers.GetProductAsync(product.Id, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Succeeded, result.Outcome);
        Assert.Equal(ProductStatus.Inactive, detail!.Status);
        Assert.Equal(UrlA, detail.ImageUrl);
    }

    [Fact]
    public async Task Stored_urls_always_start_with_a_lower_case_https_scheme()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("SCHEME"));

        await ctx.CreateImageHandlers().AddAsync(Add(product.Id, " HTTPS://cdn.example.com/Upper.JPG "), null, CancellationToken.None);

        Assert.Equal("https://cdn.example.com/Upper.JPG", Assert.Single(await ctx.ImagesOfAsync(product.Id)).Url);
    }

    // ---- remove ----

    [Fact]
    public async Task Removing_a_secondary_image_keeps_the_primary()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "RM-1", UrlA, UrlB);
        var images = await ctx.ImagesOfAsync(product.Id);

        var result = await ctx.CreateImageHandlers().RemoveAsync(product.Id, images[1].Id, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Succeeded, result.Outcome);
        var remaining = Assert.Single(await ctx.ImagesOfAsync(product.Id));
        Assert.True(remaining.IsPrimary);
        Assert.Equal(UrlA, remaining.Url);
    }

    [Fact]
    public async Task Removing_the_primary_promotes_the_lowest_sort_order()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("RM-2"));
        // Primary in the middle; the remaining lowest sort order must win, not insertion order.
        await ctx.SeedImageAsync(product.Id, UrlC, 7, isPrimary: false);
        var primary = await ctx.SeedImageAsync(product.Id, UrlA, 3, isPrimary: true);
        await ctx.SeedImageAsync(product.Id, UrlB, 5, isPrimary: false);

        var result = await ctx.CreateImageHandlers().RemoveAsync(product.Id, primary.Id, CancellationToken.None);

        var stored = await ctx.ImagesOfAsync(product.Id);
        Assert.Equal(new[] { UrlB, UrlC }, stored.Select(i => i.Url));
        Assert.Equal(UrlB, Assert.Single(stored, i => i.IsPrimary).Url);
        Assert.Equal(UrlB, result.Product!.ImageUrl);
    }

    [Fact]
    public async Task Removing_the_last_image_leaves_an_empty_gallery()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "RM-3", UrlA);
        var image = Assert.Single(await ctx.ImagesOfAsync(product.Id));

        var result = await ctx.CreateImageHandlers().RemoveAsync(product.Id, image.Id, CancellationToken.None);

        Assert.Empty(result.Product!.Images);
        Assert.Null(result.Product.ImageUrl);
        AssertProductFieldsUnchanged(product, (await ctx.ReloadAsync(product.Id))!);
    }

    [Fact]
    public async Task Removing_an_unknown_or_foreign_image_is_not_found()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "RM-4", UrlA);
        var other = await SeedWithImagesAsync(ctx, "RM-5", UrlB);
        var foreign = Assert.Single(await ctx.ImagesOfAsync(other.Id));
        var handlers = ctx.CreateImageHandlers();

        Assert.Equal(ProductImageOutcome.ImageNotFound,
            (await handlers.RemoveAsync(product.Id, Guid.NewGuid(), CancellationToken.None)).Outcome);
        Assert.Equal(ProductImageOutcome.ImageNotFound,
            (await handlers.RemoveAsync(product.Id, foreign.Id, CancellationToken.None)).Outcome);
        Assert.Single(await ctx.ImagesOfAsync(other.Id));
    }

    // ---- reorder ----

    [Fact]
    public async Task Reorder_renumbers_in_the_given_order_and_keeps_the_primary()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "ORD-1", UrlA, UrlB, UrlC);
        var ids = (await ctx.ImagesOfAsync(product.Id)).Select(i => i.Id).ToArray();

        var result = await ctx.CreateImageHandlers().ReorderAsync(
            new ReorderProductImagesCommand(product.Id, [ids[2], ids[0], ids[1]]), CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Succeeded, result.Outcome);
        var stored = await ctx.ImagesOfAsync(product.Id);
        Assert.Equal(new[] { UrlC, UrlA, UrlB }, stored.Select(i => i.Url));
        Assert.Equal(new[] { 0, 1, 2 }, stored.Select(i => i.SortOrder));
        Assert.Equal(UrlA, Assert.Single(stored, i => i.IsPrimary).Url);
        Assert.Equal(new[] { UrlC, UrlA, UrlB }, result.Product!.Images.Select(i => i.Url));
        Assert.Equal(new[] { CatalogCacheKeys.Product(product.Id) }, ctx.Cache.Removes);
        AssertProductFieldsUnchanged(product, (await ctx.ReloadAsync(product.Id))!);
    }

    [Fact]
    public async Task Reorder_compacts_gapped_sort_orders()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("ORD-2"));
        var a = await ctx.SeedImageAsync(product.Id, UrlA, 0, isPrimary: true);
        var b = await ctx.SeedImageAsync(product.Id, UrlB, 4, isPrimary: false);

        await ctx.CreateImageHandlers().ReorderAsync(new(product.Id, [b.Id, a.Id]), CancellationToken.None);

        var stored = await ctx.ImagesOfAsync(product.Id);
        Assert.Equal(new[] { (b.Id, 0), (a.Id, 1) }, stored.Select(i => (i.Id, i.SortOrder)));
    }

    public static TheoryData<string> WrongSets => new() { "missing", "extra", "repeated", "foreign" };

    [Theory]
    [MemberData(nameof(WrongSets))]
    public async Task Reorder_requires_exactly_the_current_image_set(string variant)
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "ORD-3", UrlA, UrlB);
        var other = await SeedWithImagesAsync(ctx, "ORD-4", UrlC);
        var ids = (await ctx.ImagesOfAsync(product.Id)).Select(i => i.Id).ToArray();
        var foreignId = (await ctx.ImagesOfAsync(other.Id)).Single().Id;

        Guid[] requested = variant switch
        {
            "missing" => [ids[1]],
            "extra" => [ids[1], ids[0], Guid.NewGuid()],
            "repeated" => [ids[1], ids[1]],
            _ => [ids[1], foreignId]
        };

        var result = await ctx.CreateImageHandlers().ReorderAsync(new(product.Id, requested), CancellationToken.None);

        Assert.Equal(ProductImageOutcome.OrderMismatch, result.Outcome);
        Assert.Equal(new[] { UrlA, UrlB }, (await ctx.ImagesOfAsync(product.Id)).Select(i => i.Url));
        Assert.Empty(ctx.Cache.Removes);
    }

    // ---- set primary ----

    [Fact]
    public async Task Set_primary_moves_the_flag_and_is_idempotent()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "PRI-1", UrlA, UrlB);
        var ids = (await ctx.ImagesOfAsync(product.Id)).Select(i => i.Id).ToArray();
        var handlers = ctx.CreateImageHandlers();

        var moved = await handlers.SetPrimaryAsync(product.Id, ids[1], CancellationToken.None);
        var again = await handlers.SetPrimaryAsync(product.Id, ids[1], CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Succeeded, moved.Outcome);
        Assert.Equal(ProductImageOutcome.Succeeded, again.Outcome);
        var stored = await ctx.ImagesOfAsync(product.Id);
        Assert.Equal(ids[1], Assert.Single(stored, i => i.IsPrimary).Id);
        Assert.Equal(new[] { UrlA, UrlB }, stored.Select(i => i.Url)); // order unchanged
        Assert.Equal(UrlB, again.Product!.ImageUrl);
        Assert.Equal(ProductImageOutcome.ImageNotFound,
            (await handlers.SetPrimaryAsync(product.Id, Guid.NewGuid(), CancellationToken.None)).Outcome);
        AssertProductFieldsUnchanged(product, (await ctx.ReloadAsync(product.Id))!);
    }

    // ---- cache ----

    [Fact]
    public async Task Every_image_operation_evicts_the_cached_product_so_reads_see_the_new_gallery()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("CACHE-IMG"));
        var key = CatalogCacheKeys.Product(product.Id);
        var get = ctx.CreateGetHandler();
        var handlers = ctx.CreateImageHandlers();

        Assert.Empty((await get.HandleAsync(product.Id, CancellationToken.None))!.Images);
        Assert.True(ctx.Cache.Contains(key));

        var added = await handlers.AddAsync(Add(product.Id, UrlA), null, CancellationToken.None);
        Assert.False(ctx.Cache.Contains(key));
        Assert.Equal(UrlA, (await get.HandleAsync(product.Id, CancellationToken.None))!.ImageUrl);

        var second = await handlers.AddAsync(Add(product.Id, UrlB), null, CancellationToken.None);
        var bId = second.Product!.Images.Single(i => i.Url == UrlB).Id;
        var aId = added.Product!.Images.Single().Id;
        await get.HandleAsync(product.Id, CancellationToken.None);

        await handlers.SetPrimaryAsync(product.Id, bId, CancellationToken.None);
        Assert.Equal(UrlB, (await get.HandleAsync(product.Id, CancellationToken.None))!.ImageUrl);

        await handlers.ReorderAsync(new(product.Id, [bId, aId]), CancellationToken.None);
        Assert.Equal(new[] { UrlB, UrlA }, (await get.HandleAsync(product.Id, CancellationToken.None))!.Images.Select(i => i.Url));

        await handlers.RemoveAsync(product.Id, bId, CancellationToken.None);
        Assert.Equal(UrlA, (await get.HandleAsync(product.Id, CancellationToken.None))!.ImageUrl);

        Assert.Equal(5, ctx.Cache.Removes.Count(k => k == key));
        Assert.StartsWith("catalog:product:v2:", key);
    }

    [Fact]
    public async Task A_failed_cache_eviction_does_not_fail_a_committed_image_operation()
    {
        await using var ctx = new CatalogTestContext();
        var product = await ctx.SeedAsync(CatalogTestContext.NewPanel("CACHE-DOWN"));
        ctx.Cache.Fail = true;

        var result = await ctx.CreateImageHandlers().AddAsync(Add(product.Id, UrlA), null, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Succeeded, result.Outcome);
        Assert.Single(await ctx.ImagesOfAsync(product.Id));
    }

    // ---- reads ----

    [Fact]
    public async Task List_returns_ordered_galleries_with_a_fixed_number_of_queries()
    {
        var counter = new CommandCounter();
        await using var ctx = new CatalogTestContext(counter);
        for (var p = 0; p < 4; p++)
        {
            await SeedWithImagesAsync(ctx, $"LIST-{p}", $"https://cdn.example.com/{p}/a.jpg", $"https://cdn.example.com/{p}/b.jpg");
        }
        await ctx.SeedAsync(CatalogTestContext.NewPanel("LIST-EMPTY"));

        counter.Count = 0;
        var page = await ctx.CreateListHandler().HandleAsync(
            new ListProductsQuery(null, null, null, null, null, null, null, null, 1, 20, "name", "asc"),
            CancellationToken.None);

        // One COUNT and one page query, however many products and images: no per-product image query.
        Assert.Equal(2, counter.Count);
        Assert.Equal(5, page.Items.Count);
        Assert.Empty(page.Items.Single(i => i.Sku == "LIST-EMPTY").Images);
        Assert.All(page.Items.Where(i => i.Sku != "LIST-EMPTY"), item =>
        {
            Assert.Equal(new[] { 0, 1 }, item.Images.Select(i => i.SortOrder));
            Assert.EndsWith("/a.jpg", item.ImageUrl);
        });
    }

    [Fact]
    public void The_read_model_derives_the_deprecated_image_url_from_the_primary_image()
    {
        var product = CatalogTestContext.NewPanel("MAP");
        product.Images =
        [
            new ProductImage { Id = Guid.NewGuid(), Url = UrlB, SortOrder = 1, IsPrimary = true },
            new ProductImage { Id = Guid.NewGuid(), Url = UrlA, SortOrder = 0, IsPrimary = false, AltText = "alt" }
        ];

        var dto = ProductProjection.Map(product);

        Assert.Equal(new[] { UrlA, UrlB }, dto.Images.Select(i => i.Url));
        Assert.Equal(new ProductImageDto(product.Images[1].Id, UrlA, "alt", 0, false), dto.Images[0]);
        Assert.Equal(UrlB, dto.ImageUrl);
    }

    // ---- concurrency backstop ----

    [Fact]
    public async Task A_concurrent_add_that_bypassed_the_lock_is_answered_with_a_conflict()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "RACE-1", UrlA);

        // The handler sees an empty gallery (as if the other add had not committed), so it
        // writes sort_order 0 + primary again; only the unique indexes can stop it.
        var handlers = ctx.CreateImageHandlers(new StaleGalleryUnitOfWork(ctx.ImageUnitOfWork, hideUrl: UrlA));

        var result = await handlers.AddAsync(Add(product.Id, UrlB), null, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Conflict, result.Outcome);
        Assert.Equal(new[] { UrlA }, (await ctx.ImagesOfAsync(product.Id)).Select(i => i.Url));
        Assert.Empty(ctx.Cache.Removes);
    }

    [Fact]
    public async Task A_concurrent_duplicate_url_is_answered_with_duplicate()
    {
        await using var ctx = new CatalogTestContext();
        var product = await SeedWithImagesAsync(ctx, "RACE-2", UrlA, UrlB);

        // Only the URL row is hidden: sort order and primary stay valid, so the
        // URL index is the one that fires.
        var handlers = ctx.CreateImageHandlers(new StaleGalleryUnitOfWork(ctx.ImageUnitOfWork, hideUrl: UrlA));

        var result = await handlers.AddAsync(Add(product.Id, UrlA), null, CancellationToken.None);

        Assert.Equal(ProductImageOutcome.Duplicate, result.Outcome);
        Assert.Equal(2, (await ctx.ImagesOfAsync(product.Id)).Count);
    }

    /// <summary>Hides one stored image from the handler, like a read that raced a concurrent write.</summary>
    private sealed class StaleGalleryUnitOfWork(IProductImageUnitOfWork inner, string hideUrl) : IProductImageUnitOfWork
    {
        public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken)
            => inner.ExecuteInTransactionAsync(work, cancellationToken);

        public Task SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);

        public Task<ProductDto?> FindProductDetailsAsync(Guid productId, CancellationToken cancellationToken)
            => inner.FindProductDetailsAsync(productId, cancellationToken);

        public Task<bool> LockProductAsync(Guid productId, CancellationToken cancellationToken)
            => inner.LockProductAsync(productId, cancellationToken);

        public async Task<List<ProductImage>> GetImagesAsync(Guid productId, CancellationToken cancellationToken)
            => (await inner.GetImagesAsync(productId, cancellationToken)).Where(i => i.Url != hideUrl).ToList();

        public void AddImage(ProductImage image) => inner.AddImage(image);

        public void RemoveImage(ProductImage image) => inner.RemoveImage(image);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
