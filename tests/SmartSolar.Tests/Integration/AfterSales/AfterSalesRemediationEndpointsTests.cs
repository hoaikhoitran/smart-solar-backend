using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.AfterSales;

/// <summary>
/// AFTER-QUOTE / AFTER-PAYMENT / payment desk through the real API pipeline (2026-11-18): the role matrix of all five roles + anonymous on every new
/// route, request validation before any work, and 404 without disclosure for unknown or foreign resources.
/// </summary>
public sealed class AfterSalesRemediationEndpointsTests
{
    private static readonly string[] AllRoles = [RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer];

    private static async Task AssertRolesAsync(AuthApiFactory factory, HttpMethod method, string path, object? body, params string[] allowed)
    {
        HttpRequestMessage Message() => new(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().SendAsync(Message())).StatusCode);
        foreach (var role in AllRoles)
        {
            var status = (await CatalogEndpointsTests.ClientFor(factory, role).SendAsync(Message())).StatusCode;
            if (allowed.Contains(role)) Assert.True(status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{role} {method} {path}: {(int)status}");
            else Assert.True(status == HttpStatusCode.Forbidden, $"{role} {method} {path}: expected 403, got {(int)status}");
        }
    }

    [Fact]
    public async Task New_routes_admit_only_their_roles()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        var quote = new { lines = new[] { new { itemId = id, components = new[] { new { productId = id, quantity = 1m } } } } };
        var cash = new { amount = 1m, receiptReference = "R", evidenceFileId = id };

        await AssertRolesAsync(factory, HttpMethod.Post, $"/api/technicians/me/after-sales/requests/{id}/quotations", quote, RoleCodes.Technician);
        await AssertRolesAsync(factory, HttpMethod.Post, $"/api/customers/me/after-sales/quotations/{id}/cash", null, RoleCodes.Customer);
        await AssertRolesAsync(factory, HttpMethod.Post, $"/api/customers/me/after-sales/quotations/{id}/cash/cancel", null, RoleCodes.Customer);
        await AssertRolesAsync(factory, HttpMethod.Get, "/api/manager/payments", null, RoleCodes.Manager, RoleCodes.Admin);
        await AssertRolesAsync(factory, HttpMethod.Post, $"/api/manager/payments/{id}/cash/confirm", cash, RoleCodes.Manager, RoleCodes.Admin);
        await AssertRolesAsync(factory, HttpMethod.Get, "/api/admin/after-sales/dashboard/overview", null, RoleCodes.Admin);
        await AssertRolesAsync(factory, HttpMethod.Get, "/api/manager/after-sales/unapplied-money", null, RoleCodes.Manager, RoleCodes.Admin);
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager).GetAsync("/api/manager/after-sales/unapplied-money?status=MAYBE"),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        // The overview answers an empty database with zeros, and refuses a reversed period.
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        var view = await DataOf(await admin.GetAsync("/api/admin/after-sales/dashboard/overview?from=2026-10-01&to=2026-10-31"));
        Assert.Equal(0, view.GetProperty("pendingCashChoices").GetProperty("count").GetInt32());
        await AssertFailureAsync(await admin.GetAsync("/api/admin/after-sales/dashboard/overview?from=2026-10-31&to=2026-10-01"), HttpStatusCode.BadRequest,
            AfterSalesErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Requests_are_validated_and_unknown_resources_are_404()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient());
        var id = Guid.NewGuid();
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);

        // A technician quotation carries no amount: lines with item + products are required.
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technicians/me/after-sales/requests/{id}/quotations", new { lines = Array.Empty<object>() }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technicians/me/after-sales/requests/{id}/quotations",
            new { lines = new[] { new { itemId = id, components = new[] { new { productId = id } } } } }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technicians/me/after-sales/requests/{id}/quotations",
            new { lines = new[] { new { itemId = id, components = new[] { new { productId = id, quantity = 1m } } } } }), HttpStatusCode.NotFound,
            AfterSalesErrorCodes.RequestNotFound);

        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/after-sales/quotations/{id}/cash", null), HttpStatusCode.NotFound, AfterSalesErrorCodes.QuotationNotFound);
        await AssertFailureAsync(await customer.PostAsync($"/api/customers/me/after-sales/quotations/{id}/cash/cancel", null), HttpStatusCode.NotFound, AfterSalesErrorCodes.QuotationNotFound);

        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/{id}/cash/confirm", new { receiptReference = "R" }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/payments/{id}/cash/confirm", new { amount = 1m, receiptReference = "R", evidenceFileId = id }),
            HttpStatusCode.NotFound, PaymentErrorCodes.AttemptNotFound);
        await AssertFailureAsync(await manager.GetAsync("/api/manager/payments?method=CHEQUE"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.GetAsync("/api/manager/payments?status=MAYBE"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await manager.GetAsync("/api/manager/payments?pageSize=101"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var empty = await DataOf(await manager.GetAsync("/api/manager/payments?method=CASH&status=PENDING"));
        Assert.Equal((1, 20, 0), (empty.GetProperty("page").GetInt32(), empty.GetProperty("pageSize").GetInt32(), empty.GetProperty("totalItems").GetInt32()));
    }
}
