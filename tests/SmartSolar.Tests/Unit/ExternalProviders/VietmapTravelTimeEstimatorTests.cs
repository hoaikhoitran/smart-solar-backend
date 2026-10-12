using System.Net;
using System.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SmartSolar.Infrastructure.ExternalProviders;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Travel;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.ExternalProviders;

/// <summary>
/// Deterministic tests for the Vietmap Route v4 adapter. Every response is a SYNTHETIC fixture
/// that copies the documented v4 structure; no request leaves the process.
/// </summary>
public sealed class VietmapTravelTimeEstimatorTests
{
    private const string FakeKey = "test-key-NOT-REAL-1234567890";
    private static readonly GeoPoint Origin = new(10.8494m, 106.7537m);
    private static readonly GeoPoint Destination = new(10.7653m, 106.6037m);
    private static readonly DateTimeOffset Departure = new(2026, 10, 20, 2, 37, 0, TimeSpan.Zero);
    private static readonly TravelEstimateRequest Request = new(Origin, Destination, Departure);

    private static string Ok(double distance = 18_234.5, long timeMs = 2_712_000, string extra = "")
        => "{\"license\":\"vietmap\",\"code\":\"OK\",\"messages\":null,\"paths\":[{\"distance\":" + distance.ToString(System.Globalization.CultureInfo.InvariantCulture)
           + ",\"time\":" + timeMs + ",\"weight\":1.0,\"points_encoded\":true,\"points\":\"abc\",\"instructions\":[]" + extra + "}]}";

