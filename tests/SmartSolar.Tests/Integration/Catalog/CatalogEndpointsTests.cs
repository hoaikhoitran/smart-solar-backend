using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Contracts.Security;
using SmartSolar.Modules.Identity.Entities;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Integration.Catalog;

/// <summary>
/// Runs the catalog endpoints through the real pipeline: JWT authentication,
/// role authorization, validation, envelope and soft delete.
/// </summary>
public class CatalogEndpointsTests
{
    private static object PanelBody(string sku = "PNL-550", string? status = null) => new
    {
        sku,
        productType = "SOLAR_PANEL",
        category = "PANEL",
        name = "Mono 550",
        brand = "Jinko",
        model = "Tiger Neo",
        unit = "PCS",
        unitPrice = 2_500_000m,
        currency = "VND",
        ratedPowerW = 550m,
        widthMm = 1134m,
        heightMm = 2278m,
        warrantyMonth = 144,
        spec = new { cells = 144, efficiency = 21.3 },
        status
    };

    internal static HttpClient ClientFor(AuthApiFactory factory, string role, Guid? userId = null)
    {
        var tokens = factory.Services.GetRequiredService<IAccessTokenService>();
        var user = new UserAccount { Id = userId ?? Guid.NewGuid(), Email = $"{role.ToLowerInvariant()}@example.com" };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.Generate(user, new[] { role }).Token);
        return client;
    }

    private static async Task<JsonElement> BodyOf(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static void AssertFailure(JsonElement body, string expectedCode)
    {
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("data").ValueKind);
        Assert.Equal(expectedCode, body.GetProperty("error").GetProperty("code").GetString());
    }

    private static async Task<Guid> CreateAsync(HttpClient admin, string sku = "PNL-550")
    {
        var response = await admin.PostAsJsonAsync("/api/admin/products", PanelBody(sku));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await BodyOf(response)).GetProperty("data").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task Anonymous_requests_can_read_products_but_not_manage_them()
    {
        using var factory = new AuthApiFactory();
        var id = await CreateAsync(ClientFor(factory, RoleCodes.Admin));
        var client = factory.CreateClient();

        var list = await client.GetAsync("/api/products");
        var create = await client.PostAsJsonAsync("/api/admin/products", PanelBody("ANON"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var body = await BodyOf(list);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        Assert.Equal(id, body.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        AssertFailure(await BodyOf(create), AuthErrorCodes.Unauthorized);
    }

    [Theory]
    [InlineData(RoleCodes.Admin)]
    [InlineData(RoleCodes.Manager)]
    public async Task Admins_and_managers_can_create_products(string role)
    {
        using var factory = new AuthApiFactory();
        var client = ClientFor(factory, role);

        var response = await client.PostAsJsonAsync("/api/admin/products", PanelBody());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await BodyOf(response);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("error").ValueKind);
        var data = body.GetProperty("data");
        Assert.Equal("PNL-550", data.GetProperty("sku").GetString());
        Assert.Equal("ACTIVE", data.GetProperty("status").GetString());
        Assert.Equal(144, data.GetProperty("spec").GetProperty("cells").GetInt32());
    }

    [Theory]
    [InlineData(RoleCodes.Customer)]
    [InlineData(RoleCodes.Sales)]
    [InlineData(RoleCodes.Technician)]
    public async Task Other_roles_cannot_manage_products(string role)
    {
        using var factory = new AuthApiFactory();
        var id = await CreateAsync(ClientFor(factory, RoleCodes.Admin));
        var client = ClientFor(factory, role);

        var responses = new[]
        {
            await client.PostAsJsonAsync("/api/admin/products", PanelBody("OTHER")),
            await client.PutAsJsonAsync($"/api/admin/products/{id}", PanelBody("OTHER")),
            await client.PatchAsJsonAsync($"/api/admin/products/{id}/status", new { status = "INACTIVE" }),
            await client.DeleteAsync($"/api/admin/products/{id}")
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        AssertFailure(await BodyOf(responses[0]), AuthErrorCodes.Forbidden);

        await using var db = factory.CreateDbContext();
        var stored = await db.Products.SingleAsync();
        Assert.Equal(ProductStatus.Active, stored.Status);
        Assert.Null(stored.DeletedAt);
    }

    [Fact]
    public async Task Any_authenticated_user_can_read_active_products()
    {
        using var factory = new AuthApiFactory();
        var id = await CreateAsync(ClientFor(factory, RoleCodes.Manager));
        var customer = ClientFor(factory, RoleCodes.Customer);

        var list = await customer.GetAsync("/api/products?search=mono&productType=SOLAR_PANEL&minPower=500&sortBy=unitPrice&sortDirection=desc");
        var single = await customer.GetAsync($"/api/products/{id}");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = (await BodyOf(list)).GetProperty("data");
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, page.GetProperty("totalItems").GetInt32());
        Assert.Equal(1, page.GetProperty("totalPages").GetInt32());
        Assert.Equal(id, page.GetProperty("items")[0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.OK, single.StatusCode);
        var body = await BodyOf(single);
        Assert.True(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(1134m, body.GetProperty("data").GetProperty("widthMm").GetDecimal());
    }

    [Fact]
    public async Task An_unknown_product_returns_not_found_in_the_envelope()
    {
        using var factory = new AuthApiFactory();

        var response = await ClientFor(factory, RoleCodes.Customer).GetAsync($"/api/products/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertFailure(await BodyOf(response), "CATALOG_PRODUCT_NOT_FOUND");
    }

    [Fact]
    public async Task Invalid_input_returns_the_validation_envelope_with_field_details()
    {
        using var factory = new AuthApiFactory();
        var admin = ClientFor(factory, RoleCodes.Admin);

        var create = await admin.PostAsJsonAsync("/api/admin/products", new
        {
            sku = "BAD",
            productType = "SOLAR_PANEL",
            name = "Panel",
            brand = "Brand",
            unit = "PCS",
            unitPrice = -1,
            currency = "VN"
        });
        var list = await admin.GetAsync("/api/products?pageSize=101&sortBy=sku");
        var malformed = await admin.GetAsync("/api/products?page=abc");

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var createBody = await BodyOf(create);
        AssertFailure(createBody, AuthErrorCodes.ValidationFailed);
        var details = createBody.GetProperty("error").GetProperty("details");
        Assert.True(details.TryGetProperty("UnitPrice", out _));
        Assert.True(details.TryGetProperty("Currency", out _));
        Assert.True(details.TryGetProperty("RatedPowerW", out _));

        Assert.Equal(HttpStatusCode.BadRequest, list.StatusCode);
        var listDetails = (await BodyOf(list)).GetProperty("error").GetProperty("details");
        Assert.True(listDetails.TryGetProperty("PageSize", out _));
        Assert.True(listDetails.TryGetProperty("SortBy", out _));

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        AssertFailure(await BodyOf(malformed), AuthErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task A_duplicate_sku_returns_conflict()
    {
        using var factory = new AuthApiFactory();
        var admin = ClientFor(factory, RoleCodes.Admin);
        await CreateAsync(admin, "DUP-1");

        var response = await admin.PostAsJsonAsync("/api/admin/products", PanelBody("DUP-1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertFailure(await BodyOf(response), "CATALOG_SKU_ALREADY_EXISTS");
    }

    [Fact]
    public async Task Update_cannot_overwrite_server_owned_fields()
    {
        using var factory = new AuthApiFactory();
        var admin = ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin);

        await using var before = factory.CreateDbContext();
        var original = await before.Products.AsNoTracking().SingleAsync();

        var response = await admin.PutAsJsonAsync($"/api/admin/products/{id}", new
        {
            id = Guid.NewGuid(),
            createdAt = "2000-01-01T00:00:00Z",
            deletedAt = "2000-01-01T00:00:00Z",
            status = "INACTIVE",
            sku = "PNL-550",
            productType = "SOLAR_PANEL",
            name = "Renamed panel",
            brand = "Jinko",
            unit = "PCS",
            unitPrice = 100,
            currency = "VND",
            ratedPowerW = 560,
            widthMm = 1134,
            heightMm = 2278
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var after = factory.CreateDbContext();
        var stored = await after.Products.AsNoTracking().SingleAsync();
        Assert.Equal(id, stored.Id);
        Assert.Equal("Renamed panel", stored.Name);
        Assert.Equal(original.CreatedAt, stored.CreatedAt);
        Assert.Null(stored.DeletedAt);
        Assert.Equal(ProductStatus.Active, stored.Status);
    }

    [Fact]
    public async Task Deactivated_products_disappear_from_reads()
    {
        using var factory = new AuthApiFactory();
        var admin = ClientFor(factory, RoleCodes.Admin);
        var id = await CreateAsync(admin);

        var patch = await admin.PatchAsJsonAsync($"/api/admin/products/{id}/status", new { status = "INACTIVE" });

        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        Assert.Equal("INACTIVE", (await BodyOf(patch)).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/products/{id}")).StatusCode);
        Assert.Equal(0, (await BodyOf(await admin.GetAsync("/api/products"))).GetProperty("data").GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Delete_is_soft_and_hides_the_product()
    {
        using var factory = new AuthApiFactory();
        var admin = ClientFor(factory, RoleCodes.Manager);
        var id = await CreateAsync(admin);
        await admin.GetAsync($"/api/products/{id}"); // warm the cache

        var delete = await admin.DeleteAsync($"/api/admin/products/{id}");

        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        Assert.True((await BodyOf(delete)).GetProperty("data").GetProperty("deleted").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/products/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/products/{id}")).StatusCode);

        await using var db = factory.CreateDbContext();
        var stored = await db.Products.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.DeletedAt);
    }
}
