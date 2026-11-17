using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Api.Controllers;
using SmartSolar.Api.Extensions;
using SmartSolar.Modules.Identity.AdminUsers;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.PasswordRecovery;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.IotMonitoring;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.Security;

/// <summary>
/// Regression tests of the 2026-11-16 production audit fixes, through the real pipeline: SEC-01 (Swagger and HSTS outside Development),
/// RL-03 (forwarded headers only from trusted proxies), RL-01 (per-IP ingest ceiling), RL-02 (bounded ingest body) and INT-02 for admin links.
/// </summary>
public sealed class ProductionHardeningTests
{
    private const string Password = "correct horse battery";

    // ---------------------------------------------------------------- SEC-01

    [Fact]
    public async Task Production_hides_swagger_sends_hsts_on_https_and_still_answers_the_health_probes()
    {
        using var baseFactory = new AuthApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var https = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://api.smartsolar.test") });

        Assert.Equal(HttpStatusCode.NotFound, (await https.GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await https.GetAsync("/swagger/index.html")).StatusCode);
        var products = await https.GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.OK, products.StatusCode);
        Assert.True(products.Headers.Contains("Strict-Transport-Security"), "HSTS must be sent on HTTPS outside Development");
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Swagger_follows_the_explicit_switch_and_development_keeps_it_on()
    {
        using var development = new AuthApiFactory();
        Assert.Equal(HttpStatusCode.OK, (await development.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);

        using var switchedOff = new AuthApiFactory(new() { ["Swagger:Enabled"] = "false" });
        Assert.Equal(HttpStatusCode.NotFound, (await switchedOff.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);

        using var baseFactory = new AuthApiFactory(new() { ["Swagger:Enabled"] = "true" });
        using var productionOptIn = baseFactory.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        Assert.Equal(HttpStatusCode.OK, (await productionOptIn.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    // ---------------------------------------------------------------- RL-03

    /// <summary>Makes every request arrive from <paramref name="peer"/> (the socket address a reverse proxy would have).</summary>
    private sealed class PeerAddressStartupFilter(IPAddress peer) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = peer;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private static AuthApiFactory LoginLimitedTo(int permits, IPAddress? peer, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["RateLimiting:Login:PermitLimit"] = permits.ToString() };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new AuthApiFactory(settings)
        {
            ConfigureTestServices = services =>
            {
                if (peer is not null) services.AddSingleton<IStartupFilter>(new PeerAddressStartupFilter(peer));
            },
        };
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string? forwardedFor)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { email = "nobody@example.com", password = Password }) };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task A_client_cannot_escape_the_login_limit_by_spoofing_x_forwarded_for()
    {
        using var factory = LoginLimitedTo(2, IPAddress.Parse("203.0.113.7"));
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.2")).StatusCode);
        var third = await LoginAsync(client, "198.51.100.3");
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.NotNull(third.Headers.RetryAfter);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "10.20.0.5")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.20.0.0/24")]
    public async Task Behind_a_trusted_proxy_each_client_gets_its_own_login_budget(string key, string value)
    {
        using var factory = LoginLimitedTo(2, IPAddress.Parse("10.20.0.5"), new() { [key] = value });
        var client = factory.CreateClient();

        // Two different real clients behind the proxy: separate buckets.
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, "198.51.100.2")).StatusCode);
        // The first client is over its own limit; a prepended fake address does not help (only the last hop is used).
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, "198.51.100.1")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, "192.0.2.99, 198.51.100.1")).StatusCode);
    }

    [Fact]
    public void Invalid_trusted_proxy_settings_stop_the_api_at_startup()
    {
        Assert.Null(SmartSolar.Api.Extensions.ForwardedHeadersExtensions.TryParseNetwork("10.0.0.0/33"));
        Assert.Null(SmartSolar.Api.Extensions.ForwardedHeadersExtensions.TryParseNetwork("not-a-network"));
        Assert.NotNull(SmartSolar.Api.Extensions.ForwardedHeadersExtensions.TryParseNetwork("172.30.250.0/24"));
        using var factory = new AuthApiFactory(new() { ["ForwardedHeaders:KnownNetworks:0"] = "300.1.1.0/24" });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ForwardedHeaders", error.ToString());
    }

    // ---------------------------------------------------------------- RL-01 / RL-02 (IoT ingest)

    private static async Task<(AuthApiFactory Factory, string Secret)> BoundDeviceAsync(Dictionary<string, string?> limits, string code = "ESP32-HARD")
    {
        var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), limits);
        var (adminId, managerId, customerId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin, adminId);
        var manager = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Manager, managerId);
        CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, customerId);
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            db.InstallationProjects.Add(new InstallationProject
            {
                Id = projectId, ContractId = Guid.NewGuid(), SurveyRequestId = Guid.NewGuid(), CustomerUserId = customerId, PropertySiteId = Guid.NewGuid(),
                ManagerUserId = Guid.NewGuid(), Status = InstallationStatuses.Completed, StartedAt = now.AddDays(-3), CompletedAt = now.AddDays(-1),
                HandoverAt = now.AddDays(-1), AcceptanceRound = 1, CreatedAt = now.AddDays(-5), UpdatedAt = now,
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }
        var secret = (await DataOf(await admin.PostAsJsonAsync("/api/admin/iot/devices", new { deviceId = code, displayName = "x" }), HttpStatusCode.Created))
            .GetProperty("secret").GetString()!;
        await DataOf(await manager.PostAsJsonAsync($"/api/installation-projects/{projectId}/iot-device", new { deviceId = code }), HttpStatusCode.Created);
        return (factory, secret);
    }

    private static HttpRequestMessage Reading(string code, string? key, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/iot/devices/{code}/readings") { Content = content };
        if (key is not null) request.Headers.Add(IotTelemetryController.DeviceKeyHeader, key);
        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Rotating_device_ids_hits_the_per_ip_ceiling()
    {
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), new()
        {
            ["RateLimiting:IotTelemetryIngest:PermitLimit"] = "12", [$"{RateLimitingExtensions.IotTelemetryIngestPerIpSection}:PermitLimit"] = "5",
        });
        var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
            statuses.Add((await client.SendAsync(Reading($"ESP32-ROT-{i:D3}", "guess", Json("{\"current\":0.1}")))).StatusCode);

        Assert.Equal(Enumerable.Repeat(HttpStatusCode.Unauthorized, 5).Concat(Enumerable.Repeat(HttpStatusCode.TooManyRequests, 3)), statuses);
        // The ceiling applies to the ingest endpoint only: an authenticated read from the same address is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/products")).StatusCode);
    }

    [Fact]
    public async Task A_real_device_sending_every_10_seconds_is_never_throttled_by_the_production_limits()
    {
        var (factory, secret) = await BoundDeviceAsync(new()
        {
            ["RateLimiting:IotTelemetryIngest:PermitLimit"] = "12", [$"{RateLimitingExtensions.IotTelemetryIngestPerIpSection}:PermitLimit"] = "60",
        });
        using var _ = factory;
        var client = factory.CreateClient();
        // One minute of the agreed cycle (6 readings) plus a retry of each: 12 requests, all accepted.
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Reading("ESP32-HARD", secret, Json($"{{\"current\":0.{i + 1}}}")))).StatusCode);
        // The 13th in the same minute is over the per-device limit.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Reading("ESP32-HARD", secret, Json("{\"current\":0.5}")))).StatusCode);
    }

    /// <summary>
    /// The test server always computes Content-Length, so a chunked upload (no length) is checked on the filter directly: Kestrel leaves
    /// <c>ContentLength</c> null for chunked bodies.
    /// </summary>
    [Theory]
    [InlineData(null, 411, BoundedRequestBodyAttribute.LengthRequiredCode)]
    [InlineData(1025L, 413, BoundedRequestBodyAttribute.TooLargeCode)]
    [InlineData(1024L, null, null)]
    [InlineData(20L, null, null)]
    public void The_bounded_body_filter_refuses_unknown_or_oversized_lengths(long? contentLength, int? status, string? code)
    {
        var http = new DefaultHttpContext();
        http.Request.ContentLength = contentLength;
        var action = new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        var context = new Microsoft.AspNetCore.Mvc.Filters.ResourceExecutingContext(action, [], []);

        new BoundedRequestBodyAttribute(IotTelemetryController.MaxBodyBytes).OnResourceExecuting(context);

        if (status is null)
        {
            Assert.Null(context.Result);
            return;
        }
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.ObjectResult>(context.Result);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(code, Assert.IsType<SmartSolar.Api.Contracts.ApiResponse<object>>(result.Value).Error!.Code);
    }

    [Fact]
    public async Task The_ingest_body_is_refused_before_it_is_read_when_too_large_or_of_unknown_length()
    {
        var (factory, secret) = await BoundDeviceAsync(new());
        using var _ = factory;
        var client = factory.CreateClient();

        var big = await client.SendAsync(Reading("ESP32-HARD", secret, Json("{\"current\":0.1" + new string(' ', 8 * 1024 * 1024) + "}")));
        await AssertFailureAsync(big, HttpStatusCode.RequestEntityTooLarge, BoundedRequestBodyAttribute.TooLargeCode);
        var justOver = await client.SendAsync(Reading("ESP32-HARD", secret, Json("{\"current\":0.1" + new string(' ', IotTelemetryController.MaxBodyBytes) + "}")));
        await AssertFailureAsync(justOver, HttpStatusCode.RequestEntityTooLarge, BoundedRequestBodyAttribute.TooLargeCode);
        // Also for an anonymous caller without a key: refused before any work.
        await AssertFailureAsync(await client.SendAsync(Reading("ESP32-HARD", null, Json(new string(' ', 4096)))), HttpStatusCode.RequestEntityTooLarge,
            BoundedRequestBodyAttribute.TooLargeCode);

        // The firmware payload is unchanged and still accepted; validation answers are unchanged.
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Reading("ESP32-HARD", secret, Json("{ \"current\": 0.2145 }")))).StatusCode);
        await AssertFailureAsync(await client.SendAsync(Reading("ESP32-HARD", secret, Json("{\"current\":\"abc\"}"))), HttpStatusCode.BadRequest, AuthErrorCodes.ValidationFailed);
        await AssertFailureAsync(await client.SendAsync(Reading("ESP32-HARD", secret, Json("{\"current\":31}"))), HttpStatusCode.BadRequest, IotErrorCodes.ReadingValidationFailed);
        await AssertFailureAsync(await client.SendAsync(Reading("ESP32-HARD", "wrong", Json("{\"current\":0.1}"))), HttpStatusCode.Unauthorized, IotErrorCodes.DeviceUnauthorized);
    }

    // ---------------------------------------------------------------- INT-02 (admin links)

    [Fact]
    public async Task An_admin_setup_link_created_while_the_broker_is_down_is_queued_and_published_later()
    {
        using var factory = new AuthApiFactory { EmailCanDeliver = true };
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);
        factory.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");

        var data = await DataOf(await admin.PostAsJsonAsync("/api/admin/users", new { email = "tech@example.com", fullName = "Tech", roles = new[] { "TECHNICIAN" } }),
            HttpStatusCode.Created);
        Assert.Equal(EmailDeliveryStates.Queued, data.GetProperty("activation").GetProperty("emailDelivery").GetString());
        Assert.Empty(factory.Publisher.Published);

        Guid dispatchId;
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.PasswordResetDispatches.SingleAsync();
            Assert.Equal((PasswordResetDispatchStatuses.Pending, 1, "InvalidOperationException"), (row.Status, row.PublishAttempts, row.LastError));
            dispatchId = row.Id;
            // Make it due now (the backoff would otherwise wait 15 s of real time).
            row.NextAttemptAt = DateTimeOffset.UnixEpoch;
            await db.SaveChangesAsync();
        }

        factory.Publisher.ThrowOnPublish = null;
        using (var scope = factory.Services.CreateScope())
            Assert.Equal((1, 0), await scope.ServiceProvider.GetRequiredService<PasswordResetDispatcher>().SweepAsync(10, CancellationToken.None));
        var published = Assert.Single(factory.Publisher.PublishedOf<AccountSetupRequestedEvent>());
        Assert.Equal(dispatchId, published.DispatchId);
        Assert.Contains("token=", published.SetupUrl);
    }
}