    private static (ITravelTimeEstimator Estimator, CapturingLoggerProvider Logs) Build(
        StubHttpMessageHandler stub, ICacheStore? cache = null, bool enabled = true, string? key = FakeKey, string vehicle = "motorcycle")
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Vietmap:Enabled"] = enabled ? "true" : "false",
            ["Providers:Vietmap:ApiKey"] = key,
            ["Providers:Vietmap:BaseUrl"] = "\"https://maps.vietmap.vn\"",
            ["Providers:Vietmap:Vehicle"] = "\"" + vehicle + "\"",
            ["Providers:Vietmap:RetryBaseDelayMilliseconds"] = "1",
            ["Providers:Vietmap:AttemptTimeoutMilliseconds"] = "200",
        }).Build();

        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddSingleton(cache ?? new FakeCacheStore());
        services.AddSingleton<TimeProvider>(new FixedTimeProvider(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)));
        ExternalProvidersRegistration.AddVietmapClient(services, config).ConfigurePrimaryHttpMessageHandler(() => stub);
        return (services.BuildServiceProvider().GetRequiredService<ITravelTimeEstimator>(), logs);
    }

    [Fact]
    public async Task Maps_the_request_to_route_v4_with_motorcycle_repeated_points_and_utc_time()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Ok());
        var (estimator, _) = Build(stub);

        await estimator.EstimateAsync(Request, CancellationToken.None);

        var uri = Assert.Single(stub.Requests).RequestUri!;
        Assert.Equal("https://maps.vietmap.vn/api/route/v4", uri.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal(new[] { "10.84940,106.75370", "10.76530,106.60370" }, query.GetValues("point"));
        Assert.Equal("motorcycle", query["vehicle"]);
        Assert.Equal("2026-10-20T02:30:00Z", query["time"]);  // 02:37 floored to the 15-minute bucket
        Assert.Equal("congestion,congestion_distance", query["annotations"]);
        Assert.Equal(FakeKey, query["apikey"]);
    }

    [Fact]
    public async Task Converts_meters_and_milliseconds()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Ok(distance: 18_234.5, timeMs: 2_712_345));
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(18_234.5, result.DistanceMeters);
        Assert.Equal(TimeSpan.FromMilliseconds(2_712_345), result.Duration);
        Assert.Equal(45.20575, result.Duration!.Value.TotalMinutes, 5);
        Assert.Equal("VIETMAP", result.Provider);
        Assert.Equal("v4", result.ProviderVersion);
        Assert.Equal("motorcycle", result.Vehicle);
        Assert.Equal(Departure, result.DepartureAtUtc);
    }

    /// <summary>Route v4 docs: congestion lives in paths[].annotations; congestion_distance lists only heavy-or-worse segments.</summary>
    [Fact]
    public async Task Reads_congestion_from_the_documented_annotations_object()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK,
            Ok(extra: ",\"annotations\":{\"congestion\":[\"low\",\"heavy\",\"low\",\"severe\"],\"congestion_distance\":[50.5,25.0]}"));
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Congestion!.SegmentsByLevel["low"]);
        Assert.Equal(1, result.Congestion.SegmentsByLevel["heavy"]);
        Assert.Equal(1, result.Congestion.SegmentsByLevel["severe"]);
        Assert.Equal(75.5, result.Congestion.HeavyOrWorseMeters);
    }

    /// <summary>Shape observed in a live Route v4 response (values here are synthetic): objects with point ranges, arrays aligned.</summary>
    [Fact]
    public async Task Reads_live_style_congestion_objects_and_computes_meters_per_level()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Ok(extra:
            ",\"annotations\":{\"congestion\":[{\"value\":\"low\",\"first\":0,\"last\":1},{\"value\":\"heavy\",\"first\":1,\"last\":2},{\"value\":\"low\",\"first\":2,\"last\":4}],"
            + "\"congestion_distance\":[{\"value\":3.3,\"first\":0,\"last\":1},{\"value\":98,\"first\":1,\"last\":2},{\"value\":62,\"first\":2,\"last\":4}],"
            + "\"history_speed\":{}}"));
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Congestion!.SegmentsByLevel["low"]);
        Assert.Equal(65.3, result.Congestion.MetersByLevel!["low"]);
        Assert.Equal(98, result.Congestion.MetersByLevel["heavy"]);
        Assert.Equal(98, result.Congestion.HeavyOrWorseMeters);
    }

    [Fact]
    public async Task Missing_congestion_is_null_and_the_route_stays_valid()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Ok(distance: 11_920, timeMs: 1_434_000));
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Null(result.Congestion);
        Assert.Equal(11_920, result.DistanceMeters);
        Assert.Equal(TimeSpan.FromMilliseconds(1_434_000), result.Duration);
    }

    [Fact]
    public async Task Congestion_levels_without_distances_report_no_heavy_meters()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK,
            Ok(extra: ",\"annotations\":{\"congestion\":[\"moderate\",\"unknown\"]}"));
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(1, result.Congestion!.SegmentsByLevel["moderate"]);
        Assert.Null(result.Congestion.HeavyOrWorseMeters);
    }

    [Theory]
    [InlineData(",\"annotations\":{\"congestion\":[\"gridlock\"]}")]
    [InlineData(",\"annotations\":{\"congestion\":[1,2]}")]
    [InlineData(",\"annotations\":{\"congestion\":[]}")]
    [InlineData(",\"congestion\":[\"low\"],\"congestion_distance\":[10]")]
    public async Task Unusable_congestion_annotations_are_ignored_without_failing_the_route(string extra)
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, Ok(extra: extra));
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.True(result.IsOk);
        Assert.Null(result.Congestion);
    }

    [Theory]
    [InlineData("{\"code\":\"ZERO_RESULTS\",\"paths\":[]}", RouteCalculationStatuses.RouteNotFound)]
    [InlineData("{\"code\":\"OVER_DAILY_LIMIT\"}", RouteCalculationStatuses.QuotaExceeded)]
    [InlineData("{\"code\":\"INVALID_REQUEST\"}", RouteCalculationStatuses.RejectedRequest)]
    [InlineData("{\"code\":\"MAX_POINTS_EXCEED\"}", RouteCalculationStatuses.RejectedRequest)]
    [InlineData("{\"code\":\"ERROR_UNKNOWN\"}", RouteCalculationStatuses.ProviderUnavailable)]
    [InlineData("{\"code\":\"OK\",\"paths\":[]}", RouteCalculationStatuses.InvalidResponse)]
    [InlineData("{\"code\":\"OK\",\"paths\":[{\"distance\":-1,\"time\":10}]}", RouteCalculationStatuses.InvalidResponse)]
    [InlineData("{\"code\":\"OK\",\"paths\":[{\"distance\":10,\"time\":-5}]}", RouteCalculationStatuses.InvalidResponse)]
    [InlineData("{\"code\":\"OK\",\"paths\":[{\"distance\":10}]}", RouteCalculationStatuses.InvalidResponse)]
    [InlineData("{\"code\":\"OK\",\"paths\":[{\"distance\":\"10\",\"time\":5}]}", RouteCalculationStatuses.InvalidResponse)]
    [InlineData("{\"paths\":[]}", RouteCalculationStatuses.InvalidResponse)]
    [InlineData("not json", RouteCalculationStatuses.InvalidResponse)]
    public async Task Provider_problems_become_explicit_unverified_statuses(string body, string expected)
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.OK, body);
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Equal(expected, result.Status);
        Assert.Null(result.Duration);
        Assert.Null(result.DistanceMeters);
    }

    [Fact]
    public async Task Rate_limited_responses_are_not_retried()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.TooManyRequests, "{}");
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(RouteCalculationStatuses.RateLimited, result.Status);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Quota_exhaustion_returned_with_an_error_status_is_not_retried()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.Forbidden, "{\"code\":\"OVER_DAILY_LIMIT\"}");
        var (estimator, _) = Build(stub);

        Assert.Equal(RouteCalculationStatuses.QuotaExceeded, (await estimator.EstimateAsync(Request, CancellationToken.None)).Status);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_once_then_reported()
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.BadGateway, "<html>bad gateway</html>");
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(RouteCalculationStatuses.ProviderUnavailable, result.Status);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task Server_error_then_success_recovers()
    {
        var stub = new StubHttpMessageHandler().Enqueue(HttpStatusCode.ServiceUnavailable, "{}").Enqueue(HttpStatusCode.OK, Ok());
        var (estimator, _) = Build(stub);

        Assert.True((await estimator.EstimateAsync(Request, CancellationToken.None)).IsOk);
    }

    [Fact]
    public async Task Timeout_is_a_failure_not_a_zero_duration()
    {
        var stub = new StubHttpMessageHandler().AlwaysHang();
        var (estimator, _) = Build(stub);

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(RouteCalculationStatuses.Timeout, result.Status);
        Assert.Null(result.Duration);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var stub = new StubHttpMessageHandler().AlwaysHang();
        var (estimator, _) = Build(stub);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => estimator.EstimateAsync(Request, cts.Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Enabled_without_an_api_key_fails_fast_at_startup(string? key)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Vietmap:Enabled"] = "true",
            ["Providers:Vietmap:ApiKey"] = key,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICacheStore>(new FakeCacheStore());
        ExternalProvidersRegistration.AddVietmapClient(services, config);

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            services.BuildServiceProvider().GetRequiredService<ITravelTimeEstimator>());
    }

    [Theory]
    [InlineData(false, FakeKey)]
    [InlineData(false, null)]
    public async Task Disabled_configuration_never_calls_the_provider(bool enabled, string? key)
    {
        var stub = new StubHttpMessageHandler().Always(HttpStatusCode.OK, Ok());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Vietmap:Enabled"] = enabled ? "true" : "false",
            ["Providers:Vietmap:ApiKey"] = key,
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICacheStore>(new FakeCacheStore());
        ExternalProvidersRegistration.AddVietmapClient(services, config).ConfigurePrimaryHttpMessageHandler(() => stub);
        var estimator = services.BuildServiceProvider().GetRequiredService<ITravelTimeEstimator>();

        var result = await estimator.EstimateAsync(Request, CancellationToken.None);

        Assert.Equal(RouteCalculationStatuses.ProviderDisabled, result.Status);
        Assert.Empty(stub.Requests);
    }

    [Fact]
    public async Task Successful_estimates_are_cached_briefly_and_failures_are_not()
    {
        var stub = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, "{\"code\":\"ZERO_RESULTS\"}")
            .Always(HttpStatusCode.OK, Ok());
        var cache = new FakeCacheStore();
        var (estimator, _) = Build(stub, cache);

        Assert.False((await estimator.EstimateAsync(Request, CancellationToken.None)).IsOk);
        Assert.True((await estimator.EstimateAsync(Request, CancellationToken.None)).IsOk);
        Assert.True((await estimator.EstimateAsync(Request with { DepartureAtUtc = Departure.AddMinutes(5) }, CancellationToken.None)).IsOk);

        Assert.Equal(2, stub.Requests.Count);  // third call hit the cache: same 15-minute bucket
        var cachedKey = Assert.Single(cache.Sets);
        Assert.DoesNotContain(FakeKey, cachedKey);
        Assert.True(cache.ExpirationOf(cachedKey) <= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task The_api_key_never_appears_in_logs_even_on_failures()
    {
        var stub = new StubHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, Ok())
            .Enqueue(HttpStatusCode.OK, "{\"code\":\"OVER_DAILY_LIMIT\"}")
            .Always(HttpStatusCode.InternalServerError, "oops");
        var (estimator, logs) = Build(stub);

        await estimator.EstimateAsync(Request, CancellationToken.None);
        await estimator.EstimateAsync(Request with { Destination = Origin }, CancellationToken.None);
        await estimator.EstimateAsync(Request with { Origin = Destination }, CancellationToken.None);

        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(FakeKey, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, m => m.Contains("apikey=", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Non_motorcycle_vehicle_configuration_is_rejected_at_startup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Providers:Vietmap:Enabled"] = "true",
            ["Providers:Vietmap:ApiKey"] = FakeKey,
            ["Providers:Vietmap:Vehicle"] = "car",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICacheStore>(new FakeCacheStore());
        ExternalProvidersRegistration.AddVietmapClient(services, config);
        var provider = services.BuildServiceProvider();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SmartSolar.Infrastructure.ExternalProviders.Vietmap.VietmapOptions>>().Value);
    }
}
