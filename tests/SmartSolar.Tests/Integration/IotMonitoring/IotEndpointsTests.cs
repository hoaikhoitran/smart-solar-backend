using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Api.Controllers;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.IotMonitoring;

/// <summary>
/// IoT endpoints through the real pipeline (authentication, roles, feature gate, rate limiting, envelope, headers): the role matrix,
/// the whole provision → bind → ingest → read flow over HTTP, the device rate limit and the unchanged energy monitoring contract.
/// </summary>
public sealed class IotEndpointsTests
{
    private const string Admin = "/api/admin/iot/devices";

    private static string Readings(string code) => $"/api/iot/devices/{code}/readings";
    private static string CustomerMonitoring(Guid project) => $"/api/customers/me/installation-projects/{project}/energy-monitoring";
    private static string StaffMonitoring(Guid project) => $"/api/installation-projects/{project}/energy-monitoring";

    private static AuthApiFactory Start(Dictionary<string, string?>? extra = null) => Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), extra);

    /// <summary>A COMPLETED project row of the customer (foreign keys off for the insert: the IoT rules only need the project and its customer).</summary>
    private static async Task<Guid> SeedProjectAsync(AuthApiFactory factory, Guid customerId)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        await using var db = factory.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        try
        {
            db.InstallationProjects.Add(new InstallationProject
            {
                Id = id, ContractId = Guid.NewGuid(), SurveyRequestId = Guid.NewGuid(), CustomerUserId = customerId, PropertySiteId = Guid.NewGuid(), ManagerUserId = Guid.NewGuid(),
                Status = InstallationStatuses.Completed, StartedAt = now.AddDays(-3), CompletedAt = now.AddDays(-1), HandoverAt = now.AddDays(-1), AcceptanceRound = 1,
                CreatedAt = now.AddDays(-5), UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
        return id;
    }

    private static HttpRequestMessage Reading(string code, string? key, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Readings(code)) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add(IotTelemetryController.DeviceKeyHeader, key);
        return request;
    }

    [Fact]
    public async Task Device_management_is_admin_only_bindings_are_staff_only_and_customers_read_only_their_own_projects()
    {
        using var factory = Start();
        var anonymous = factory.CreateClient();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer);
        var technician = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Technician);
        var sales = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Sales);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager);
        var project = Guid.NewGuid();
        var register = new { deviceId = "ESP32-RBAC", displayName = "x" };
        var bind = new { deviceId = "ESP32-RBAC" };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Admin, register)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(CustomerMonitoring(project) + "/readings/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync(Admin, register)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync(Admin, register)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync(Admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.PostAsJsonAsync($"/api/installation-projects/{project}/iot-device", bind)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sales.GetAsync("/api/iot/bindings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsJsonAsync($"/api/installation-projects/{project}/iot-device", bind)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(StaffMonitoring(project) + "/readings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await technician.GetAsync(CustomerMonitoring(project) + "/device")).StatusCode);
        await AssertFailureAsync(await customer.GetAsync(CustomerMonitoring(project) + "/readings/latest"), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await manager.GetAsync(StaffMonitoring(project) + "/device"), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/installation-projects/{project}/iot-device", bind), HttpStatusCode.NotFound, IotErrorCodes.DeviceNotFound);
    }

    [Fact]
    public async Task Provision_bind_ingest_and_read_over_http()
    {
        // The connection status is computed from the SERVER clock (receipt time vs now, OfflineAfterSeconds = 30). The server clock is
        // controlled here so the assertions do not depend on how long the HTTP steps take on a loaded test host (a full parallel run took
        // 47 s for this test and the device then read OFFLINE); the 30 s threshold is asserted explicitly below instead.
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), null, services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var (adminId, managerId, customerId, otherId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager, managerId);
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        var other = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, otherId);
        var device = factory.CreateClient();
        var project = await SeedProjectAsync(factory, customerId);

        // Admin registers: the secret is in this response only, which must not be cached.
        var registration = await admin.PostAsJsonAsync(Admin, new { deviceId = "esp32-001", displayName = "Roof meter", sensorModel = "ACS712-30A" });
        Assert.Equal("no-store, max-age=0", registration.Headers.CacheControl?.ToString());
        var credential = await DataOf(registration, HttpStatusCode.Created);
        var secret = credential.GetProperty("secret").GetString()!;
        var deviceId = credential.GetProperty("device").GetProperty("id").GetGuid();
        Assert.Equal("ESP32-001", credential.GetProperty("device").GetProperty("deviceId").GetString());
        Assert.False(credential.GetProperty("device").TryGetProperty("secretHash", out _));
        await AssertFailureAsync(await admin.PostAsJsonAsync(Admin, new { deviceId = "ESP32-001", displayName = "Dup" }), HttpStatusCode.Conflict, IotErrorCodes.DeviceIdTaken);
        await AssertFailureAsync(await admin.PostAsJsonAsync(Admin, new { deviceId = "x", displayName = "Dup" }), HttpStatusCode.BadRequest, IotErrorCodes.ValidationFailed);

        // Not bound yet: a correct credential is still refused with 409.
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret, new { current = 0.2145 })), HttpStatusCode.Conflict, IotErrorCodes.DeviceNotBound);

        // Manager binds the device to the customer's project.
        var bound = await DataOf(await manager.PostAsJsonAsync($"/api/installation-projects/{project}/iot-device", new { deviceId = "ESP32-001", note = "Installed at the inverter" }),
            HttpStatusCode.Created);
        var bindingId = bound.GetProperty("bindingId").GetGuid();
        Assert.Equal((IotBindingStatuses.Active, customerId), (bound.GetProperty("status").GetString(), bound.GetProperty("customerUserId").GetGuid()));

        // The device posts the firmware payload with its header credential.
        var ingested = await DataOf(await device.SendAsync(Reading("ESP32-001", secret, new { current = 0.2145 })), HttpStatusCode.Created);
        Assert.Equal((0.2145, IotConnectionStatuses.Online), (ingested.GetProperty("currentAmps").GetDouble(), ingested.GetProperty("connectionStatus").GetString()));
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret + "x", new { current = 0.2 })), HttpStatusCode.Unauthorized, IotErrorCodes.DeviceUnauthorized);
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", null, new { current = 0.2 })), HttpStatusCode.Unauthorized, IotErrorCodes.DeviceUnauthorized);
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret, new { current = "abc" })), HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret, new { current = 31 })), HttpStatusCode.BadRequest, IotErrorCodes.ReadingValidationFailed);
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret, new { })), HttpStatusCode.BadRequest, IotErrorCodes.ReadingValidationFailed);

        // Customer reads: the monitoring view keeps its original fields and gains the measured block.
        var monitoring = await DataOf(await customer.GetAsync(CustomerMonitoring(project)));
        Assert.Equal((EnergyMonitoringValues.Simulated, true, EnergyMonitoringValues.MeasuredDataAvailable, IotViewMapping.MeasuredSource),
            (monitoring.GetProperty("dataSource").GetString(), monitoring.GetProperty("measuredDataAvailable").GetBoolean(), monitoring.GetProperty("status").GetString(),
                monitoring.GetProperty("measuredDataSource").GetString()));
        var measured = monitoring.GetProperty("measured");
        Assert.Equal(0.2145, measured.GetProperty("latest").GetProperty("currentAmps").GetDouble());
        Assert.Equal(IotMeasurementStatuses.NotAvailable, measured.GetProperty("powerWatts").GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, measured.GetProperty("energyImportedKwh").GetProperty("value").ValueKind);
        Assert.Equal("ESP32-001", (await DataOf(await customer.GetAsync(CustomerMonitoring(project) + "/device"))).GetProperty("deviceId").GetString());
        var latest = await DataOf(await customer.GetAsync(CustomerMonitoring(project) + "/readings/latest"));
        Assert.Equal(0.2145, latest.GetProperty("reading").GetProperty("currentAmps").GetDouble());
        var history = await DataOf(await customer.GetAsync(CustomerMonitoring(project) + "/readings?limit=10"));
        Assert.Equal(1, history.GetProperty("items").GetArrayLength());
        await AssertFailureAsync(await customer.GetAsync(CustomerMonitoring(project) + "/readings?limit=0"), HttpStatusCode.BadRequest, IotErrorCodes.ValidationFailed);

        // Another customer never sees the project; staff do.
        await AssertFailureAsync(await other.GetAsync(CustomerMonitoring(project) + "/readings/latest"), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        await AssertFailureAsync(await other.GetAsync(CustomerMonitoring(project) + "/readings"), HttpStatusCode.NotFound, InstallationErrorCodes.ProjectNotFound);
        Assert.Equal(IotConnectionStatuses.Online, (await DataOf(await manager.GetAsync(StaffMonitoring(project) + "/readings/latest"))).GetProperty("connectionStatus").GetString());
        Assert.Equal(1, (await DataOf(await manager.GetAsync(StaffMonitoring(project) + "/readings"))).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await DataOf(await manager.GetAsync("/api/iot/bindings?status=ACTIVE"))).GetArrayLength());
        Assert.Equal(IotConnectionStatuses.Online, (await DataOf(await admin.GetAsync($"{Admin}/{deviceId}"))).GetProperty("connectionStatus").GetString());
        Assert.Equal(1, (await DataOf(await admin.GetAsync($"{Admin}?search=roof"))).GetProperty("totalItems").GetInt32());
        // Exactly at the threshold the device is still ONLINE; one second later it is OFFLINE.
        clock.Now = clock.Now.AddSeconds(30);
        Assert.Equal(IotConnectionStatuses.Online, (await DataOf(await admin.GetAsync($"{Admin}/{deviceId}"))).GetProperty("connectionStatus").GetString());
        clock.Now = clock.Now.AddSeconds(1);
        Assert.Equal(IotConnectionStatuses.Offline, (await DataOf(await admin.GetAsync($"{Admin}/{deviceId}"))).GetProperty("connectionStatus").GetString());
        Assert.Equal(IotConnectionStatuses.Offline, (await DataOf(await manager.GetAsync(StaffMonitoring(project) + "/readings/latest"))).GetProperty("connectionStatus").GetString());

        // Release: history stays, the device is refused again, the customer has no current device.
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/iot/bindings/{bindingId}/release", new { }), HttpStatusCode.BadRequest, IotErrorCodes.ValidationFailed);
        Assert.Equal(IotBindingStatuses.Released, (await DataOf(await manager.PostAsJsonAsync($"/api/iot/bindings/{bindingId}/release", new { reason = "Moved" }))).GetProperty("status").GetString());
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret, new { current = 0.2 })), HttpStatusCode.Conflict, IotErrorCodes.DeviceNotBound);
        await AssertFailureAsync(await customer.GetAsync(CustomerMonitoring(project) + "/device"), HttpStatusCode.NotFound, IotErrorCodes.BindingNotFound);
        Assert.Equal(1, (await DataOf(await customer.GetAsync(CustomerMonitoring(project) + "/readings"))).GetProperty("items").GetArrayLength());

        // Disable + rotate: the old secret and the disabled device are refused with distinct statuses.
        var rotation = await admin.PostAsync($"{Admin}/{deviceId}/rotate-credential", null);
        var rotated = (await DataOf(rotation)).GetProperty("secret").GetString()!;
        Assert.NotEqual(secret, rotated);
        Assert.Equal("no-store, max-age=0", rotation.Headers.CacheControl?.ToString());
        Assert.Equal(IotDeviceStatuses.Disabled, (await DataOf(await admin.PostAsJsonAsync($"{Admin}/{deviceId}/disable", new { reason = "Returned" }))).GetProperty("status").GetString());
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", secret, new { current = 0.2 })), HttpStatusCode.Unauthorized, IotErrorCodes.DeviceUnauthorized);
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-001", rotated, new { current = 0.2 })), HttpStatusCode.Forbidden, IotErrorCodes.DeviceDisabled);
        await AssertFailureAsync(await manager.PostAsJsonAsync($"/api/installation-projects/{project}/iot-device", new { deviceId = "ESP32-001" }), HttpStatusCode.Forbidden, IotErrorCodes.DeviceDisabled);
        Assert.Equal(IotDeviceStatuses.Active, (await DataOf(await admin.PostAsync($"{Admin}/{deviceId}/enable", null))).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Ingestion_is_rate_limited_per_device()
    {
        using var factory = Start(new() { ["RateLimiting:IotTelemetryIngest:PermitLimit"] = "2" });
        var (adminId, managerId, customerId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager, managerId);
        CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        var project = await SeedProjectAsync(factory, customerId);
        var secret = (await DataOf(await admin.PostAsJsonAsync(Admin, new { deviceId = "ESP32-RL", displayName = "x" }), HttpStatusCode.Created)).GetProperty("secret").GetString()!;
        await DataOf(await manager.PostAsJsonAsync($"/api/installation-projects/{project}/iot-device", new { deviceId = "ESP32-RL" }), HttpStatusCode.Created);
        var device = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Created, (await device.SendAsync(Reading("ESP32-RL", secret, new { current = 0.1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await device.SendAsync(Reading("ESP32-RL", secret, new { current = 0.2 }))).StatusCode);
        var limited = await device.SendAsync(Reading("ESP32-RL", secret, new { current = 0.3 }));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        // Another device id has its own budget.
        await AssertFailureAsync(await device.SendAsync(Reading("ESP32-OTHER", secret, new { current = 0.3 })), HttpStatusCode.Unauthorized, IotErrorCodes.DeviceUnauthorized);
    }

    [Fact]
    public async Task The_energy_monitoring_contract_keeps_every_original_field_without_a_device()
    {
        using var factory = Start();
        var customerId = Guid.NewGuid();
        var customer = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        var project = await SeedProjectAsync(factory, customerId);

        var body = await DataOf(await customer.GetAsync(CustomerMonitoring(project)));

        Assert.Equal(new[] { "alerts", "alertsStatus", "dataSource", "handoverAt", "measured", "measuredDataAvailable", "measuredDataSource", "notice", "projectId", "projectStatus", "simulated", "status" },
            body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal((project, InstallationStatuses.Completed, EnergyMonitoringValues.Simulated, false, EnergyMonitoringValues.SimulationUnavailable, EnergyMonitoringValues.NotConfigured),
            (body.GetProperty("projectId").GetGuid(), body.GetProperty("projectStatus").GetString(), body.GetProperty("dataSource").GetString(),
                body.GetProperty("measuredDataAvailable").GetBoolean(), body.GetProperty("status").GetString(), body.GetProperty("alertsStatus").GetString()));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("simulated").ValueKind);
        Assert.Equal(0, body.GetProperty("alerts").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measured").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("measuredDataSource").ValueKind);
    }
}
