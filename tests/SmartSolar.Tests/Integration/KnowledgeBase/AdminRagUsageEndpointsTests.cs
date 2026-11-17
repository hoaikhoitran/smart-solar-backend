using System.Net;
using System.Net.Http.Json;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>
/// The Admin AI cost dashboard and budget API through the real pipeline: Admin only (the other four roles get 403, anonymous 401), bounded
/// date ranges, the budget change as an audited configuration version within the hard ceilings, and no secret in any response.
/// </summary>
public sealed class AdminRagUsageEndpointsTests
{
    private const string Secret = "do-not-leak-this-shared-secret-0123456789";
    private static readonly string[] AllRoles = [RoleCodes.Admin, RoleCodes.Manager, RoleCodes.Sales, RoleCodes.Technician, RoleCodes.Customer];

    private static AuthApiFactory Factory() => Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), new()
    {
        ["Rag:ServiceSharedSecret"] = Secret, ["RateLimiting:RagAdminRead:PermitLimit"] = "1000", ["RateLimiting:RagAdminConfig:PermitLimit"] = "1000",
    });

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path, object? body = null) => method switch
    {
        "GET" => client.GetAsync(path),
        "PUT" => client.PutAsJsonAsync(path, body ?? new { }),
        _ => client.PostAsJsonAsync(path, body ?? new { }),
    };

    [Fact]
    public async Task Every_cost_endpoint_is_admin_only()
    {
        using var factory = Factory();
        var id = Guid.NewGuid();
        var calls = new (string Method, string Path, object? Body)[]
        {
            ("GET", "/api/admin/rag-usage/summary", null), ("GET", "/api/admin/rag-usage/timeseries", null), ("GET", "/api/admin/rag-usage/by-model", null),
            ("GET", "/api/admin/rag-usage/by-operation", null), ("GET", "/api/admin/rag-usage/by-role", null), ("GET", "/api/admin/rag-usage/top-users", null),
            ("GET", "/api/admin/rag-usage/requests", null), ("POST", $"/api/admin/rag-usage/{id}/review", new { costUsd = 0, note = "x" }),
            ("GET", "/api/admin/rag-budget", null), ("PUT", "/api/admin/rag-budget", new { expectedActiveVersion = 0, dailyBudgetUsd = 0.5 }),
        };
        foreach (var (method, path, body) in calls)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Send(factory.CreateClient(), method, path, body)).StatusCode);
            foreach (var role in AllRoles.Where(r => r != RoleCodes.Admin))
                Assert.True((await Send(CatalogEndpointsTests.ClientFor(factory, role), method, path, body)).StatusCode == HttpStatusCode.Forbidden, $"{role} {method} {path}");
        }
    }

    [Fact]
    public async Task Admin_reads_the_dashboard_changes_the_budget_within_ceilings_and_never_sees_secrets()
    {
        using var factory = Factory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        var summary = await BodyOf(await admin.GetAsync("/api/admin/rag-usage/summary"));
        Assert.True(summary.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(1.00m, summary.GetProperty("data").GetProperty("today").GetProperty("limitUsd").GetDecimal());
        var series = await BodyOf(await admin.GetAsync("/api/admin/rag-usage/timeseries?from=2026-11-01&to=2026-11-17"));
        Assert.Equal(17, series.GetProperty("data").GetArrayLength());
        Assert.Equal(4, (await BodyOf(await admin.GetAsync("/api/admin/rag-usage/by-operation"))).GetProperty("data").GetArrayLength());
        Assert.Equal(0, (await BodyOf(await admin.GetAsync("/api/admin/rag-usage/requests?page=1&pageSize=10"))).GetProperty("data").GetProperty("total").GetInt32());

        // Validation: ranges over 92 days, reversed ranges, unknown filters.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/rag-usage/summary?from=2026-01-01&to=2026-11-17")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/rag-usage/timeseries?from=2026-11-17&to=2026-11-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/rag-usage/requests?status=DONE")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/admin/rag-usage/{Guid.NewGuid()}/review", new { costUsd = 0, note = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/admin/rag-usage/{Guid.NewGuid()}/review", new { costUsd = -1, note = "x" })).StatusCode);

        var budget = (await BodyOf(await admin.GetAsync("/api/admin/rag-budget"))).GetProperty("data");
        Assert.Equal((0, true, 4.00m), (budget.GetProperty("activeVersion").GetInt32(), budget.GetProperty("bootstrap").GetBoolean(), budget.GetProperty("monthlyBudgetUsd").GetDecimal()));
        Assert.Equal(10m, budget.GetProperty("ceilings").GetProperty("dailyBudgetUsdMax").GetDecimal());

        // Above the hard ceiling: refused; stale version: 409; valid change: a new active version recorded with the admin.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/rag-budget", new { expectedActiveVersion = 0, dailyBudgetUsd = 25 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/rag-budget", new { dailyBudgetUsd = 0.5 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/admin/rag-budget", new { expectedActiveVersion = 3, dailyBudgetUsd = 0.5 })).StatusCode);
        var changed = await admin.PutAsJsonAsync("/api/admin/rag-budget", new { expectedActiveVersion = 0, dailyBudgetUsd = 0.5, generationEnabled = false, notes = "Drill" });
        var active = (await BodyOf(changed)).GetProperty("data").GetProperty("active");
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal((1, 0.5m, false), (active.GetProperty("version").GetInt32(), active.GetProperty("dailyBudgetUsd").GetDecimal(), active.GetProperty("generationEnabled").GetBoolean()));
        var after = (await BodyOf(await admin.GetAsync("/api/admin/rag-budget"))).GetProperty("data");
        Assert.Contains(after.GetProperty("alerts").EnumerateArray(), a => a.GetProperty("code").GetString() == "GENERATION_DISABLED");

        // The model catalog shows prices and the cheapest default; nothing secret anywhere.
        var models = await (await admin.GetAsync("/api/admin/rag-configuration/models")).Content.ReadAsStringAsync();
        Assert.Contains("\"gpt-6-luna\"", models);
        Assert.Contains("\"isDefault\":true", models);
        foreach (var path in new[] { "/api/admin/rag-budget", "/api/admin/rag-usage/summary", "/api/admin/rag-configuration/models" })
        {
            var text = await (await admin.GetAsync(path)).Content.ReadAsStringAsync();
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("sk-", text);
        }
    }
}
