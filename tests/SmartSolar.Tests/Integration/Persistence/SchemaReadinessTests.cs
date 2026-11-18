using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Api.Middlewares;
using SmartSolar.Tests.TestSupport;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.Persistence;

/// <summary>
/// BL-01: a build running on a schema that misses any of its migrations answers 503 SCHEMA_NOT_READY (enveloped) for every
/// request instead of failing query by query; it recovers by itself once the migrations are applied. The probe is a fake here;
/// the PostgreSQL probe itself (EF pending migrations) needs Docker and is not exercised in this class.
/// </summary>
public sealed class SchemaReadinessTests
{
    private sealed class FakeProbe : ISchemaMigrationProbe
    {
        public IReadOnlyList<string>? Pending { get; set; } = ["20261030122441_AutomatedInstallationQuotation"];
        public int Calls { get; private set; }

        public Task<IReadOnlyList<string>?> GetPendingMigrationsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Pending);
        }
    }

    [Fact]
    public async Task A_missing_migration_keeps_the_service_not_ready_until_it_is_applied_and_rechecks_are_throttled()
    {
        var probe = new FakeProbe();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var readiness = new SchemaReadiness(probe, NullLogger<SchemaReadiness>.Instance, clock);

        Assert.False(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.False(await readiness.IsReadyAsync(CancellationToken.None));
        Assert.Equal(1, probe.Calls);                                   // no database hit per request
        Assert.Equal(probe.Pending, readiness.Pending);

        probe.Pending = [];
        clock.Now = clock.Now.Add(SchemaReadiness.RecheckInterval);
        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
        probe.Pending = ["late"];
        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));   // ready is final
        Assert.Equal(2, probe.Calls);
    }

    /// <summary>
    /// Regression (2026-11-18, found by NFR-RL-CONCURRENT): callers that arrive while the first check is still in flight must wait for its answer, not
    /// be told "not ready" (before the fix the check time was stamped before the probe answered, so a cold start with traffic answered 503 to every
    /// concurrent request for 30 s although the schema was current).
    /// </summary>
    [Fact]
    public async Task Concurrent_callers_during_the_first_check_get_its_answer_and_the_probe_runs_once()
    {
        var release = new TaskCompletionSource();
        var probe = new SlowProbe(release.Task);
        var readiness = new SchemaReadiness(probe, NullLogger<SchemaReadiness>.Instance, new FixedTimeProvider(DateTimeOffset.UtcNow));

        var callers = Enumerable.Range(0, 20).Select(_ => readiness.IsReadyAsync(CancellationToken.None)).ToArray();
        await Task.Delay(50);
        release.SetResult();
        var answers = await Task.WhenAll(callers);

        Assert.All(answers, Assert.True);
        Assert.Equal(1, probe.Calls);
    }

    private sealed class SlowProbe(Task release) : ISchemaMigrationProbe
    {
        public int Calls;

        public async Task<IReadOnlyList<string>?> GetPendingMigrationsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            await release;
            return [];
        }
    }

    [Fact]
    public async Task A_provider_without_migrations_is_ready()
    {
        var readiness = new SchemaReadiness(new FakeProbe { Pending = null }, NullLogger<SchemaReadiness>.Instance, TimeProvider.System);

        Assert.True(await readiness.IsReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Every_request_answers_503_schema_not_ready_with_the_pending_migrations()
    {
        var probe = new FakeProbe();
        using var factory = new AuthApiFactory
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<ISchemaMigrationProbe>();
                services.AddSingleton<ISchemaMigrationProbe>(probe);
            },
        };
        var client = factory.CreateClient();

        var anonymous = await client.GetAsync("/api/products");
        await AssertFailureAsync(anonymous, HttpStatusCode.ServiceUnavailable, SchemaReadinessMiddleware.ErrorCode);
        var body = await BodyOf(await client.GetAsync("/api/administrative-units/communes"));
        Assert.Equal("20261030122441_AutomatedInstallationQuotation",
            body.GetProperty("error").GetProperty("details").GetProperty("pendingMigrations")[0].GetString());
    }

    [Fact]
    public async Task Liveness_stays_up_while_readiness_and_the_payment_webhook_report_the_missing_schema_until_it_is_applied()
    {
        var probe = new FakeProbe();
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        using var factory = new AuthApiFactory
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<ISchemaMigrationProbe>();
                services.AddSingleton<ISchemaMigrationProbe>(probe);
                services.RemoveAll<SchemaReadiness>();
                services.AddSingleton(sp => new SchemaReadiness(probe, NullLogger<SchemaReadiness>.Instance, clock));
            },
        };
        var client = factory.CreateClient();

        // The process is alive (no restart loop), but not ready: no traffic and no payment is applied to an outdated schema.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(SchemaReadinessMiddleware.LivePath)).StatusCode);
        await AssertFailureAsync(await client.GetAsync(SchemaReadinessMiddleware.ReadyPath), HttpStatusCode.ServiceUnavailable, SchemaReadinessMiddleware.ErrorCode);
        await AssertFailureAsync(await client.PostAsync("/api/payments/payos/webhook", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")),
            HttpStatusCode.ServiceUnavailable, SchemaReadinessMiddleware.ErrorCode);

        // Once the migrations are applied (next re-check), readiness reports ready.
        probe.Pending = [];
        clock.Now = clock.Now.Add(SchemaReadiness.RecheckInterval);
        var ready = await client.GetAsync(SchemaReadinessMiddleware.ReadyPath);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", (await BodyOf(ready)).GetProperty("status").GetString());
    }
}
