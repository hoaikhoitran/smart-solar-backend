using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Catalog;

/// <summary>
/// The product gallery through the real pipeline: JWT, roles, validation,
/// envelope, persistence and the cache-aside public read.
/// </summary>
public class ProductImageEndpointsTests
{
    private const string UrlA = "https://cdn.example.com/a.jpg";
    private const string UrlB = "https://cdn.example.com/b.jpg";
    private const string UrlC = "https://cdn.example.com/c.jpg";

    private static object PanelBody(string sku = "PNL-IMG", object? images = null, string? imageUrl = null) => new
    {
        sku,
        productType = "SOLAR_PANEL",
        category = "PANEL",
        name = "Mono 550",
        brand = "Jinko",
        unit = "PCS",
        unitPrice = 2_500_000m,
        currency = "VND",
        ratedPowerW = 550m,
        widthMm = 1134m,
        heightMm = 2278m,
        spec = new { cells = 144 },
        images,
        imageUrl
    };

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static async Task<JsonElement> DataOf(HttpResponseMessage response)
        => (await BodyOf(response)).GetProperty("data");

    private static async Task AssertFailureAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var body = await BodyOf(response);
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static string[] Urls(JsonElement product)
        => product.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("url").GetString()!).ToArray();

    private static Guid ImageId(JsonElement product, string url)
        => product.GetProperty("images").EnumerateArray().Single(i => i.GetProperty("url").GetString() == url).GetProperty("id").GetGuid();

    private static string? PrimaryUrl(JsonElement product)
        => product.GetProperty("images").EnumerateArray().SingleOrDefault(i => i.GetProperty("isPrimary").GetBoolean()) is { ValueKind: JsonValueKind.Object } primary
            ? primary.GetProperty("url").GetString()
            : null;

    private static async Task<Guid> CreateAsync(HttpClient admin, string sku = "PNL-IMG", object? images = null, string? imageUrl = null)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/products", PanelBody(sku, images, imageUrl));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataOf(response)).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Create_with_images_returns_the_ordered_gallery_and_the_deprecated_primary_url()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        var response = await admin.PostAsJsonAsync("/api/admin/products", PanelBody(images: new[]
        {
            new { url = UrlB, altText = (string?)"Back" },
            new { url = UrlA, altText = (string?)null }
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await DataOf(response);
        Assert.Equal(new[] { UrlB, UrlA }, Urls(data));
        var first = data.GetProperty("images")[0];
        Assert.Equal(0, first.GetProperty("sortOrder").GetInt32());
        Assert.True(first.GetProperty("isPrimary").GetBoolean());
        Assert.Equal("Back", first.GetProperty("altText").GetString());
        Assert.False(data.GetProperty("images")[1].GetProperty("isPrimary").GetBoolean());
        Assert.Equal(UrlB, data.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task Create_without_images_returns_an_empty_gallery()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        var data = await DataOf(await admin.PostAsJsonAsync("/api/admin/products", PanelBody()));

        Assert.Equal(0, data.GetProperty("images").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("imageUrl").ValueKind);
    }

    [Fact]
    public async Task The_legacy_image_url_on_create_becomes_the_primary_image()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);

        var id = await CreateAsync(admin, imageUrl: UrlA);

        var detail = await DataOf(await admin.GetAsync($"/api/admin/products/{id}"));
        Assert.Equal(new[] { UrlA }, Urls(detail));
        Assert.Equal(UrlA, PrimaryUrl(detail));
        Assert.Equal(UrlA, detail.GetProperty("imageUrl").GetString());

        await using var db = factory.CreateDbContext();
        var stored = await db.ProductImages.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.CreatedByUserId);
    }

    [Fact]
    public async Task Invalid_or_insecure_image_urls_return_the_validation_envelope()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin);

        var insecureCreate = await admin.PostAsJsonAsync("/api/admin/products", PanelBody("BAD-IMG", images: new[] { new { url = "http://cdn.example.com/a.jpg" } }));
        var legacyInsecure = await admin.PostAsJsonAsync("/api/admin/products", PanelBody("BAD-IMG2", imageUrl: "javascript:alert(1)"));
        var insecureAdd = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = "http://cdn.example.com/a.jpg" });
        var relativeAdd = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = "/a.jpg" });
        var missingIds = await admin.PutAsJsonAsync($"/api/admin/products/{id}/images/order", new { });

        await AssertFailureAsync(insecureCreate, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        Assert.True((await BodyOf(insecureCreate)).GetProperty("error").GetProperty("details").TryGetProperty("Images[0].Url", out _));
        await AssertFailureAsync(legacyInsecure, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        await AssertFailureAsync(insecureAdd, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        await AssertFailureAsync(relativeAdd, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        await AssertFailureAsync(missingIds, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.Products.CountAsync());
        Assert.Equal(0, await db.ProductImages.CountAsync());
    }

    [Fact]
    public async Task A_configured_host_allowlist_rejects_other_hosts()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?>
        {
            ["Catalog:ImageAllowedHosts:0"] = "cdn.smartsolar.vn"
        });
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin);

        var other = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlA });
        var allowed = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = "https://cdn.smartsolar.vn/a.jpg" });

        await AssertFailureAsync(other, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    [Fact]
    public async Task The_full_gallery_lifecycle_is_reflected_in_admin_and_public_reads()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        var id = await CreateAsync(admin);
        Assert.Equal(0, (await DataOf(await customer.GetAsync($"/api/products/{id}"))).GetProperty("images").GetArrayLength()); // warm the cache

        // add: the first image is primary, later ones are appended
        var first = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlA, altText = "Front" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(UrlA, PrimaryUrl(await DataOf(first)));
        await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlB });
        var third = await DataOf(await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlC, makePrimary = true }));
        Assert.Equal(new[] { UrlA, UrlB, UrlC }, Urls(third));
        Assert.Equal(UrlC, PrimaryUrl(third));

        var publicRead = await DataOf(await customer.GetAsync($"/api/products/{id}"));
        Assert.Equal(new[] { UrlA, UrlB, UrlC }, Urls(publicRead));
        Assert.Equal(UrlC, publicRead.GetProperty("imageUrl").GetString());

        // set primary
        var primary = await admin.PutAsync($"/api/admin/products/{id}/images/{ImageId(third, UrlB)}/primary", null);
        Assert.Equal(HttpStatusCode.OK, primary.StatusCode);
        Assert.Equal(UrlB, PrimaryUrl(await DataOf(primary)));
        Assert.Equal(UrlB, (await DataOf(await customer.GetAsync($"/api/products/{id}"))).GetProperty("imageUrl").GetString());

        // reorder: exact set required
        var wrongSet = await admin.PutAsJsonAsync($"/api/admin/products/{id}/images/order",
            new { imageIds = new[] { ImageId(third, UrlC), ImageId(third, UrlA) } });
        await AssertFailureAsync(wrongSet, HttpStatusCode.BadRequest, "PRODUCT_IMAGE_ORDER_MISMATCH");
        var reordered = await admin.PutAsJsonAsync($"/api/admin/products/{id}/images/order",
            new { imageIds = new[] { ImageId(third, UrlC), ImageId(third, UrlA), ImageId(third, UrlB) } });
        Assert.Equal(HttpStatusCode.OK, reordered.StatusCode);
        var reorderedData = await DataOf(reordered);
        Assert.Equal(new[] { UrlC, UrlA, UrlB }, Urls(reorderedData));
        Assert.Equal(new[] { 0, 1, 2 }, reorderedData.GetProperty("images").EnumerateArray().Select(i => i.GetProperty("sortOrder").GetInt32()));
        Assert.Equal(new[] { UrlC, UrlA, UrlB }, Urls(await DataOf(await customer.GetAsync($"/api/products/{id}"))));

        // remove the primary: the lowest remaining sort order (C) is promoted
        var removed = await admin.DeleteAsync($"/api/admin/products/{id}/images/{ImageId(third, UrlB)}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal(UrlC, PrimaryUrl(await DataOf(removed)));
        Assert.Equal(new[] { UrlC, UrlA }, Urls(await DataOf(await customer.GetAsync($"/api/products/{id}"))));

        await AssertFailureAsync(
            await admin.DeleteAsync($"/api/admin/products/{id}/images/{ImageId(third, UrlB)}"),
            HttpStatusCode.NotFound, "PRODUCT_IMAGE_NOT_FOUND");

        // the list returns the gallery too
        var item = (await DataOf(await customer.GetAsync("/api/products"))).GetProperty("items")[0];
        Assert.Equal(new[] { UrlC, UrlA }, Urls(item));
        Assert.Equal(UrlC, item.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task Image_operations_do_not_modify_product_fields()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin);
        await using var before = factory.CreateDbContext();
        var original = await before.Products.AsNoTracking().SingleAsync();

        var added = await DataOf(await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlA }));
        await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlB, makePrimary = true });
        await admin.DeleteAsync($"/api/admin/products/{id}/images/{ImageId(added, UrlA)}");

        await using var after = factory.CreateDbContext();
        var stored = await after.Products.AsNoTracking().SingleAsync();
        Assert.Equal(original.UnitPrice, stored.UnitPrice);
        Assert.Equal(original.Spec, stored.Spec);
        Assert.Equal(original.UpdatedAt, stored.UpdatedAt);
    }

    [Fact]
    public async Task A_duplicate_image_url_returns_conflict()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin, images: new[] { new { url = UrlA } });

        var response = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlA });

        await AssertFailureAsync(response, HttpStatusCode.Conflict, "PRODUCT_IMAGE_DUPLICATE");
        var duplicateInCreate = await admin.PostAsJsonAsync("/api/admin/products",
            PanelBody("DUP-LIST", images: new[] { new { url = UrlA }, new { url = UrlA } }));
        await AssertFailureAsync(duplicateInCreate, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Too_many_images_are_rejected()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["Catalog:MaxImagesPerProduct"] = "2" });
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin, images: new[] { new { url = UrlA }, new { url = UrlB } });

        var add = await admin.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlC });
        var create = await admin.PostAsJsonAsync("/api/admin/products",
            PanelBody("TOO-MANY", images: new[] { new { url = UrlA }, new { url = UrlB }, new { url = UrlC } }));

        await AssertFailureAsync(add, HttpStatusCode.Conflict, "PRODUCT_IMAGE_LIMIT_REACHED");
        await AssertFailureAsync(create, HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        Assert.True((await BodyOf(create)).GetProperty("error").GetProperty("details").TryGetProperty("Images", out _));
    }

    [Fact]
    public async Task Update_does_not_touch_the_gallery_even_when_a_legacy_image_url_is_sent()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin, images: new[] { new { url = UrlA }, new { url = UrlB } });

        var response = await admin.PutAsJsonAsync($"/api/admin/products/{id}", new
        {
            sku = "PNL-IMG",
            productType = "SOLAR_PANEL",
            name = "Renamed",
            brand = "Jinko",
            unit = "PCS",
            unitPrice = 100,
            currency = "VND",
            ratedPowerW = 550,
            widthMm = 1134,
            heightMm = 2278,
            imageUrl = UrlC,
            images = new[] { new { url = UrlC } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataOf(response);
        Assert.Equal("Renamed", data.GetProperty("name").GetString());
        Assert.Equal(new[] { UrlA, UrlB }, Urls(data));
        Assert.Equal(UrlA, data.GetProperty("imageUrl").GetString());
    }

    [Fact]
    public async Task Admin_detail_includes_inactive_products_but_not_deleted_ones()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var inactive = await CreateAsync(admin, "OFF-IMG", images: new[] { new { url = UrlA } });
        await admin.PatchAsJsonAsync($"/api/admin/products/{inactive}/status", new { status = "INACTIVE" });
        var deleted = await CreateAsync(admin, "DEL-IMG", images: new[] { new { url = UrlA } });
        var deletedImage = ImageId(await DataOf(await admin.GetAsync($"/api/admin/products/{deleted}")), UrlA);
        await admin.DeleteAsync($"/api/admin/products/{deleted}");

        var inactiveDetail = await admin.GetAsync($"/api/admin/products/{inactive}");
        Assert.Equal(HttpStatusCode.OK, inactiveDetail.StatusCode);
        Assert.Equal("INACTIVE", (await DataOf(inactiveDetail)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/products/{inactive}")).StatusCode);

        // An inactive product's gallery can still be managed.
        Assert.Equal(HttpStatusCode.Created,
            (await admin.PostAsJsonAsync($"/api/admin/products/{inactive}/images", new { url = UrlB })).StatusCode);

        var notFound = new[]
        {
            await admin.GetAsync($"/api/admin/products/{deleted}"),
            await admin.GetAsync($"/api/admin/products/{Guid.NewGuid()}"),
            await admin.PostAsJsonAsync($"/api/admin/products/{deleted}/images", new { url = UrlB }),
            await admin.DeleteAsync($"/api/admin/products/{deleted}/images/{deletedImage}"),
            await admin.PutAsJsonAsync($"/api/admin/products/{deleted}/images/order", new { imageIds = new[] { deletedImage } }),
            await admin.PutAsync($"/api/admin/products/{deleted}/images/{deletedImage}/primary", null)
        };
        foreach (var response in notFound)
        {
            await AssertFailureAsync(response, HttpStatusCode.NotFound, "CATALOG_PRODUCT_NOT_FOUND");
        }

        await using var db = factory.CreateDbContext();
        Assert.Equal(1, await db.ProductImages.CountAsync(i => i.ProductId == deleted));
    }

    [Fact]
    public async Task Anonymous_callers_and_non_staff_roles_cannot_manage_images()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin, images: new[] { new { url = UrlA } });
        var imageId = ImageId(await DataOf(await admin.GetAsync($"/api/admin/products/{id}")), UrlA);

        async Task<HttpResponseMessage[]> CallAll(HttpClient client) => new[]
        {
            await client.GetAsync($"/api/admin/products/{id}"),
            await client.PostAsJsonAsync($"/api/admin/products/{id}/images", new { url = UrlB }),
            await client.DeleteAsync($"/api/admin/products/{id}/images/{imageId}"),
            await client.PutAsJsonAsync($"/api/admin/products/{id}/images/order", new { imageIds = new[] { imageId } }),
            await client.PutAsync($"/api/admin/products/{id}/images/{imageId}/primary", null)
        };

        foreach (var response in await CallAll(factory.CreateClient()))
        {
            await AssertFailureAsync(response, HttpStatusCode.Unauthorized, AuthErrorCodes.Unauthorized);
        }

        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Technician })
        {
            foreach (var response in await CallAll(CatalogEndpointsTests.ClientFor(factory, role)))
            {
                await AssertFailureAsync(response, HttpStatusCode.Forbidden, AuthErrorCodes.Forbidden);
            }
        }

        await using var db = factory.CreateDbContext();
        var stored = await db.ProductImages.AsNoTracking().SingleAsync();
        Assert.Equal(UrlA, stored.Url);
        Assert.True(stored.IsPrimary);
    }
}
