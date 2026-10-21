using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.SystemSettings;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;
using K = SmartSolar.Modules.SystemSettings.SchedulingPolicyKeys;

namespace SmartSolar.Tests.Integration.SystemSettings;

/// <summary>Admin system settings through the real pipeline: RBAC, envelope, appsettings defaults, validation codes, versioning, history, cache invalidation.</summary>
public sealed class SystemSettingsEndpointsTests
{
    private const string Route = "/api/admin/system-settings/scheduling";

    private static object Body(int? expectedVersion, string? reason, params object[] settings) => new { settings, expectedVersion, reason };

    private static async Task<JsonElement> ErrorDetails(HttpResponseMessage r, HttpStatusCode status, string code)
    {
        var body = await BodyOf(r);
        Assert.True(status == r.StatusCode, $"Expected {status} but got {(int)r.StatusCode}: {body}");
        Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
        return body.GetProperty("error").GetProperty("details");
    }

    private static JsonElement Setting(JsonElement view, string key)
        => view.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Admin_reads_and_writes_managers_only_read_and_other_roles_are_forbidden()
    {
        using var factory = new AuthApiFactory();
        var update = Body(0, "test", new { key = K.DispatchReviewSlaMinutes, value = 200 });

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync(Route, update)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Route}/history")).StatusCode);

