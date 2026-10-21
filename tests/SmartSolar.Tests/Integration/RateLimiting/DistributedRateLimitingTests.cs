using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Infrastructure.Caching;
using SmartSolar.Modules.Common.RateLimiting;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.RateLimiting;

/// <summary>
/// Cluster-wide limits for expensive operations (chat here): several API replicas share one counter,
/// a Redis outage falls back to per-instance limits instead of failing requests, and the PayOS webhook
/// only has a generous per-IP guard. The shared counter is an in-memory stand-in for Redis; the last
/// test runs the real Redis script when SMARTSOLAR_TEST_REDIS points at a disposable local Redis.
/// </summary>
public sealed class DistributedRateLimitingTests
{
    private static AuthApiFactory Replica(IDistributedRateLimitCounter counter, int chatLimit, Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?> { ["RateLimiting:RagChat:PermitLimit"] = chatLimit.ToString() };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), settings, s =>
        {
            s.RemoveAll<IDistributedRateLimitCounter>();
            s.AddSingleton(counter);
        });
    }

    private static Task<HttpResponseMessage> AskAsync(HttpClient client, int i) => client.PostAsJsonAsync("/api/chat/messages", new { message = $"Question {i}" });

    private static async Task AssertEnvelopedTooManyRequestsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.InRange(response.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("isSuccess").GetBoolean());
        Assert.Equal(AuthErrorCodes.TooManyRequests, body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Two_replicas_share_one_limit_per_user()
    {
        var counter = new SharedRateLimitCounter();
        using var first = Replica(counter, chatLimit: 3);
        using var second = Replica(counter, chatLimit: 3);
        var user = Guid.NewGuid();
        var a = CatalogEndpointsTests.ClientFor(first, RoleCodes.Customer, user);
        var b = CatalogEndpointsTests.ClientFor(second, RoleCodes.Customer, user);

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(a, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(b, 2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(a, 3)).StatusCode);
        await AssertEnvelopedTooManyRequestsAsync(await AskAsync(b, 4));
        await AssertEnvelopedTooManyRequestsAsync(await AskAsync(a, 5));
        // Another user is not affected.
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(CatalogEndpointsTests.ClientFor(second, RoleCodes.Customer), 6)).StatusCode);
    }

    /// <summary>
    /// Concurrency is exercised at the limiter level: the HTTP test host shares one in-memory SQLite
    /// connection, which cannot serve many simultaneous requests (unrelated to rate limiting).
    /// </summary>
    [Fact]
    public async Task Concurrent_acquisitions_on_two_replicas_never_exceed_the_cluster_limit()
    {
        var counter = new SharedRateLimitCounter();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        using var replicaA = new SmartSolar.Api.Extensions.DistributedFixedWindowRateLimiter(counter, "rag-chat:user:1", 5, TimeSpan.FromMinutes(1), logger);
        using var replicaB = new SmartSolar.Api.Extensions.DistributedFixedWindowRateLimiter(counter, "rag-chat:user:1", 5, TimeSpan.FromMinutes(1), logger);

        var leases = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () => await (i % 2 == 0 ? replicaA : replicaB).AcquireAsync(1))));

        Assert.Equal(5, leases.Count(l => l.IsAcquired));
        Assert.All(leases.Where(l => !l.IsAcquired), l => Assert.True(l.TryGetMetadata(System.Threading.RateLimiting.MetadataName.RetryAfter, out _)));
        Assert.Equal(100, counter.Calls);
    }

    [Fact]
    public async Task Fallback_limits_hold_per_instance_under_concurrency_while_redis_is_down()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        using var replica = new SmartSolar.Api.Extensions.DistributedFixedWindowRateLimiter(new FailingRateLimitCounter(), "rag-chat:user:1", 5, TimeSpan.FromMinutes(1), logger);

        var leases = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(async () => await replica.AcquireAsync(1))));

        Assert.Equal(5, leases.Count(l => l.IsAcquired));
    }

    [Fact]
    public async Task A_redis_outage_falls_back_to_per_instance_limits_without_failing_requests()
    {
        var counter = new FailingRateLimitCounter();
        using var first = Replica(counter, chatLimit: 2);
        using var second = Replica(counter, chatLimit: 2);
        var user = Guid.NewGuid();
        var a = CatalogEndpointsTests.ClientFor(first, RoleCodes.Customer, user);
        var b = CatalogEndpointsTests.ClientFor(second, RoleCodes.Customer, user);

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(a, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(a, 2)).StatusCode);
        await AssertEnvelopedTooManyRequestsAsync(await AskAsync(a, 3));
        // Each instance enforces the limit on its own while Redis is down.
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(b, 4)).StatusCode);
        Assert.True(counter.Calls > 0);
    }

    [Fact]
    public async Task An_unresponsive_redis_is_abandoned_quickly()
    {
        using var replica = Replica(new HangingRateLimitCounter(), chatLimit: 5);
        var client = CatalogEndpointsTests.ClientFor(replica, RoleCodes.Customer);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(HttpStatusCode.OK, (await AskAsync(client, 1)).StatusCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Per_instance_policies_are_unchanged_when_a_shared_counter_exists()
    {
        var counter = new SharedRateLimitCounter();
        using var first = Replica(counter, chatLimit: 100, new Dictionary<string, string?> { ["RateLimiting:RagHistory:PermitLimit"] = "2" });
        using var second = Replica(counter, chatLimit: 100, new Dictionary<string, string?> { ["RateLimiting:RagHistory:PermitLimit"] = "2" });
        var user = Guid.NewGuid();
        var a = CatalogEndpointsTests.ClientFor(first, RoleCodes.Customer, user);
        var b = CatalogEndpointsTests.ClientFor(second, RoleCodes.Customer, user);

        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/chat/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/chat/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await a.GetAsync("/api/chat/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await b.GetAsync("/api/chat/sessions")).StatusCode);   // history is per instance
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task The_payos_webhook_has_only_a_per_ip_guard()
    {
        using var replica = Replica(new SharedRateLimitCounter(), chatLimit: 1, new Dictionary<string, string?> { ["RateLimiting:PayOsWebhook:PermitLimit"] = "3" });
        var anonymous = replica.CreateClient();
        Task<HttpResponseMessage> Post() => anonymous.PostAsync("/api/payments/payos/webhook",
            new StringContent("{\"code\":\"00\",\"data\":{\"orderCode\":1},\"signature\":\"bad\"}", System.Text.Encoding.UTF8, "application/json"));

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.BadRequest, (await Post()).StatusCode);   // signature still verified
        await AssertEnvelopedTooManyRequestsAsync(await Post());
        // A customer at their chat limit does not affect webhook deliveries from another source.
        var customer = CatalogEndpointsTests.ClientFor(replica, RoleCodes.Customer);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(customer, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await AskAsync(customer, 2)).StatusCode);
    }

    [RedisFact]
    public async Task The_redis_script_counts_atomically_across_concurrent_callers()
    {
        await using var redis = await StackExchange.Redis.ConnectionMultiplexer.ConnectAsync(RedisFactAttribute.ConnectionString!);
        var counter = new RedisRateLimitCounter(redis, $"smartsolar-test:{Guid.NewGuid():N}:");
        var window = TimeSpan.FromSeconds(30);

        var results = await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => counter.IncrementAsync("chat:user:1", window, CancellationToken.None)));

        Assert.Equal(Enumerable.Range(1, 200).Select(i => (long)i), results.Select(r => r.Count).Order());
        Assert.All(results, r => Assert.InRange(r.ResetIn, TimeSpan.Zero, window));
        Assert.Equal(1, (await counter.IncrementAsync("chat:user:2", window, CancellationToken.None)).Count);
    }

    [RedisFact]
    public async Task Two_replicas_backed_by_real_redis_share_one_limit()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Redis:Enabled"] = "true", ["Redis:ConnectionString"] = RedisFactAttribute.ConnectionString, ["Redis:InstanceName"] = $"smartsolar-test-{Guid.NewGuid():N}:",
            ["RateLimiting:RagChat:PermitLimit"] = "3",
        };
        using var first = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), settings);
        using var second = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), settings);
        var user = Guid.NewGuid();
        var clients = new[] { CatalogEndpointsTests.ClientFor(first, RoleCodes.Customer, user), CatalogEndpointsTests.ClientFor(second, RoleCodes.Customer, user) };

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) statuses.Add((await AskAsync(clients[i % 2], i)).StatusCode);

        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
    }
}

/// <summary>Runs only when SMARTSOLAR_TEST_REDIS points at a disposable local Redis (localhost/127.0.0.1).</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("SMARTSOLAR_TEST_REDIS");

    public RedisFactAttribute()
    {
        var value = ConnectionString;
        if (string.IsNullOrWhiteSpace(value)) Skip = "Set SMARTSOLAR_TEST_REDIS to a disposable local Redis (host:port) to run.";
        else if (!(value.StartsWith("127.0.0.1:", StringComparison.Ordinal) || value.StartsWith("localhost:", StringComparison.Ordinal)))
            Skip = "SMARTSOLAR_TEST_REDIS must point at localhost.";
    }
}
