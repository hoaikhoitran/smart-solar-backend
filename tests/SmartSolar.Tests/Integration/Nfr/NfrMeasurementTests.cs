using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.ExternalProviders;
using SmartSolar.Infrastructure.Storage;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Files;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Payments;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.Integration.Files;
using SmartSolar.Tests.TestSupport;
using Xunit.Abstractions;

namespace SmartSolar.Tests.Integration.Nfr;

/// <summary>Opt-in NFR measurement: runs only with <c>SMARTSOLAR_NFR=1</c> (it calls public providers or waits several minutes).</summary>
public sealed class NfrFactAttribute : FactAttribute
{
    public NfrFactAttribute(string? alsoRequires = null)
    {
        if (Environment.GetEnvironmentVariable("SMARTSOLAR_NFR") != "1") Skip = "Set SMARTSOLAR_NFR=1 to run NFR measurements.";
        else if (alsoRequires is not null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(alsoRequires))) Skip = $"Set {alsoRequires} to run this measurement.";
    }
}

/// <summary>
/// NFR measurements (2026-11-18) with the approved thresholds of the repository (guides/full-regression-test-report.md §7,
/// guides/full-project-production-audit.md §9): PayOS PAID within 2 minutes, simulation p95 ≤ 25 s over ≥ 30 runs, presigned URLs expiring at the
/// configured TTL (300 s), rate limits holding under concurrent requests. Results are written to the test output (TRX).
/// </summary>
[Collection(WallClockSensitiveCollection.Name)]
public sealed class NfrMeasurementTests(ITestOutputHelper output)
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>
    /// NFR-PAYOS-RECON (always runs, deterministic clock): the webhook is lost; PayOS has the money one second after the customer's last status check.
    /// For several phases of the worker tick relative to that check, the backend's own worker logic (<see cref="PaymentService.ReconcileDueAsync"/>,
    /// default 30 s tick, 60 s re-check age) must confirm the contract PAID within 120 s of the payment. Real PayOS latency is not included.
    /// </summary>
    [Fact]
    public async Task NFR_PAYOS_RECON_A_lost_webhook_is_confirmed_within_two_minutes_for_every_tick_phase()
    {
        var options = new PaymentOptions();
        var worst = TimeSpan.Zero;
        foreach (var phaseSeconds in new[] { 0.5, 1, 15, 29, 29.9 })
        {
            await using var ctx = InstallationWorkflowTestContext.Create();
            var signed = await SimplifiedFlowFixture.SignedAsync(ctx);
            var payments = ctx.Payments();
            var link = (await payments.CreateOrGetAttemptAsync(signed.Cart.CustomerId, signed.Contract.ContractId, None)).Value!;
            ctx.Clock.Now = ctx.Clock.Now.AddSeconds(30);
            await payments.ReconcileAsync(link.PaymentAttemptId, force: true, None);   // the customer's last poll: PayOS says pending
            var paidAt = ctx.Clock.Now.AddSeconds(1);
            ctx.PayOs.Links[link.OrderCode] = ("PAID", (long)link.Amount, "FT-NFR");   // money arrives, webhook never does

            ctx.Clock.Now = paidAt.AddSeconds(phaseSeconds);   // first worker tick after the payment
            var confirmedAt = (DateTimeOffset?)null;
            for (var tick = 0; tick < 20 && confirmedAt is null; tick++)
            {
                await payments.ReconcileDueAsync(None);
                if ((await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == signed.Contract.ContractId)).Status == ContractStatuses.Paid)
                    confirmedAt = ctx.Clock.Now;
                else ctx.Clock.Now = ctx.Clock.Now.AddSeconds(options.ReconciliationIntervalSeconds);
            }
            Assert.NotNull(confirmedAt);
            var delay = confirmedAt.Value - paidAt;
            output.WriteLine($"phase {phaseSeconds,5:0.0} s → PAID {delay.TotalSeconds:0.0} s after the payment");
            if (delay > worst) worst = delay;
        }
        output.WriteLine($"worst case {worst.TotalSeconds:0.0} s (target ≤ 120 s; interval {options.ReconciliationIntervalSeconds} s, re-check age {options.ReconcileAfterSeconds} s)");
        Assert.True(worst <= TimeSpan.FromMinutes(2), $"worst case {worst.TotalSeconds:0.0} s");
    }

    /// <summary>
    /// NFR-READ-P95 (always runs, in-process TestServer + SQLite, NOT a VPS measurement): the read endpoints touched by the 2026-11-23 changes —
    /// simulation detail (regional metadata), simulation list, the caller's chat sessions and the Admin shared-visibility review — are each read 50
    /// times sequentially; target p95 ≤ 1.5 s. Raw percentiles are written to the test output.
    /// </summary>
    [Fact]
    public async Task NFR_READ_P95_Changed_read_endpoints_stay_within_1_5_seconds_at_p95()
    {
        // A latency measurement, not a rate-limit test (those are separate): the 60/min read policies would refuse the 100 reads of one user.
        using var factory = FeatureEndpointTestSupport.Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), new Dictionary<string, string?>
        {
            ["RateLimiting:PortalRead:PermitLimit"] = "500",
            ["RateLimiting:RagHistory:PermitLimit"] = "500",
            ["RateLimiting:RagAdminRead:PermitLimit"] = "500",
        });
        var customer = await factory.SeedCustomerWithSiteAsync("NFR read customer");
        var preSurvey = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(customer.Site.Id));
        var product = await factory.SeedAsync(PreSurveyTestContext.NewPanelProduct());
        var surface = await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurvey.Id}/surface", new
        {
            expectedRevision = 0, surfaceLengthM = 12, surfaceWidthM = 8, surfaceTiltDegree = 15, surfaceAzimuthDegree = 180, obstacles = Array.Empty<object>(),
        });
        Assert.True(surface.IsSuccessStatusCode, await surface.Content.ReadAsStringAsync());
        var created = await customer.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurvey.Id}/simulations",
            new { expectedGeometryVersion = 1, productId = product.Id, mountingType = "FLUSH", installation = new { panelGapMm = 20 } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var simulationId = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("simulationId").GetGuid();
        var admin = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Admin);

        var reads = new (string Name, HttpClient Client, string Url)[]
        {
            ("simulation detail", customer.Client, $"/api/pre-surveys/{preSurvey.Id}/simulations/{simulationId}"),
            ("simulation list", customer.Client, $"/api/pre-surveys/{preSurvey.Id}/simulations"),
            ("chat sessions", customer.Client, "/api/chat/sessions"),
            ("shared-visibility review", admin, "/api/admin/knowledge-documents/shared-visibility-review"),
        };
        foreach (var (name, client, url) in reads)
        {
            var durations = new List<double>();
            for (var i = 0; i < 50; i++)
            {
                var watch = Stopwatch.StartNew();
                var response = await client.GetAsync(url);
                watch.Stop();
                Assert.True(response.IsSuccessStatusCode, $"{name}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
                durations.Add(watch.Elapsed.TotalMilliseconds);
            }
            durations.Sort();
            double P(double q) => durations[(int)Math.Ceiling(q * durations.Count) - 1];
            output.WriteLine($"{name}: n=50 p50 {P(0.5):0.0} ms p95 {P(0.95):0.0} ms max {durations[^1]:0.0} ms");
            Assert.True(P(0.95) <= 1500, $"{name} p95 {P(0.95):0.0} ms");
        }
    }

    /// <summary>
    /// NFR-RL-CONCURRENT (always runs): 100 concurrent anonymous requests from one client IP against a 20/min policy (catalog-read, IP partition) → exactly
    /// 20 admitted and 80 rejected with the enveloped 429 + Retry-After. Anonymous so the limiter decides before any database access (the test host's single
    /// in-memory SQLite connection cannot serve 100 concurrent authenticated token checks); per-user partitions are covered by EndpointRateLimitCoverageTests.
    /// </summary>
    [Fact]
    public async Task NFR_RL_CONCURRENT_A_burst_of_concurrent_requests_admits_exactly_the_limit()
    {
        using var factory = new AuthApiFactory(new Dictionary<string, string?> { ["RateLimiting:CatalogRead:PermitLimit"] = "20" });
        var client = factory.CreateClient();

        var responses = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => client.GetAsync("/api/products?page=1&pageSize=5")).ToArray());

        output.WriteLine(string.Join(", ", responses.GroupBy(r => (int)r.StatusCode).OrderBy(g => g.Key).Select(g => $"{g.Key}: {g.Count()}")));
        Assert.Equal(80, responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests));
        Assert.Equal(20, responses.Count(r => r.StatusCode != HttpStatusCode.TooManyRequests));
        Assert.All(responses.Where(r => r.StatusCode == HttpStatusCode.TooManyRequests), r => Assert.NotNull(r.Headers.RetryAfter));
    }

    /// <summary>
    /// NFR-SIM-P95 (opt-in, real PVGIS 5.3 + NASA POWER over the internet): 30 simulations through the real API pipeline (JWT, validation, persistence),
    /// each with a different tilt so no PVGIS answer is reused. Target p95 ≤ 25 s.
    /// </summary>
    [NfrFact]
    public async Task NFR_SIM_P95_Thirty_simulations_with_the_real_providers_stay_within_25_seconds_at_p95()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SmartSolar.Api", "appsettings.json"))).Build();
        using var factory = new AuthApiFactory
        {
            ConfigureTestServices = services =>
            {
                services.RemoveAll<IPvEnergyEstimator>();
                services.RemoveAll<IClimateContextProvider>();
                ExternalProvidersRegistration.AddPvgisClient(services, configuration);
                ExternalProvidersRegistration.AddNasaPowerClient(services, configuration);
            },
        };
        var product = await factory.SeedAsync(PreSurveyTestContext.NewPanelProduct());
        var durations = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            var customer = await factory.SeedCustomerWithSiteAsync($"NFR customer {i}");
            var preSurvey = await factory.SeedAsync(PreSurveyTestContext.NewPreSurvey(customer.Site.Id));
            var surface = await customer.Client.PutAsJsonAsync($"/api/pre-surveys/{preSurvey.Id}/surface", new
            {
                expectedRevision = 0, surfaceLengthM = 12, surfaceWidthM = 8, surfaceTiltDegree = 5 + i, surfaceAzimuthDegree = 180, obstacles = Array.Empty<object>(),
            });
            Assert.True(surface.IsSuccessStatusCode, await surface.Content.ReadAsStringAsync());
            var watch = Stopwatch.StartNew();
            var created = await customer.Client.PostAsJsonAsync($"/api/pre-surveys/{preSurvey.Id}/simulations",
                new { expectedGeometryVersion = 1, productId = product.Id, mountingType = "FLUSH", installation = new { panelGapMm = 20 } });
            watch.Stop();
            Assert.True(created.StatusCode == HttpStatusCode.Created, $"run {i}: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");
            durations.Add(watch.Elapsed.TotalSeconds);
            // Since 2026-11-23 each run is a cold 2025 HCMC regional baseline: 26 samples × 2 mounting places of real PVGIS calls (new tilt each run).
            var body = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("energy");
            var regional = body.TryGetProperty("regional", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Object ? r : default;
            output.WriteLine($"run {i}: {watch.Elapsed.TotalSeconds:0.00} s energy {body.GetProperty("status").GetString()} {body.GetProperty("reason")}"
                + (regional.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? $" quality {regional.GetProperty("quality").GetString()} samples {regional.GetProperty("succeededSampleCount").GetInt32()}/{regional.GetProperty("sampleCount").GetInt32()} specific {body.GetProperty("specificYieldKwhPerKwpYear")}"
                    : ""));
        }
        // Evidence that the real providers answered: the factory's synthetic providers were never called.
        Assert.Empty(factory.PvEnergy.Requests);
        Assert.Equal(0, factory.Climate.Calls);
        durations.Sort();
        double P(double q) => durations[(int)Math.Ceiling(q * durations.Count) - 1];
        output.WriteLine($"n={durations.Count} min {durations[0]:0.00} s p50 {P(0.5):0.00} s p95 {P(0.95):0.00} s max {durations[^1]:0.00} s");
        Assert.True(P(0.95) <= 25, $"p95 {P(0.95):0.00} s");
    }

    /// <summary>
    /// NFR-SIGNED-URL (opt-in, real S3-compatible storage, ~5.5 min): a read URL signed with the production TTL (300 s) works before expiry and is refused
    /// with 403 once the storage server's clock passes X-Amz-Date + 300 s.
    /// </summary>
    [NfrFact(S3FactAttribute.Variable)]
    public async Task NFR_SIGNED_URL_A_read_url_signed_for_300_seconds_expires_at_the_storage_server()
    {
        var parts = Environment.GetEnvironmentVariable(S3FactAttribute.Variable)!.Split('|');
        var endpoint = parts[0].Trim().TrimEnd('/');
        const string bucket = "smartsolar-nfr-signed-url";
        var options = new StorageOptions
        {
            DefaultBucket = bucket,
            S3 = new S3VerificationOptions
            {
                Enabled = true, ServiceUrl = endpoint, ForcePathStyle = true, Region = "us-east-1", AccessKeyId = parts[1].Trim(), SecretAccessKey = parts[2].Trim(),
                PresignedUrlTtlSeconds = 300, VerifyObjectBeforeSigning = false,
            },
        };
        using var s3 = new AmazonS3Client(new BasicAWSCredentials(parts[1].Trim(), parts[2].Trim()),
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        try { await s3.PutBucketAsync(bucket); } catch (AmazonS3Exception) { }
        await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = "nfr/object.jpg", InputStream = new MemoryStream(FakeStorageObjectVerifier.JpegBytes) });
        using var signer = new S3StorageUrlSigner(s3, options, TimeProvider.System, NullLogger<S3StorageUrlSigner>.Instance);

        var signed = await signer.CreateReadUrlAsync(new StorageObjectLocation(bucket, "nfr/object.jpg"),
            new StorageUrlRequest(StorageUrlDisposition.Inline, "object.jpg", FilePolicies.Jpeg, TimeSpan.FromSeconds(300)), None);
        Assert.Equal(StorageUrlStatus.Signed, signed.Status);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(signed.Url!).Query);
        // The SDK computes X-Amz-Expires against its clock-skew-corrected time (learned from the storage server) and rounds to the second: 300 ± 1.
        var lifetime = int.Parse(query["X-Amz-Expires"]!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(lifetime, 299, 301);
        var signedAt = DateTimeOffset.ParseExact(query["X-Amz-Date"]!, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal);
        using var http = new HttpClient();
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(signed.Url)).StatusCode);

        // Still valid shortly before expiry (server clock), refused after it.
        while ((await http.GetAsync($"{endpoint}/minio/health/live")).Headers.Date is { } now && now < signedAt.AddSeconds(lifetime - 10)) await Task.Delay(1000);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(signed.Url)).StatusCode);
        var giveUp = DateTimeOffset.UtcNow.AddSeconds(60);
        while ((await http.GetAsync($"{endpoint}/minio/health/live")).Headers.Date is { } now && now <= signedAt.AddSeconds(lifetime + 1))
        {
            Assert.True(DateTimeOffset.UtcNow < giveUp, "storage clock did not pass the expiry");
            await Task.Delay(500);
        }
        var late = await http.GetAsync(signed.Url);
        output.WriteLine($"signed {signedAt:O}, X-Amz-Expires {lifetime} s (configured 300): before expiry 200, after expiry {(int)late.StatusCode}");
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
    }
}
