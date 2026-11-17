using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Tests.Integration.Catalog;
using SmartSolar.Tests.TestSupport;
using Xunit.Abstractions;
using static SmartSolar.Tests.TestSupport.FeatureEndpointTestSupport;

namespace SmartSolar.Tests.Integration.KnowledgeBase;

/// <summary>
/// Opt-in latency benchmark (SMARTSOLAR_BENCH=1) of the configuration-advisor chat path through the real API pipeline (JWT, rate-limit
/// middleware, validation, quota reservation, catalog read, deterministic computation, persistence of the exchange). PVGIS is replaced by an
/// instant fake so the number is the API's own cost; the provider's latency is measured separately. No model is called on this path.
/// </summary>
public sealed class SolarAdvisorBenchmarkTests(ITestOutputHelper output)
{
    private sealed class InstantPvgis : IPvEnergyEstimator
    {
        public PvEnergySettings Settings => new("PVGIS", "5.3", "crystSi", true);
        public Task<PvEnergyResult> EstimateAsync(PvEnergyRequest r, CancellationToken c) => Task.FromResult(PvEnergyResult.Success(new PvEnergyEstimate(1300m * r.PeakPowerKwp,
            null, null, null, [], new PvProviderMetadata("PVGIS", "5.3", "ERA5", null, 2005, 2023, null, r.MountingPlace, "crystSi", true, r.PeakPowerKwp, r.SystemLossPercent,
                r.TiltDegree, r.CompassAzimuthDegree - 180, DateTimeOffset.UtcNow))));
        public Task<PvHourlyResult> GetHourlySeriesAsync(PvHourlyRequest r, CancellationToken c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Advisor_chat_answers_through_the_api_well_inside_the_10_second_nfr()
    {
        if (Environment.GetEnvironmentVariable("SMARTSOLAR_BENCH") != "1") return;   // opt-in
        var requests = int.TryParse(Environment.GetEnvironmentVariable("BENCH_REQUESTS"), out var n) ? n : 100;
        using var factory = Enabled(new FakeStorageObjectVerifier(), new FakeRagServiceClient(), new()
        {
            ["RateLimiting:RagChat:PermitLimit"] = "100000", ["Rag:DailyRequestsPerUser"] = "100000", ["Rag:DailyTokensPerUser"] = "100000000",
            ["Rag:GlobalDailyTokens"] = "1000000000", ["Payments:ReconciliationWorkerEnabled"] = "false", ["Installation:ScheduleWorkerEnabled"] = "false",
        }, services =>
        {
            services.RemoveAll<IPvEnergyEstimator>();
            services.AddSingleton<IPvEnergyEstimator, InstantPvgis>();
        });
        _ = factory.Services;
        await using (var db = factory.CreateDbContext())
        {
            for (var i = 0; i < 20; i++)
            {
                var panel = PreSurveyTestContext.NewPanelProduct($"BENCH-PNL-{i}");
                panel.RatedPowerW = 400 + i * 10; panel.WidthMm = 1134; panel.HeightMm = 2000 + i * 10; panel.UnitPrice = 2_500_000 + i * 50_000;
                db.Products.Add(panel);
                var inverter = PreSurveyTestContext.NewPanelProduct($"BENCH-INV-{i}");
                inverter.ProductType = "INVERTER"; inverter.RatedPowerW = null; inverter.UnitPrice = 10_000_000 + i * 1_000_000;
                inverter.Spec = $$"""{"ratedAcPowerW": {{3000 + i * 1000}}, "maxDcInputPowerW": {{4500 + i * 1500}}}""";
                db.Products.Add(inverter);
            }
            await db.SaveChangesAsync();
        }
        string[] questions =
        [
            "Nhà tôi có mái 50 m², nên lắp bao nhiêu tấm pin?", "Ngân sách 50 triệu thì lắp hệ thống nào?",
            "Gia đình dùng 600 kWh/tháng, mái nghiêng 15 độ hướng nam thì nên chọn bao nhiêu kWp?", "Mái 80 m2, ngân sách 120 triệu, dùng 900 kWh/tháng",
        ];
        var client = CatalogEndpointsTests.ClientFor(factory, RoleCodes.Customer, Guid.NewGuid());
        // Warm-up (JIT, first query plans).
        await client.PostAsJsonAsync("/api/chat/messages", new { message = questions[0] });

        var times = new List<double>();
        for (var i = 0; i < requests; i++)
        {
            var watch = Stopwatch.StartNew();
            var response = await client.PostAsJsonAsync("/api/chat/messages", new
            {
                message = questions[i % questions.Length], clientMessageId = $"bench-{i}",
                advisor = new { latitude = 10.7653, longitude = 106.6037 },
            });
            watch.Stop();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        double P(double p) => times[Math.Min(times.Count - 1, (int)Math.Round(p / 100 * (times.Count - 1)))];
        output.WriteLine($"advisor chat via API (SQLite, in-process, 20 panels × 20 inverters): n={times.Count} p50={P(50):0.0}ms p95={P(95):0.0}ms max={times[^1]:0.0}ms");
        Console.WriteLine($"BENCH advisor-chat n={times.Count} p50={P(50):0.0}ms p95={P(95):0.0}ms max={times[^1]:0.0}ms");
        Assert.True(P(95) < 10_000);
    }
}
