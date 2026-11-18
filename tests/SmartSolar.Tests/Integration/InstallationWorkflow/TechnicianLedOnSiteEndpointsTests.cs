using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// Technician-led on-site flow through the real API (2026-11-18): the role matrix of every new route (five roles + anonymous), request validation,
/// and the retirement (410 ENDPOINT_RETIRED) of every customer approval / selection route — only the customer gets 410; the other roles stay 403.
/// </summary>
public sealed class TechnicianLedOnSiteEndpointsTests
{
    private static readonly string[] AllRoles = [RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer];

    private static readonly Dictionary<string, string?> GenerousLimits = new()
    {
        ["RateLimiting:QuotationWrite:PermitLimit"] = "1000", ["RateLimiting:QuotationRead:PermitLimit"] = "1000",
        ["RateLimiting:PaymentCreate:PermitLimit"] = "1000", ["RateLimiting:PaymentStatus:PermitLimit"] = "1000",
        ["RateLimiting:InstallationWrite:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["RateLimiting:InstallationScheduling:PermitLimit"] = "1000", ["RateLimiting:RetiredEndpoint:PermitLimit"] = "1000",
        ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false",
    };

    private sealed record Call(string Method, string Path, object? Body = null);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, Call call) => call.Method switch
    {
        "GET" => client.GetAsync(call.Path),
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
    public async Task New_on_site_routes_admit_only_their_roles()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();

        foreach (var call in new[]
                 {
                     new Call("POST", $"/api/technician/quotations/{id}/on-site-decision", new { decision = "AGREED", revision = 1 }),
                     new Call("POST", $"/api/technician/quotations/{id}/payment", new { method = "ONLINE" }),
                     new Call("GET", $"/api/technician/quotations/{id}/payment"),
                     new Call("POST", $"/api/technician/quotations/{id}/payment/cancel"),
                     new Call("POST", $"/api/technicians/me/after-sales/quotations/{id}/on-site-decision", new { decision = "AGREED" }),
                     new Call("POST", $"/api/technicians/me/after-sales/quotations/{id}/payment", new { method = "CASH" }),
                     new Call("GET", $"/api/technicians/me/after-sales/quotations/{id}/payment"),
                     new Call("POST", $"/api/technicians/me/after-sales/quotations/{id}/payment/cancel"),
                     new Call("POST", $"/api/technicians/me/after-sales/requests/{id}/completion-confirmation", new { note = "ok" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Technician);

        await AssertRolesAsync(factory, new Call("POST", $"/api/installation-projects/{id}/acceptance", new { expectedRevision = 0, note = "handover done" }),
            RoleCodes.Manager, RoleCodes.Admin);
        // Cash recorded on site is confirmed by Manager/Admin only (never by the technician who took it).
        await AssertRolesAsync(factory, new Call("POST", $"/api/manager/payments/{id}/cash/confirm", new { amount = 1m, receiptReference = "R", evidenceFileId = id }),
            RoleCodes.Manager, RoleCodes.Admin);
    }

    [Fact]
    public async Task Requests_are_validated_and_unknown_resources_are_404()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);

        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/on-site-decision", new { decision = "AGREED" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");   // revision required
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/on-site-decision", new { decision = "DECLINED", revision = 1 }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");   // note required for a decline
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/on-site-decision", new { decision = "AGREED", revision = 1 }),
            HttpStatusCode.NotFound, "QUOTATION_NOT_FOUND");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/payment", new { method = "BITCOIN" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technician/quotations/{id}/payment", new { method = "CASH" }),
            HttpStatusCode.NotFound, "QUOTATION_NOT_FOUND");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technicians/me/after-sales/quotations/{id}/on-site-decision", new { decision = "LATER" }),
            HttpStatusCode.BadRequest, "AFTER_SALES_VALIDATION_FAILED");
        await AssertFailureAsync(await technician.PostAsJsonAsync($"/api/technicians/me/after-sales/requests/{id}/completion-confirmation", new { note = " " }),
            HttpStatusCode.BadRequest, "AFTER_SALES_REASON_REQUIRED");

        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/installation-projects/{id}/acceptance", new { note = "x" }),
            HttpStatusCode.BadRequest, "VALIDATION_FAILED");   // expectedRevision required
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/installation-projects/{id}/acceptance", new { expectedRevision = 0, note = "handover done" }),
            HttpStatusCode.NotFound, "INSTALLATION_PROJECT_NOT_FOUND");
    }

    [Fact]
    public async Task Every_customer_approval_or_selection_route_is_retired_with_410()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);

        foreach (var call in new[]
                 {
                     new Call("GET", "/api/customers/me/quotations"), new Call("GET", $"/api/customers/me/quotations/{id}"),
                     new Call("POST", $"/api/customers/me/quotations/{id}/accept"), new Call("POST", $"/api/customers/me/quotations/{id}/reject", new { reason = "x" }),
                     new Call("POST", $"/api/customers/me/quotations/{id}/revision-request", new { reason = "x" }),
                     new Call("GET", $"/api/customers/me/contracts/{id}/installation-slot-offers"),
                     new Call("POST", $"/api/customers/me/installation-slot-offers/{id}/select"),
                     new Call("POST", $"/api/customers/me/installation-projects/{id}/acceptance", new { decision = "ACCEPT" }),
                     new Call("POST", $"/api/customers/me/installation-projects/{id}/equipment-substitution/approve"),
                 })
        {
            await AssertFailureAsync(await SendAsync(customer, call), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
            // Still the customer's route: other roles are refused, never told about the retirement.
            Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync(CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician), call)).StatusCode);
        }

        // The technician's offer publishing (input to the customer's selection) is retired too; reading the schedule stays available to the customer.
        await AssertFailureAsync(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician).PostAsJsonAsync($"/api/technicians/me/contracts/{id}/installation-slot-offers",
            new { slots = new[] { new { startsAt = DateTimeOffset.UtcNow.AddDays(3), endsAt = DateTimeOffset.UtcNow.AddDays(3).AddHours(4) } } }), HttpStatusCode.Gone, "ENDPOINT_RETIRED");
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/installation-schedules")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/installation-projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/contracts")).StatusCode);
    }
}
