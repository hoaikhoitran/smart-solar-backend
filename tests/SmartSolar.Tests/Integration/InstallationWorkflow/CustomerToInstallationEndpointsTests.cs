using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.InstallationWorkflow;

/// <summary>
/// 2026-11-17 customer-to-installation simplification through the real API pipeline: the negative role matrix of all five roles on direct
/// survey booking, technician installation schedules, customer schedule tracking, Manager supervision and the configuration advisor, plus
/// request validation and the advisor's authorization (every signed-in role; no anonymous access; own sites only).
/// </summary>
public sealed class CustomerToInstallationEndpointsTests
{
    private static readonly string[] AllRoles = [RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer];

    private static readonly Dictionary<string, string?> GenerousLimits = new()
    {
        ["RateLimiting:SchedulingWrite:PermitLimit"] = "1000", ["RateLimiting:SchedulingRead:PermitLimit"] = "1000",
        ["RateLimiting:InstallationScheduling:PermitLimit"] = "1000", ["RateLimiting:InstallationRead:PermitLimit"] = "1000",
        ["RateLimiting:RagChat:PermitLimit"] = "1000",
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
    public async Task Every_new_endpoint_refuses_the_other_roles_and_anonymous_callers()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var id = Guid.NewGuid();
        var plan = new { startsAt = "2030-01-07T08:00:00+07:00", endsAt = "2030-01-07T12:00:00+07:00", customerAgreementNote = "ok" };

        await AssertRolesAsync(factory, new Call("POST", "/api/customers/me/survey-requests/direct",
            new { propertySiteId = id, startsAt = "2030-01-07T08:00:00+07:00" }), RoleCodes.Customer);

        foreach (var call in new[]
                 {
                     new Call("GET", "/api/technicians/me/installation-schedules"), new Call("GET", $"/api/technicians/me/contracts/{id}/installation-schedule"),
                     new Call("POST", $"/api/technicians/me/contracts/{id}/installation-schedule", plan),
                     new Call("POST", $"/api/technicians/me/installation-schedules/{id}/cancel", new { reason = "x" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Technician);

        foreach (var call in new[]
                 {
                     new Call("GET", "/api/customers/me/installation-schedules"), new Call("GET", $"/api/customers/me/contracts/{id}/installation-schedule"),
                     new Call("POST", $"/api/customers/me/installation-schedules/{id}/change-request", new { note = "x" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Customer);

        foreach (var call in new[]
                 {
                     new Call("GET", "/api/manager/installation-schedules"), new Call("GET", $"/api/manager/contracts/{id}/installation-schedule"),
                     new Call("POST", $"/api/manager/contracts/{id}/installation-schedule", plan),
                     new Call("POST", $"/api/manager/contracts/{id}/installation-schedule/confirm"),
                     new Call("POST", $"/api/manager/installation-schedules/{id}/cancel", new { reason = "x" }),
                 })
            await AssertRolesAsync(factory, call, RoleCodes.Manager, RoleCodes.Admin);

        // The advisor (and the chat) are for every signed-in role, never anonymous.
        await AssertRolesAsync(factory, new Call("POST", "/api/chat/advisor", new { roofAreaM2 = 30 }), AllRoles);
    }

    [Fact]
    public async Task Requests_are_validated_and_unknown_or_foreign_records_are_404()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), GenerousLimits);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        var id = Guid.NewGuid();

        // Direct booking validation (400) and an unknown site (404).
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/customers/me/survey-requests/direct", new { startsAt = "2030-01-07T08:00:00+07:00" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/customers/me/survey-requests/direct", new { propertySiteId = id, startsAt = "tomorrow" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/customers/me/survey-requests/direct",
            new { propertySiteId = id, startsAt = "2030-01-07T08:00:00+07:00", contactPhone = "abc" })).StatusCode);
        var unknown = await customer.PostAsJsonAsync("/api/customers/me/survey-requests/direct", new { propertySiteId = id, startsAt = "2030-01-07T08:00:00+07:00" });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("PROPERTY_SITE_NOT_FOUND", (await BodyOf(unknown)).GetProperty("error").GetProperty("code").GetString());

        // Schedules: missing interval (400); unknown contract or schedule (404) for every role.
        Assert.Equal(HttpStatusCode.BadRequest, (await technician.PostAsJsonAsync($"/api/technicians/me/contracts/{id}/installation-schedule", new { customerAgreementNote = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await technician.PostAsJsonAsync($"/api/technicians/me/contracts/{id}/installation-schedule",
            new { startsAt = "2030-01-07T08:00:00+07:00", endsAt = "2030-01-07T12:00:00+07:00", customerAgreementNote = "ok" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await technician.GetAsync($"/api/technicians/me/contracts/{id}/installation-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.GetAsync($"/api/customers/me/contracts/{id}/installation-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync($"/api/customers/me/installation-schedules/{id}/change-request", new { note = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await customer.PostAsJsonAsync($"/api/customers/me/installation-schedules/{id}/change-request", new { note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/manager/contracts/{id}/installation-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/manager/installation-schedules?status=planned")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await customer.GetAsync("/api/customers/me/installation-schedules")).StatusCode);

        // Advisor validation and a deterministic answer (empty synthetic catalog: BLOCKED, nothing invented).
        foreach (var bad in new object[] { new { roofAreaM2 = -1 }, new { budgetVnd = 10 }, new { latitude = 10.5 }, new { tiltDegree = 95 }, new { azimuthDegree = 400 } })
            Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/chat/advisor", bad)).StatusCode);
        var blocked = await BodyOf(await customer.PostAsJsonAsync("/api/chat/advisor", new { roofAreaM2 = 50 }));
        Assert.Equal("BLOCKED", blocked.GetProperty("data").GetProperty("status").GetString());
        var ask = await BodyOf(await customer.PostAsJsonAsync("/api/chat/advisor", new { }));
        Assert.Equal("NEEDS_INPUT", ask.GetProperty("data").GetProperty("status").GetString());
        // Another customer's (or an unknown) site is never read.
        var foreign = await BodyOf(await customer.PostAsJsonAsync("/api/chat/advisor", new { roofAreaM2 = 50, propertySiteId = id }));
        Assert.Equal("PROPERTY_SITE", foreign.GetProperty("data").GetProperty("missingInputs")[0].GetProperty("code").GetString());
        // Chat message with invalid structured advisor input.
        Assert.Equal(HttpStatusCode.BadRequest, (await customer.PostAsJsonAsync("/api/chat/messages",
            new { message = "Mái 50 m2", advisor = new { monthlyConsumptionKwh = 0 } })).StatusCode);
    }
}
