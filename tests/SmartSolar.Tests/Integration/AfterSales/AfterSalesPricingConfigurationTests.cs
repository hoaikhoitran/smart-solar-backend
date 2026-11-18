using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// AFTER-PRICING configuration (2026-11-18) through the real API: the Admin creates priced labor / service and replacement-part products with the
/// existing catalog endpoints (TEST DATA ONLY — no real company price), technicians read the active catalog with search, filter and paging but cannot
/// change it, and an inactive product leaves the catalog technicians price from.
/// </summary>
public sealed class AfterSalesPricingConfigurationTests
{
    private static object Product(string sku, string type, string unit, decimal price) => new
    {
        sku, productType = type, name = $"TEST {sku}", brand = "SmartSolar test", unit, unitPrice = price, currency = "VND", status = "ACTIVE",
    };

    [Fact]
    public async Task Admin_configures_labor_and_parts_technicians_find_them_and_cannot_change_them()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);

        var labor = await DataOf(await admin.PostAsJsonAsync("/api/admin/products", Product("TEST-SVC-REPAIR-HOUR", "OTHER_SERVICE", "hour", 200_000m)), HttpStatusCode.Created);
        var part = await DataOf(await admin.PostAsJsonAsync("/api/admin/products", Product("TEST-MC4-PAIR", "CONNECTOR", "pair", 40_000m)), HttpStatusCode.Created);
        // An unpriced service may be created (price 0): it is refused at quotation time with SERVICE_QUOTATION_PRICE_MISSING (unit-tested).
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/products", Product("TEST-SVC-UNPRICED", "OTHER_SERVICE", "JOB", 0m))).StatusCode);
        // Invalid configuration is refused: negative price, missing unit, more than 2 decimals.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/products", Product("TEST-BAD-1", "OTHER_SERVICE", "JOB", -1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/products", Product("TEST-BAD-2", "OTHER_SERVICE", "", 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/products", Product("TEST-BAD-3", "OTHER_SERVICE", "JOB", 1.005m))).StatusCode);

        // Technicians search the active catalog (server-side filter + paging).
        var services = await DataOf(await technician.GetAsync("/api/products?productType=OTHER_SERVICE&page=1&pageSize=10"));
        Assert.Contains(services.GetProperty("items").EnumerateArray(), p => p.GetProperty("sku").GetString() == "TEST-SVC-REPAIR-HOUR");
        Assert.DoesNotContain(services.GetProperty("items").EnumerateArray(), p => p.GetProperty("sku").GetString() == "TEST-MC4-PAIR");
        var search = await DataOf(await technician.GetAsync("/api/products?search=MC4&page=1&pageSize=10"));
        Assert.Equal("TEST-MC4-PAIR", Assert.Single(search.GetProperty("items").EnumerateArray()).GetProperty("sku").GetString());

        // A technician cannot create or re-price catalog products.
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync("/api/admin/products", Product("TEST-X", "OTHER_SERVICE", "JOB", 1m))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PatchAsJsonAsync($"/api/admin/products/{labor.GetProperty("id").GetGuid()}/status", new { status = "INACTIVE" })).StatusCode);

        // An inactive product leaves the catalog technicians price from.
        var partId = part.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/admin/products/{partId}/status", new { status = "INACTIVE" })).StatusCode);
        var after = await DataOf(await technician.GetAsync("/api/products?search=MC4&page=1&pageSize=10"));
        Assert.Empty(after.GetProperty("items").EnumerateArray());
    }
}
