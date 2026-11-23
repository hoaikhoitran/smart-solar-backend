using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Persistence.KnowledgeBase;
using SmartSolar.Modules.Catalog.Enums;
using SmartSolar.Modules.KnowledgeBase;
using SmartSolar.Modules.KnowledgeBase.Advisor;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Regional;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.KnowledgeBase;

/// <summary>
/// The configuration advisor: every figure comes from a stated deterministic rule over verified catalog data, the customer's own site /
/// simulation or the 2025 HCMC regional PVGIS baseline (the same model as /simulations, never a single point); missing essentials are asked
/// for, never invented; the model is never called for it. Catalog and provider values are SYNTHETIC.
/// </summary>
public sealed class SolarAdvisorTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly DateTimeOffset Updated = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    // 550 W, 1134 × 2278 mm (2.583252 m²), 3,000,000 VND.
    private static readonly AdvisorPanel Panel550 = new(Guid.Parse("00000000-0000-0000-0000-000000000550"), "PNL-550", "Panel 550", "Brand", 550m, 1134m, 2278m, 3_000_000m, 300, Updated);
    private static readonly AdvisorInverter Inv5k = new(Guid.Parse("00000000-0000-0000-0000-000000005000"), "INV-5K", "Inverter 5 kW", "Brand", 15_000_000m, 5000m, 7500m, "SINGLE_PHASE", Updated);
    private static readonly AdvisorInverter Inv10k = new(Guid.Parse("00000000-0000-0000-0000-000000010000"), "INV-10K", "Inverter 10 kW", "Brand", 25_000_000m, 10000m, 15000m, "THREE_PHASE", Updated);
    // Cheapest, but its DC limit is not documented: never called compatible.
    private static readonly AdvisorInverter InvUnknown = new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "INV-X", "Inverter undocumented", "Brand", 1_000_000m, 3000m, null, null, Updated);
    private static readonly AdvisorServiceItem LaborPerKwp = new(Guid.NewGuid(), "LAB-KWP", "Nhân công lắp đặt", "INSTALLATION_LABOR", "KWP", 1_000_000m);
    private static readonly AdvisorServiceItem TransportTrip = new(Guid.NewGuid(), "TRN", "Vận chuyển", "TRANSPORT", "TRIP", 500_000m);

    private sealed class FakeData(AdvisorCatalog catalog, AdvisorSite? site = null, Guid? owner = null) : ISolarAdvisorDataSource
    {
        public int CatalogReads;
        public Task<AdvisorCatalog> GetCatalogAsync(CancellationToken cancellationToken) { CatalogReads++; return Task.FromResult(catalog); }
        public Task<AdvisorSite?> GetOwnSiteAsync(Guid userId, Guid propertySiteId, CancellationToken cancellationToken)
            => Task.FromResult(site is not null && site.PropertySiteId == propertySiteId && userId == owner ? site : null);
    }

    private sealed class FakePvgis(decimal annualPerKwp, bool fail = false) : IPvEnergyEstimator
    {
        public int Calls;
        public PvEnergyRequest? Last;
        public PvEnergySettings Settings => new("PVGIS", "5.3", "crystSi", true);
        public Task<PvEnergyResult> EstimateAsync(PvEnergyRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Last = request;
            if (fail) return Task.FromResult(PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "down"));
            var monthly = Enumerable.Range(1, 12).Select(m => new PvMonthlyEnergy(m, annualPerKwp * request.PeakPowerKwp / 12m, null, null)).ToList();
            return Task.FromResult(PvEnergyResult.Success(new PvEnergyEstimate(annualPerKwp * request.PeakPowerKwp, null, null, null, monthly,
                new PvProviderMetadata("PVGIS", "5.3", "PVGIS-ERA5", null, 2005, 2023, null, request.MountingPlace, "crystSi", true, request.PeakPowerKwp,
                    request.SystemLossPercent, request.TiltDegree, request.CompassAzimuthDegree - 180, DateTimeOffset.UtcNow))));
        }
        public Task<PvHourlyResult> GetHourlySeriesAsync(PvHourlyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static AdvisorCatalog Catalog(params AdvisorInverter[] inverters)
        => new([Panel550], inverters.Length == 0 ? [Inv5k, Inv10k, InvUnknown] : inverters, [LaborPerKwp, TransportTrip], 0);

    /// <summary>PVGIS calls of one regional aggregate: one per verified sample point (26).</summary>
    private static int Samples => RegionalTestSupport.Dataset.Samples.Count;

    private static SolarAdvisorService Service(ISolarAdvisorDataSource data, IPvEnergyEstimator? pvgis = null, SolarYieldCache? cache = null)
        => new(data, pvgis is null ? null : RegionalTestSupport.Provider(pvgis, new FakeClimateContextProvider()), cache ?? new SolarYieldCache(),
            new SolarAdvisorOptions { TaxRatePercent = 8m, SystemLossPercent = 14m }, TimeProvider.System);

    [Fact]
    public async Task Roof_area_gives_the_theoretical_module_footprint_limit_and_its_catalog_cost()
    {
        var view = await Service(new FakeData(Catalog())).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(RoofAreaM2: 50m), None);
        Assert.Equal(SolarAdvisorStatuses.Ready, view.Status);
        // ⌊50 ÷ (1.134 × 2.278)⌋ = ⌊19.355⌋ = 19 panels = 10.45 kWp → needs ≥ 10,450 W DC: only the 10 kW inverter (15,000 W DC) qualifies.
        Assert.Equal(19, view.TheoreticalMaxPanelsByArea);
        var c = Assert.Single(view.Configurations);
        Assert.Equal((19, 10.45m, "AREA"), (c.PanelCount, c.SystemKwp, c.LimitedBy));
        Assert.Equal(Inv10k.ProductId, c.Inverter!.ProductId);
        // 19 × 3,000,000 + 25,000,000 + 10.45 kWp × 1,000,000 = 92,450,000; tax 8% = 7,396,000.
        Assert.Equal((92_450_000m, 7_396_000m, 99_846_000m), (c.Subtotal, c.EstimatedTax, c.EstimatedTotal));
        Assert.DoesNotContain(c.CostLines, l => l.Code == "TRANSPORT");   // per-trip services are not guessed
        Assert.Contains(view.Limitations, l => l.Contains("Vận chuyển"));
        Assert.Contains(view.Assumptions, a => a.Contains("giới hạn trên"));
        Assert.Null(c.AnnualProductionKwh);   // no consumption asked: PVGIS is not needed
        Assert.Contains(view.Sources, s => s.Kind == "CATALOG_PRODUCT" && s.Reference == Panel550.ProductId.ToString());
    }

    [Fact]
    public async Task Consumption_uses_the_regional_PVGIS_baseline_once_and_sizes_kWp_from_the_annual_energy_balance()
    {
        var pvgis = new FakePvgis(1300m);
        var cache = new SolarYieldCache();
        var input = new SolarAdvisorInput(MonthlyConsumptionKwh: 600m, Latitude: 10.7653m, Longitude: 106.6037m, TiltDegree: 15m, AzimuthDegree: 180m);
        var view = await Service(new FakeData(Catalog()), pvgis, cache).AdviseAsync(Guid.NewGuid(), input, None);
        Assert.Equal(SolarAdvisorStatuses.Ready, view.Status);
        Assert.Equal((1300m, "PVGIS_HCMC_REGIONAL"), (view.SpecificYieldKwhPerKwpYear, view.YieldSource));
        Assert.Contains(view.Sources, s => s.Kind == "PVGIS_HCMC_REGIONAL" && s.Detail.Contains(EstimateBases.LabelVi) && s.Detail.Contains($"{Samples}/{Samples}"));
        // Never at the customer's point: every call is a regional sample, none is the given coordinate.
        Assert.DoesNotContain(RegionalTestSupport.Dataset.Samples, s => s.Latitude == 10.7653m && s.Longitude == 106.6037m);
        // 600 × 12 ÷ 1300 = 5.538… → 5.54 kWp → ⌈5540 ÷ 550⌉ = 11 panels = 6.05 kWp (DC 6,050 W ≤ 7,500 W: the 5 kW inverter).
        Assert.Equal(5.54m, view.RequiredKwpForConsumption);
        var c = view.Configurations[0];
        Assert.Equal((11, 6.05m, "CONSUMPTION", Inv5k.ProductId), (c.PanelCount, c.SystemKwp, c.LimitedBy, c.Inverter!.ProductId));
        Assert.Equal((7865m, 655m, 109m), (c.AnnualProductionKwh!.Value, c.AverageMonthlyProductionKwh!.Value, c.ConsumptionCoveragePercent!.Value));
        Assert.Equal((1m, PvgisMountingPlaces.Building, 14m), (pvgis.Last!.PeakPowerKwp, pvgis.Last.MountingPlace, pvgis.Last.SystemLossPercent));

        Assert.Equal(Samples, pvgis.Calls);

        // Same tilt and azimuth anywhere in the city (another former province): the same regional yield from the cache, no provider call.
        var elsewhere = await Service(new FakeData(Catalog()), pvgis, cache).AdviseAsync(Guid.NewGuid(),
            input with { MonthlyConsumptionKwh = 300m, Latitude = 10.4114m, Longitude = 107.1362m }, None);
        Assert.Equal(1300m, elsewhere.SpecificYieldKwhPerKwpYear);
        Assert.Equal(Samples, pvgis.Calls);

        // Outside the 2025 territory (Hà Nội): no regional estimate is applied, nothing is called.
        var outside = await Service(new FakeData(Catalog()), pvgis).AdviseAsync(Guid.NewGuid(), input with { Latitude = 21.0285m, Longitude = 105.8542m }, None);
        Assert.Null(outside.SpecificYieldKwhPerKwpYear);
        Assert.Contains(outside.Limitations, l => l.Contains("ngoài địa bàn TP.HCM"));
        Assert.Equal(Samples, pvgis.Calls);
    }

    [Fact]
    public async Task Budget_buys_the_largest_configuration_with_a_documented_compatible_inverter_that_fits()
    {
        var view = await Service(new FakeData(Catalog())).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(BudgetVnd: 50_000_000m), None);
        var c = Assert.Single(view.Configurations);
        Assert.Equal("BUDGET", c.LimitedBy);
        Assert.True(c.WithinBudget && c.EstimatedTotal <= 50_000_000m);
        // n panels + 5 kW inverter + 0.55·n M labour, ×1.08 ≤ 50 M  →  n = 9 (9 × 3 M + 15 M + 4.95 M = 46.95 M; × 1.08 = 50,706,000 > 50 M → 8).
        Assert.Equal(8, c.PanelCount);
        Assert.Equal(Inv5k.ProductId, c.Inverter!.ProductId);
        Assert.NotEqual(InvUnknown.ProductId, c.Inverter.ProductId);

        // A budget below the smallest compatible system is reported as such, never "fitted" without an inverter.
        var low = await Service(new FakeData(Catalog())).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(RoofAreaM2: 20m, BudgetVnd: 5_000_000m), None);
        Assert.False(Assert.Single(low.Configurations).WithinBudget);
        Assert.Contains(low.Limitations, l => l.Contains("Ngân sách chưa đủ"));
    }

    [Fact]
    public async Task Missing_essentials_are_asked_for_and_nothing_is_invented_or_fetched()
    {
        var pvgis = new FakePvgis(1300m);
        var data = new FakeData(Catalog());
        var nothing = await Service(data, pvgis).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(), None);
        Assert.Equal(SolarAdvisorStatuses.NeedsInput, nothing.Status);
        Assert.Equal("SIZING_INPUT", Assert.Single(nothing.MissingInputs).Code);
        Assert.Equal(0, data.CatalogReads);

        var noLocation = await Service(data, pvgis).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(MonthlyConsumptionKwh: 600m), None);
        Assert.Equal(SolarAdvisorStatuses.NeedsInput, noLocation.Status);
        Assert.Equal(["LOCATION", "TILT", "AZIMUTH"], noLocation.MissingInputs.Select(m => m.Code));
        Assert.Empty(noLocation.Configurations);
        Assert.Null(noLocation.SpecificYieldKwhPerKwpYear);
        Assert.Equal(0, pvgis.Calls);

        // A provider failure is a limitation, not a guess.
        var down = await Service(data, new FakePvgis(0, fail: true)).AdviseAsync(Guid.NewGuid(),
            new SolarAdvisorInput(MonthlyConsumptionKwh: 600m, Latitude: 10.7m, Longitude: 106.6m, TiltDegree: 10m, AzimuthDegree: 180m), None);
        Assert.Null(down.SpecificYieldKwhPerKwpYear);
        Assert.Contains(down.Limitations, l => l.Contains("REGIONAL_COVERAGE_INSUFFICIENT"));
        Assert.Empty(down.Configurations);
    }

    [Fact]
    public async Task Battery_sizing_and_an_empty_catalog_are_blocked_and_another_customers_site_is_not_read()
    {
        var battery = await Service(new FakeData(Catalog())).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(RoofAreaM2: 30m, WantsBattery: true), None);
        Assert.Contains(battery.Blocked, b => b.Contains("Pin lưu trữ"));
        Assert.Contains(battery.Limitations, l => l.Contains("chịu tải"));

        var empty = await Service(new FakeData(new AdvisorCatalog([], [], [], 0))).AdviseAsync(Guid.NewGuid(), new SolarAdvisorInput(RoofAreaM2: 30m), None);
        Assert.Equal(SolarAdvisorStatuses.Blocked, empty.Status);
        Assert.Empty(empty.Configurations);

        var owner = Guid.NewGuid();
        var site = new AdvisorSite(Guid.NewGuid(), "Nhà", 10.7m, 106.6m, 12m, 180m, 40m, new AdvisorSimulation(Guid.NewGuid(), 1250m, 5m, 6250m, Updated));
        var pvgis = new FakePvgis(9999m);
        var own = await Service(new FakeData(Catalog(), site, owner), pvgis).AdviseAsync(owner,
            new SolarAdvisorInput(MonthlyConsumptionKwh: 500m, PropertySiteId: site.PropertySiteId), None);
        Assert.Equal((1250m, "SIMULATION", 0), (own.SpecificYieldKwhPerKwpYear, own.YieldSource, pvgis.Calls));   // the selected simulation, no provider call
        Assert.Equal(40m, own.Input.RoofAreaM2);
        var stranger = await Service(new FakeData(Catalog(), site, owner), pvgis).AdviseAsync(Guid.NewGuid(),
            new SolarAdvisorInput(MonthlyConsumptionKwh: 500m, PropertySiteId: site.PropertySiteId), None);
        Assert.Contains(stranger.MissingInputs, m => m.Code == "PROPERTY_SITE");
        Assert.Null(stranger.SpecificYieldKwhPerKwpYear);
    }

    [Theory]
    [InlineData("Nhà tôi có mái 50 m², nên lắp bao nhiêu tấm pin?", 50.0, null, null, true)]
    [InlineData("Ngân sách 50 triệu thì lắp hệ thống nào?", null, 50_000_000.0, null, true)]
    [InlineData("Gia đình dùng 600 kWh/tháng thì nên chọn hệ thống bao nhiêu kWp?", null, null, 600.0, true)]
    [InlineData("Nên chọn loại pin nào, inverter nào?", null, null, null, true)]
    [InlineData("Ngân sách 1,5 tỷ, mái 120,5 m2", 120.5, 1_500_000_000.0, null, true)]
    [InlineData("Bảo hành tấm pin bao lâu?", null, null, null, false)]
    [InlineData("Thanh toán hợp đồng 50 triệu thế nào?", null, 50_000_000.0, null, false)]
    public void Questions_are_recognised_deterministically(string text, double? area, double? budget, double? consumption, bool advisor)
    {
        var input = SolarAdvisorTextExtractor.Extract(text);
        Assert.Equal(area is null ? null : (decimal)area, input.RoofAreaM2);
        Assert.Equal(budget is null ? null : (decimal)budget, input.BudgetVnd);
        Assert.Equal(consumption is null ? null : (decimal)consumption, input.MonthlyConsumptionKwh);
        Assert.Equal(advisor, SolarAdvisorTextExtractor.IsAdvisorQuestion(text, input));
        var direction = SolarAdvisorTextExtractor.Extract("Mái nghiêng 15 độ, hướng đông nam, muốn có pin lưu trữ");
        Assert.Equal((15m, 135m, true), (direction.TiltDegree!.Value, direction.AzimuthDegree!.Value, direction.WantsBattery));
    }

    // ---------------------------------------------------------------- chat integration (real persistence, fake RAG service)

    [Fact]
    public async Task Chat_answers_advisor_questions_without_the_model_asks_follow_ups_and_keeps_other_questions_on_RAG()
    {
        await using var ctx = InstallationWorkflowTestContext.Create();
        var customer = await ctx.Scheduling.Inner.SeedUserAsync("Advisor customer");
        // ACTIVE catalog rows read by the real data source (synthetic): one panel, one documented inverter, one per-kWp service, one INACTIVE panel.
        var panel = PreSurveyTestContext.NewPanelProduct("ADV-PNL-550");
        panel.RatedPowerW = 550m; panel.WidthMm = 1134m; panel.HeightMm = 2278m; panel.UnitPrice = 3_000_000m;
        var hidden = PreSurveyTestContext.NewPanelProduct("ADV-PNL-HIDDEN");
        hidden.RatedPowerW = 700m; hidden.WidthMm = 1000m; hidden.HeightMm = 1000m; hidden.UnitPrice = 1m; hidden.Status = ProductStatus.Inactive;
        var inverter = PreSurveyTestContext.NewPanelProduct("ADV-INV-10K");
        inverter.ProductType = "INVERTER"; inverter.RatedPowerW = null; inverter.UnitPrice = 25_000_000m;
        inverter.Spec = """{"ratedAcPowerW": 10000, "maxDcInputPowerW": 15000, "phase": "THREE_PHASE"}""";
        ctx.Db.Products.AddRange(panel, hidden, inverter);
        await ctx.Db.SaveChangesAsync();
        ctx.Db.ChangeTracker.Clear();

        var rag = new FakeRagServiceClient();
        var options = new RagOptions { Enabled = true, ServiceSharedSecret = new string('x', 32) };
        var pvgis = new FakePvgis(1300m);
        var advisor = new SolarAdvisorService(new SolarAdvisorDataSource(ctx.Db), RegionalTestSupport.Provider(pvgis, new FakeClimateContextProvider()),
            new SolarYieldCache(), new SolarAdvisorOptions(), ctx.Clock);
        ChatService Chat() => new(new KnowledgeUnitOfWork(ctx.Db), rag, options, ctx.Clock, NullLogger<ChatService>.Instance, advisor);

        var first = await Chat().AskAsync(customer.Id, null, "Gia đình dùng 600 kWh/tháng thì nên chọn hệ thống bao nhiêu kWp?", "a-1", None);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(SolarAdvisorStatuses.NeedsInput, first.Value!.Advisor!.Status);
        Assert.Contains("nghiêng", first.Value.Answer.Content);
        Assert.Equal(0, rag.AnswerCalls);

        // The follow-up gives only tilt and direction; the 600 kWh from the earlier turn is reused. Location comes structured.
        var second = await Chat().AskAsync(customer.Id, first.Value.SessionId, "Mái nghiêng 15 độ, hướng nam", "a-2", None,
            advisorInput: new SolarAdvisorInput(Latitude: 10.7653m, Longitude: 106.6037m));
        Assert.True(second.Succeeded, second.Message);
        var advice = second.Value!.Advisor!;
        Assert.Equal((SolarAdvisorStatuses.Ready, 600m), (advice.Status, advice.Input.MonthlyConsumptionKwh!.Value));
        var c = advice.Configurations[0];
        Assert.Equal(("ADV-PNL-550", 11, "ADV-INV-10K"), (c.Panel.Sku, c.PanelCount, c.Inverter!.Sku));
        Assert.DoesNotContain(advice.Configurations, x => x.Panel.Sku == "ADV-PNL-HIDDEN");
        Assert.Contains("6,05 kWp", second.Value.Answer.Content);
        Assert.Contains("không phải báo giá chính thức", second.Value.Answer.Content);
        Assert.Empty(second.Value.Answer.Citations);
        Assert.Equal(0, rag.AnswerCalls);
        Assert.Equal(Samples, pvgis.Calls);
        // Nothing transactional was created from the chat.
        Assert.Equal((0, 0, 0), (await ctx.Db.Quotations.CountAsync(), await ctx.Db.SurveyRequests.CountAsync(), await ctx.Db.TechnicianAppointments.CountAsync()));

        // A knowledge question still goes to the RAG service (and, with nothing customer-visible published, nothing internal is used).
        var warranty = await Chat().AskAsync(customer.Id, first.Value.SessionId, "Bảo hành tấm pin bao lâu?", "a-3", None);
        Assert.True(warranty.Succeeded, warranty.Message);
        Assert.Null(warranty.Value!.Advisor);
        Assert.Equal(ChatService.InsufficientAnswer, warranty.Value.Answer.Content);

        // The stored session replays the advisor answer for its owner only.
        var session = await Chat().GetSessionAsync(customer.Id, first.Value.SessionId, None);
        Assert.Equal(6, session.Value!.Messages.Count);
        Assert.Equal(KnowledgeErrorCodes.SessionNotFound, (await Chat().GetSessionAsync(Guid.NewGuid(), first.Value.SessionId, None)).ErrorCode);
    }
}
