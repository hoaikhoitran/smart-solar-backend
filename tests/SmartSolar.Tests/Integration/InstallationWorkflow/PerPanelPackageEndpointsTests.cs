using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Per-panel package routes through the real API (2026-11-25, TC27): role matrix (five roles + anonymous), request validation, error envelope with trace id,
/// and the separation between the technician's read-only catalog and the Admin-only catalog writes.
/// </summary>
public sealed class PerPanelPackageEndpointsTests
{
    private static readonly string[] AllRoles = [RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer];

    private static readonly Dictionary<string, string?> GenerousLimits = new()
    {
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:QuotationRead:PermitLimit"] = "1000",
        ["RateLimiting:PaymentCreate:PermitLimit"] = "1000", ["RateLimiting:PaymentStatus:PermitLimit"] = "1000",
        ["RateLimiting:InstallationWrite:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false", ["AfterSales:WorkerEnabled"] = "false",
    };

    private sealed record Call(string Method, string Path, object? Body = null);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Call call) => call.Method switch
    {
        "GET" => client.GetAsync(call.Path),
        "DELETE" => client.DeleteAsync(call.Path),
        "PUT" => client.PutAsJsonAsync(call.Path, call.Body ?? new { }),
        _ => client.PostAsJsonAsync(call.Path, call.Body ?? new { }),
    };

    private static async Task AssertRolesAsync(AuthApiFactory factory, Call call, params string[] allowed)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(factory.CreateClient(), call)).StatusCode);
        foreach (var role in AllRoles)
        {
            var status = (await SendAsync(CatalogEndpointsTests.ClientFor(factory, role), call)).StatusCode;
            if (allowed.Contains(role)) Assert.True(status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized), $"{role} {call.Method} {call.Path}: {status}");
            else Assert.True(status == HttpStatusCode.Forbidden, $"{role} {call.Method} {call.Path}: expected 403, got {(int)status}");
        }
    }

    [Fact]
    public async Task Per_panel_routes_admit_only_their_roles()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        // Technician: read-only per-panel catalog and its own DRAFT's package selection.
        await AssertRolesAsync(factory, new Call("GET", "/api/technician/service-packages?kind=DISASTER_PROTECTION"), RoleCodes.Technician);
        await AssertRolesAsync(factory, new Call("PUT", $"/api/technician/quotations/{id}/service-packages/DISASTER_PROTECTION", new { packageId = id, revision = 1 }),
            RoleCodes.Technician);
        await AssertRolesAsync(factory, new Call("DELETE", $"/api/technician/quotations/{id}/service-packages/DISASTER_PROTECTION?revision=1"), RoleCodes.Technician);
        // Customer: eligibility of their own installation; purchase stays customer-only.
        await AssertRolesAsync(factory, new Call("GET", $"/api/customers/me/after-sales/contracts/{id}/panel-coverage"), RoleCodes.Customer);
        await AssertRolesAsync(factory, new Call("POST", "/api/customers/me/after-sales/purchases",
            new { contractId = id, disasterPackageId = id, includeContract = false, quantity = 1, targetAssets = new[] { new { assetId = id, units = 1 } } }), RoleCodes.Customer);
        // Staff: bundled commitments and reconciliation retry.
        await AssertRolesAsync(factory, new Call("GET", "/api/manager/after-sales/bundled-packages"), RoleCodes.Manager, RoleCodes.Admin);
        await AssertRolesAsync(factory, new Call("POST", $"/api/manager/after-sales/bundled-packages/{id}/reconcile"), RoleCodes.Manager, RoleCodes.Admin);
        // The catalog (price, pricing unit) stays Admin-only: a technician can never edit a package price.
        await AssertRolesAsync(factory, new Call("POST", "/api/admin/after-sales/packages", new { kind = "DISASTER_PROTECTION", name = "x", price = 1, durationMonths = 12,
            pricingUnit = "PER_PANEL" }), RoleCodes.Admin);
        // Cash is confirmed by Manager/Admin only (never the technician who collected it).
        await AssertRolesAsync(factory, new Call("POST", $"/api/manager/payments/{id}/cash/confirm", new { amount = 1m, receiptReference = "R", evidenceFileId = id }),
            RoleCodes.Manager, RoleCodes.Admin);
    }

    [Fact]
    public async Task Requests_are_validated_and_errors_use_the_standard_envelope()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);

        var list = await technician.GetAsync("/api/technician/service-packages");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.True(body.RootElement.GetProperty("isSuccess").GetBoolean());
            Assert.Equal(JsonValueKind.Array, body.RootElement.GetProperty("data").ValueKind);
            Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("traceId").GetString()));
        }
        await AssertFailureAsync(await technician.GetAsync("/api/technician/service-packages?kind=SOLAR"), HttpStatusCode.BadRequest, "AFTER_SALES_VALIDATION_FAILED");

        var path = $"/api/technician/quotations/{id}/service-packages/DISASTER_PROTECTION";
        await AssertFailureAsync(await technician.PutAsJsonAsync(path, new { revision = 1 }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");        // packageId
        await AssertFailureAsync(await technician.PutAsJsonAsync(path, new { packageId = id }), HttpStatusCode.BadRequest, "VALIDATION_FAILED");      // revision
        await AssertFailureAsync(await technician.DeleteAsync(path), HttpStatusCode.BadRequest, "VALIDATION_FAILED");                                // ?revision
        await AssertFailureAsync(await technician.PutAsJsonAsync($"/api/technician/quotations/{id}/service-packages/WARRANTY", new { packageId = id, revision = 1 }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PutAsJsonAsync(path, new { packageId = id, revision = 1 }), HttpStatusCode.NotFound, "QUOTATION_NOT_FOUND");
        await AssertFailureAsync(await technician.DeleteAsync($"{path}?revision=1"), HttpStatusCode.NotFound, "QUOTATION_NOT_FOUND");

        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        await AssertFailureAsync(await customer.GetAsync($"/api/customers/me/after-sales/contracts/{id}/panel-coverage"), HttpStatusCode.NotFound,
            "AFTER_SALES_PROJECT_NOT_FOUND");
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/manager/after-sales/bundled-packages/{id}/reconcile", new { }), HttpStatusCode.NotFound,
            "BUNDLED_PACKAGE_NOT_FOUND");
        await AssertFailureAsync(await manager.GetAsync("/api/manager/after-sales/bundled-packages?pageSize=1000"), HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/manager/after-sales/bundled-packages?status=RECONCILIATION_REQUIRED")).StatusCode);
    }
}