        foreach (var role in new[] { RoleCodes.Customer, RoleCodes.Sales, RoleCodes.Technician })
        {
            var client = CatalogEndpointsTests.ClientFor(factory, role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(Route)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync(Route, update)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Route}/history")).StatusCode);
        }

        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        Assert.Equal(0, (await DataOf(await manager.GetAsync(Route))).GetProperty("version").GetInt32());
        await DataOf(await manager.GetAsync($"{Route}/history"));
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync(Route, update)).StatusCode);

        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        Assert.Equal(1, (await DataOf(await admin.PutAsJsonAsync(Route, update))).GetProperty("version").GetInt32());
        // The manager sees the admin's change.
        Assert.Equal(200, Setting(await DataOf(await manager.GetAsync(Route)), K.DispatchReviewSlaMinutes).GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task Get_returns_the_bootstrap_defaults_from_appsettings_with_definitions()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["Scheduling:AcceptanceWindowMinutes"] = "300" });
        var data = await DataOf(await CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin).GetAsync(Route));

        Assert.Equal((0, true, 15), (data.GetProperty("version").GetInt32(), data.GetProperty("bootstrap").GetBoolean(), data.GetProperty("cacheTtlSeconds").GetInt32()));
        Assert.Equal(SmartSolar.Modules.SystemSettings.SchedulingPolicyDefinitions.Default.All.Count, data.GetProperty("settings").GetArrayLength());
        Assert.NotEmpty(data.GetProperty("rules").EnumerateArray());
        var expected = new Dictionary<string, int>
        {
            [K.DispatchReviewSlaMinutes] = 240, [K.DispatchEscalationDelayMinutes] = 0, [K.DispatchReminderIntervalMinutes] = 0,
            [K.DispatchProposalResponseWindowMinutes] = 1440, [K.AcceptanceWindowMinutes] = 300, [K.AcceptanceMinimumLeadMinutes] = 120,
            [K.AcceptanceReminderLeadMinutes] = 0, [K.CustomerChangeLeadMinutes] = 120, [K.MinimumBookingLeadMinutes] = 0, [K.BookingHorizonDays] = 30,
            [K.BufferBetweenAppointmentsMinutes] = 30, [K.OnSiteSurveyEarlyStartMinutes] = 120, [K.InstallationMaxAssignmentDays] = 14,
        };
        foreach (var (key, value) in expected)
        {
            var s = Setting(data, key);
            Assert.Equal((value, value, "DEFAULT", "INT"), (s.GetProperty("value").GetInt32(), s.GetProperty("defaultValue").GetInt32(),
                s.GetProperty("source").GetString(), s.GetProperty("type").GetString()));
        }
        var horizon = Setting(data, K.BookingHorizonDays);
        Assert.Equal(("DAYS", 1, 365, true), (horizon.GetProperty("unit").GetString(), horizon.GetProperty("min").GetInt32(), horizon.GetProperty("max").GetInt32(),
            horizon.GetProperty("adminEditable").GetBoolean()));
        Assert.False(string.IsNullOrWhiteSpace(horizon.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task Put_validates_keys_units_values_and_rules()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = "scheduling.unknown", value = 5 })), HttpStatusCode.BadRequest,
            SystemSettingErrorCodes.UnknownKey)).TryGetProperty("scheduling.unknown", out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = K.BookingHorizonDays, value = 10, unit = "HOURS" })),
            HttpStatusCode.BadRequest, SystemSettingErrorCodes.InvalidUnit)).TryGetProperty(K.BookingHorizonDays, out _));
        foreach (var bad in new object[] { "abc", 1.5, true, 3_000_000_000L })
        {
            Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = K.DispatchReviewSlaMinutes, value = bad })),
                HttpStatusCode.BadRequest, SystemSettingErrorCodes.InvalidValue)).TryGetProperty(K.DispatchReviewSlaMinutes, out _));
        }
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = K.DispatchReviewSlaMinutes })),
            HttpStatusCode.BadRequest, SystemSettingErrorCodes.InvalidValue)).TryGetProperty(K.DispatchReviewSlaMinutes, out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = K.BufferBetweenAppointmentsMinutes, value = 241 })),
            HttpStatusCode.BadRequest, SystemSettingErrorCodes.InvalidValue)).TryGetProperty(K.BufferBetweenAppointmentsMinutes, out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = K.BookingHorizonDays, value = 1 },
                new { key = K.MinimumBookingLeadMinutes, value = 1440 })), HttpStatusCode.BadRequest, SystemSettingErrorCodes.RuleViolation))
            .TryGetProperty(K.MinimumBookingLeadMinutes, out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(null, "test", new { key = K.DispatchReviewSlaMinutes, value = 100 })),
            HttpStatusCode.BadRequest, SystemSettingErrorCodes.ValidationFailed)).TryGetProperty("expectedVersion", out _));
        Assert.True((await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test")), HttpStatusCode.BadRequest, SystemSettingErrorCodes.ValidationFailed))
            .TryGetProperty("settings", out _));

        // Nothing was saved.
        var data = await DataOf(await admin.GetAsync(Route));
        Assert.Equal(0, data.GetProperty("version").GetInt32());
        Assert.Equal(0, (await DataOf(await admin.GetAsync($"{Route}/history"))).GetProperty("totalItems").GetInt32());
    }

    [Fact]
    public async Task Put_is_versioned_conflicts_on_a_stale_version_audits_and_invalidates_the_local_cache()
    {
        using var factory = new AuthApiFactory();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        // Warm the process-wide policy cache with the bootstrap snapshot.
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(0, (await scope.ServiceProvider.GetRequiredService<ISchedulingPolicyProvider>().GetAsync(CancellationToken.None)).Version);

        var first = await DataOf(await admin.PutAsJsonAsync(Route, Body(0, "Pilot week",
            new { key = K.DispatchReviewSlaMinutes, value = 180, unit = "MINUTES" }, new { key = K.BookingHorizonDays, value = 21 })));
        Assert.Equal((1, false), (first.GetProperty("version").GetInt32(), first.GetProperty("bootstrap").GetBoolean()));
        var sla = Setting(first, K.DispatchReviewSlaMinutes);
        Assert.Equal((180, "ADMIN", 1), (sla.GetProperty("value").GetInt32(), sla.GetProperty("source").GetString(), sla.GetProperty("version").GetInt32()));

        // Immediately visible through the provider (local invalidation, no TTL wait).
        using (var scope = factory.Services.CreateScope())
        {
            var snapshot = await scope.ServiceProvider.GetRequiredService<ISchedulingPolicyProvider>().GetAsync(CancellationToken.None);
            Assert.Equal((1, 180, 21), (snapshot.Version, snapshot.DispatchReviewSlaMinutes, snapshot.BookingHorizonDays));
        }

        var conflict = await ErrorDetails(await admin.PutAsJsonAsync(Route, Body(0, "test", new { key = K.DispatchReviewSlaMinutes, value = 100 })),
            HttpStatusCode.Conflict, SystemSettingErrorCodes.VersionConflict);
        Assert.Equal(1, conflict.GetProperty("currentVersion").GetInt32());

        await DataOf(await admin.PutAsJsonAsync(Route, Body(1, "test", new { key = K.DispatchReviewSlaMinutes, value = 120 })));

        var history = await DataOf(await admin.GetAsync($"{Route}/history?page=1&pageSize=2"));
        Assert.Equal((3, 2, 2), (history.GetProperty("totalItems").GetInt32(), history.GetProperty("totalPages").GetInt32(), history.GetProperty("items").GetArrayLength()));
        var newest = history.GetProperty("items")[0];
        Assert.Equal((2, K.DispatchReviewSlaMinutes, 180, 120, "MINUTES"), (newest.GetProperty("version").GetInt32(), newest.GetProperty("key").GetString(),
            newest.GetProperty("oldValue").GetInt32(), newest.GetProperty("newValue").GetInt32(), newest.GetProperty("unit").GetString()));
        var page2 = await DataOf(await admin.GetAsync($"{Route}/history?page=2&pageSize=2"));
        var oldest = page2.GetProperty("items")[0];
        Assert.Equal((1, K.BookingHorizonDays, 30, 21, "Pilot week"), (oldest.GetProperty("version").GetInt32(), oldest.GetProperty("key").GetString(),
            oldest.GetProperty("oldValue").GetInt32(), oldest.GetProperty("newValue").GetInt32(), oldest.GetProperty("reason").GetString()));
        var filtered = await DataOf(await admin.GetAsync($"{Route}/history?key={K.BookingHorizonDays}"));
        Assert.Equal(1, filtered.GetProperty("totalItems").GetInt32());
    }
}
