using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Regional;

namespace SmartSolar.Modules.KnowledgeBase.Advisor;

/// <summary>
/// What the customer told the advisor. Every value is optional; nothing missing is ever invented. <see cref="PropertySiteId"/> must be the
/// caller's own site (its location, latest pre-survey tilt / azimuth / usable area and selected simulation are then used).
/// </summary>
public sealed record SolarAdvisorInput(
    decimal? RoofAreaM2 = null,
    decimal? BudgetVnd = null,
    decimal? MonthlyConsumptionKwh = null,
    Guid? PropertySiteId = null,
    decimal? Latitude = null,
    decimal? Longitude = null,
    decimal? TiltDegree = null,
    decimal? AzimuthDegree = null,
    bool WantsBattery = false)
{
    public bool HasSizingInput => RoofAreaM2 is not null || BudgetVnd is not null || MonthlyConsumptionKwh is not null;

    /// <summary>This input's values over <paramref name="earlier"/>'s (later turns of a conversation override earlier ones).</summary>
    public SolarAdvisorInput Over(SolarAdvisorInput? earlier) => earlier is null ? this : new(
        RoofAreaM2 ?? earlier.RoofAreaM2, BudgetVnd ?? earlier.BudgetVnd, MonthlyConsumptionKwh ?? earlier.MonthlyConsumptionKwh,
        PropertySiteId ?? earlier.PropertySiteId, Latitude ?? earlier.Latitude, Longitude ?? earlier.Longitude, TiltDegree ?? earlier.TiltDegree,
        AzimuthDegree ?? earlier.AzimuthDegree, WantsBattery || earlier.WantsBattery);
}

// ---------------------------------------------------------------- read-only data the advisor may use

/// <summary>An ACTIVE, priced (VND) SOLAR_PANEL with a rated power and module dimensions in the catalog.</summary>
public sealed record AdvisorPanel(Guid ProductId, string Sku, string Name, string Brand, decimal RatedPowerW, decimal WidthMm, decimal HeightMm, decimal UnitPrice,
    int? WarrantyMonths, DateTimeOffset UpdatedAt);

/// <summary>An ACTIVE, priced (VND) INVERTER; the power limits come from its catalog spec (null = not documented).</summary>
public sealed record AdvisorInverter(Guid ProductId, string Sku, string Name, string Brand, decimal UnitPrice, decimal? RatedAcPowerW, decimal? MaxDcInputPowerW,
    string? Phase, DateTimeOffset UpdatedAt);

/// <summary>An ACTIVE, priced (VND) service (INSTALLATION_LABOR / TRANSPORT / OTHER_SERVICE) and its unit.</summary>
public sealed record AdvisorServiceItem(Guid ProductId, string Sku, string Name, string ProductType, string Unit, decimal UnitPrice);

public sealed record AdvisorCatalog(IReadOnlyList<AdvisorPanel> Panels, IReadOnlyList<AdvisorInverter> Inverters, IReadOnlyList<AdvisorServiceItem> Services,
    int BatteryProductCount);

/// <summary>
/// The customer's own site: location (official province code and coordinates), the latest pre-survey surface values and its selected
/// (non-stale, regional-model) simulation.
/// </summary>
public sealed record AdvisorSite(Guid PropertySiteId, string Name, decimal? Latitude, decimal? Longitude, decimal? TiltDegree, decimal? AzimuthDegree,
    decimal? UsableAreaM2, AdvisorSimulation? Simulation, string? ProvinceCode = null);

public sealed record AdvisorSimulation(Guid SimulationId, decimal SpecificYieldKwhPerKwpYear, decimal InstalledCapacityKwp, decimal? AnnualEnergyKwh, DateTimeOffset CreatedAt);

/// <summary>Read-only access (no write method exists): ACTIVE catalog products and the caller's own site.</summary>
public interface ISolarAdvisorDataSource
{
    Task<AdvisorCatalog> GetCatalogAsync(CancellationToken cancellationToken);

    /// <summary>The site when it belongs to <paramref name="userId"/>'s customer profile; otherwise null.</summary>
    Task<AdvisorSite?> GetOwnSiteAsync(Guid userId, Guid propertySiteId, CancellationToken cancellationToken);
}

// ---------------------------------------------------------------- result

public static class SolarAdvisorStatuses
{
    /// <summary>At least one configuration was computed and every asked goal could be evaluated.</summary>
    public const string Ready = "READY";
    /// <summary>Essential data is missing: <see cref="SolarAdvisorView.MissingInputs"/> lists the questions to ask.</summary>
    public const string NeedsInput = "NEEDS_INPUT";
    /// <summary>The verified data needed for a safe answer does not exist (e.g. no priced panel in the catalog).</summary>
    public const string Blocked = "BLOCKED";
}

public sealed record AdvisorMissingInput(string Code, string Question);

public sealed record AdvisorProductRef(Guid ProductId, string Sku, string Name, string Brand, decimal UnitPrice);

public sealed record AdvisorCostLine(string Code, string Description, decimal Quantity, string Unit, decimal UnitPrice, decimal Amount, Guid? ProductId);

/// <summary>
/// One reference configuration. <see cref="LimitedBy"/>: CONSUMPTION (sized to the stated use), AREA (the theoretical module-footprint
/// limit), BUDGET (the most that fits the budget). Amounts are VND from current catalog prices; NOT a quotation.
/// </summary>
public sealed record AdvisorConfiguration(
    AdvisorProductRef Panel, decimal PanelRatedPowerW, int PanelCount, decimal SystemKwp, decimal ModuleFootprintM2,
    AdvisorProductRef? Inverter, decimal? InverterRatedAcPowerW, decimal? InverterMaxDcInputPowerW, string? InverterNote,
    IReadOnlyList<AdvisorCostLine> CostLines, decimal Subtotal, decimal TaxRatePercent, decimal EstimatedTax, decimal EstimatedTotal,
    decimal? AnnualProductionKwh, decimal? AverageMonthlyProductionKwh, decimal? ConsumptionCoveragePercent, string LimitedBy, bool WithinBudget);

public sealed record AdvisorSource(string Kind, string Reference, string Detail);

public sealed record SolarAdvisorView(
    string Status,
    SolarAdvisorInput Input,
    IReadOnlyList<AdvisorMissingInput> MissingInputs,
    IReadOnlyList<AdvisorConfiguration> Configurations,
    decimal? SpecificYieldKwhPerKwpYear,
    string? YieldSource,
    decimal? RequiredKwpForConsumption,
    int? TheoreticalMaxPanelsByArea,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Blocked,
    IReadOnlyList<AdvisorSource> Sources,
    string Disclaimer,
    long ElapsedMs,
    long ProviderMs);

public sealed class SolarAdvisorOptions
{
    /// <summary>Tax rate applied to estimates; the same configured default as quotations (<c>Quotation:DefaultTaxRatePercent</c>).</summary>
    public decimal TaxRatePercent { get; set; } = 8m;

    /// <summary>PVGIS system losses (the simulation default, <c>SolarSimulation:DefaultSystemLossPercent</c>).</summary>
    public decimal SystemLossPercent { get; set; } = 14m;

    /// <summary>Hard cap for any computed panel count (bounds the search).</summary>
    public int MaxPanels { get; set; } = 2000;

    public int MaxConfigurations { get; set; } = 3;

    /// <summary>
    /// Seconds the regional yield lookup may take inside a chat request. A slower first (uncached) computation keeps running on its own
    /// budget and is cached for the next question; this request then reports the yield as unavailable (never invented).
    /// </summary>
    public int ProviderTimeoutSeconds { get; set; } = 8;

    /// <summary>How long a regional specific yield for the same tilt / azimuth / losses is reused in process.</summary>
    public int YieldCacheMinutes { get; set; } = 1440;
}

/// <summary>Process-wide cache of regional specific yields (tilt / azimuth rounded to 1°, losses). Bounded.</summary>
public sealed class SolarYieldCache
{
    private readonly ConcurrentDictionary<string, (decimal Yield, string Detail, DateTimeOffset Expires)> _entries = new();

    public bool TryGet(string key, DateTimeOffset now, out (decimal Yield, string Detail) value)
    {
        if (_entries.TryGetValue(key, out var e) && e.Expires > now) { value = (e.Yield, e.Detail); return true; }
        value = default;
        return false;
    }

    public void Set(string key, decimal yield, string detail, DateTimeOffset expires)
    {
        if (_entries.Count > 2000) _entries.Clear();
        _entries[key] = (yield, detail, expires);
    }
}

/// <summary>
/// Deterministic solar configuration advisor. Every number comes from a stated rule applied to verified data; the language model never
/// computes prices or specifications. Rules:
/// <list type="bullet">
/// <item>Module footprint = catalog width × height. Theoretical maximum panels by area = ⌊area ÷ footprint⌋ — an UPPER bound without row gaps,
/// setbacks, walkways or obstacles; the real count needs the layout simulation or the on-site survey.</item>
/// <item>Specific yield (kWh/kWp/year): the site's selected, non-stale simulation on the regional model; otherwise the 2025 Ho Chi Minh City
/// regional baseline (area-weighted PVGIS over the verified territory, <see cref="IRegionalSolarBaselineProvider"/> — the same model as
/// /simulations) for 1 kWp at the given tilt and compass azimuth with the configured system losses and the conservative "building" (flush)
/// mounting. The location only decides whether the site is in the supported territory; no default location, tilt or azimuth is assumed.</item>
/// <item>kWp to cover the stated use = monthly kWh × 12 ÷ specific yield (annual energy balance; self-consumption, export and tariffs are not
/// assessed). Panels = ⌈kWp × 1000 ÷ rated W⌉.</item>
/// <item>Inverter: the cheapest ACTIVE inverter whose documented maximum DC input power ≥ the array's rated DC power. Inverters without that
/// documented value are never called compatible. String voltage / MPPT design is not verified (panel Voc is not in the catalog).</item>
/// <item>Cost = panels + inverter + services priced per KWP × kWp (other service units depend on the survey and are listed as not included)
/// + the configured tax rate. Budget: the largest panel count whose estimated total fits.</item>
/// <item>Battery storage: BLOCKED (no battery capacity specification in the catalog and no approved backup-sizing rule).</item>
/// </list>
/// </summary>
public sealed class SolarAdvisorService(ISolarAdvisorDataSource data, IRegionalSolarBaselineProvider? regional, SolarYieldCache cache, SolarAdvisorOptions options, TimeProvider time)
{
    public const string Disclaimer =
        "Đây là cấu hình và chi phí tham khảo tính từ danh mục sản phẩm hiện hành, không phải báo giá chính thức. Báo giá, hợp đồng và lịch hẹn chỉ được tạo khi bạn chủ động gửi yêu cầu; "
        + "số tấm thực tế và khả năng chịu tải của mái cần khảo sát tại chỗ.";

    public async Task<SolarAdvisorView> AdviseAsync(Guid userId, SolarAdvisorInput input, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long providerMs = 0;
        var missing = new List<AdvisorMissingInput>();
        var assumptions = new List<string>();
        var limitations = new List<string>
        {
            "Không đánh giá kết cấu và khả năng chịu tải của mái (cần khảo sát kết cấu).",
            "Không thiết kế chuỗi pin / điện áp DC và MPPT (danh mục chưa có Voc/Isc của tấm pin).",
        };
        var blocked = new List<string>();
        var sources = new List<AdvisorSource>();

        AdvisorSite? site = null;
        if (input.PropertySiteId is { } siteId)
        {
            site = await data.GetOwnSiteAsync(userId, siteId, cancellationToken);
            if (site is null) missing.Add(new("PROPERTY_SITE", "Không tìm thấy địa điểm này trong tài khoản của bạn; vui lòng chọn lại địa điểm."));
            else sources.Add(new("PROPERTY_SITE", site.PropertySiteId.ToString(), $"Địa điểm \"{site.Name}\" của bạn"));
        }
        var area = input.RoofAreaM2 ?? site?.UsableAreaM2;
        if (input.RoofAreaM2 is null && site?.UsableAreaM2 is { } siteArea)
            assumptions.Add($"Diện tích khả dụng {Fmt(siteArea)} m² lấy từ thông tin tiền khảo sát của địa điểm.");
        var input2 = input with { RoofAreaM2 = area };

        if (!input2.HasSizingInput)
        {
            missing.Add(new("SIZING_INPUT",
                "Bạn cho biết ít nhất một trong các thông tin: diện tích mái có thể lắp (m²), ngân sách (VND) hoặc điện tiêu thụ trung bình (kWh/tháng)?"));
            return View(SolarAdvisorStatuses.NeedsInput, input2, missing, [], null, null, null, null, assumptions, limitations, blocked, sources, clock, providerMs);
        }

        var catalog = await data.GetCatalogAsync(cancellationToken);
        if (input.WantsBattery)
            blocked.Add(catalog.BatteryProductCount == 0
                ? "Pin lưu trữ (battery): danh mục chưa có sản phẩm pin lưu trữ có thông số dung lượng đã xác minh, và chưa có quy tắc định cỡ dự phòng được phê duyệt."
                : "Pin lưu trữ (battery): danh mục có sản phẩm pin lưu trữ nhưng chưa có thông số dung lượng chuẩn hóa và quy tắc định cỡ dự phòng được phê duyệt, nên không đề xuất dung lượng.");
        if (catalog.Panels.Count == 0)
        {
            blocked.Add("Danh mục chưa có tấm pin ĐANG BÁN có đủ công suất định mức, kích thước và giá (VND).");
            return View(SolarAdvisorStatuses.Blocked, input2, missing, [], null, null, null, null, assumptions, limitations, blocked, sources, clock, providerMs);
        }

        // ---- specific yield
        decimal? yield = null;
        string? yieldSource = null;
        if (site?.Simulation is { } sim)
        {
            yield = sim.SpecificYieldKwhPerKwpYear;
            yieldSource = "SIMULATION";
            sources.Add(new("SIMULATION", sim.SimulationId.ToString(),
                $"Mô phỏng đã chọn ({sim.CreatedAt:yyyy-MM-dd}): {Fmt(sim.InstalledCapacityKwp)} kWp, sản lượng riêng {Fmt(sim.SpecificYieldKwhPerKwpYear)} kWh/kWp/năm"));
        }
        var lat = input.Latitude ?? site?.Latitude;
        var lon = input.Longitude ?? site?.Longitude;
        var tilt = input.TiltDegree ?? site?.TiltDegree;
        var azimuth = input.AzimuthDegree ?? site?.AzimuthDegree;
        var wantsProduction = input2.MonthlyConsumptionKwh is not null;
        if (yield is null && wantsProduction)
        {
            // The location only decides whether the regional baseline applies (official province code, or coordinates in the territory).
            var siteProvince = input.Latitude is null && input.Longitude is null ? site?.ProvinceCode : null;
            var locationKnown = (lat is not null && lon is not null) || siteProvince is not null;
            if (!locationKnown)
                missing.Add(new("LOCATION", "Công trình ở đâu? Hãy chọn địa điểm đã lưu (có tọa độ) hoặc cung cấp vị trí để ước tính sản lượng."));
            if (tilt is null)
                missing.Add(new("TILT", "Mái nghiêng khoảng bao nhiêu độ (0° là mái bằng)?"));
            if (azimuth is null)
                missing.Add(new("AZIMUTH", "Mặt mái hướng về đâu (độ la bàn, ví dụ 180° = hướng Nam)?"));
            // Membership: explicit coordinates in the question, else the site's official province code (or its coordinates).
            var membership = regional?.ResolveMembership(siteProvince, lat, lon);
            if (locationKnown && membership is { Status: RegionMembershipStatus.OutOfRegion })
            {
                limitations.Add("Vị trí nằm ngoài địa bàn TP.HCM (sau sáp nhập) mà SmartSolar phục vụ, nên không áp dụng ước tính sản lượng trung bình khu vực.");
            }
            else if (locationKnown && tilt is { } t && azimuth is { } az)
            {
                if (regional is not { Enabled: true })
                    blocked.Add("Chưa có dữ liệu nền sản lượng khu vực TP.HCM để ước tính sản lượng.");
                else
                {
                    // Keyed by the regional model and dataset too: a new dataset or algorithm never serves a yield of the previous one.
                    var key = string.Create(CultureInfo.InvariantCulture,
                        $"{SmartSolar.Modules.SolarSimulation.Constants.AlgorithmVersions.RegionalBaseline}|{regional.Dataset!.ManifestSha256[..16]}|{Math.Round(t)}|{Math.Round(az)}|{options.SystemLossPercent}");
                    var now = time.GetUtcNow();
                    if (cache.TryGet(key, now, out var hit))
                    {
                        yield = hit.Yield;
                        sources.Add(new("PVGIS", key, hit.Detail + " (bộ nhớ đệm)"));
                    }
                    else
                    {
                        var watch = Stopwatch.StartNew();
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ProviderTimeoutSeconds, 2, 30)));
                        RegionalPvResult result;
                        try
                        {
                            // The same 1° rounding as the cache key, so a cached yield is exactly the one computed for its key.
                            result = await regional.GetPvBaselineAsync(new RegionalPvRequest(options.SystemLossPercent, Math.Round(t), Math.Round(az), PvgisMountingPlaces.Building),
                                timeout.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            result = new RegionalPvResult(null, new ProviderFailure(ProviderFailureCodes.Timeout, "The regional baseline was not ready in time."), null);
                        }
                        providerMs = watch.ElapsedMilliseconds;
                        if (result.Baseline is { AnnualSpecificYieldKwhPerKwp: > 0 } baseline)
                        {
                            yield = Math.Round(baseline.AnnualSpecificYieldKwhPerKwp, 1);
                            var p = baseline.ReferenceRequest;
                            var detail = $"{EstimateBases.LabelVi}: PVGIS {p.ProviderVersion} ({string.Join(", ", baseline.RadiationDatabases)}, {baseline.ProviderYearMin}-{baseline.ProviderYearMax}), "
                                         + $"trung bình có trọng số diện tích của {baseline.Coverage.SucceededSampleCount}/{baseline.Coverage.SampleCount} điểm mẫu ({regional.Dataset!.DatasetKey}), "
                                         + $"1 kWp, nghiêng {Fmt(t)}°, hướng {Fmt(az)}°, tổn hao {Fmt(options.SystemLossPercent)}%, lắp sát mái";
                            // A DEGRADED (partially covered) baseline is used for this answer but never kept.
                            if (baseline.Coverage.Quality == RegionalQualities.Complete)
                                cache.Set(key, yield.Value, detail, now.AddMinutes(Math.Max(1, options.YieldCacheMinutes)));
                            else
                                limitations.Add($"Ước tính khu vực chỉ dùng {baseline.Coverage.SucceededSampleCount}/{baseline.Coverage.SampleCount} điểm mẫu (thiếu dữ liệu nhà cung cấp).");
                            sources.Add(new("PVGIS_HCMC_REGIONAL", key, detail));
                        }
                        else
                        {
                            limitations.Add($"Không ước tính được sản lượng khu vực: {result.Failure?.Code ?? "không có kết quả"}. Bạn có thể thử lại sau ít phút hoặc chạy mô phỏng cho địa điểm.");
                        }
                    }
                    if (yield is not null)
                    {
                        yieldSource = "PVGIS_HCMC_REGIONAL";
                        assumptions.Add($"Sản lượng riêng theo {EstimateBases.LabelVi} (năm khí tượng điển hình, PVGIS) với cách lắp sát mái — ước tính thận trọng; "
                                        + "không phải dự báo chính xác tại mái nhà; chưa tính bóng che cục bộ.");
                    }
                }
            }
        }

        decimal? requiredKwp = null;
        if (input2.MonthlyConsumptionKwh is { } monthly && yield is { } y && y > 0)
        {
            requiredKwp = Math.Round(monthly * 12m / y, 2);
            assumptions.Add($"Công suất để bù 100% điện năng tiêu thụ trung bình năm = {Fmt(monthly)} kWh/tháng × 12 ÷ {Fmt(y)} kWh/kWp/năm = {Fmt(requiredKwp.Value)} kWp "
                            + "(cân bằng năng lượng năm; chưa xét tự dùng, bán điện dư hay biểu giá).");
        }
        if (area is not null) assumptions.Add("Số tấm tối đa theo diện tích = diện tích ÷ diện tích một tấm (chiều rộng × chiều dài theo danh mục), CHƯA trừ khe hở, khoảng lùi mép, lối đi và vật cản — đây là giới hạn trên.");
        if (input2.BudgetVnd is not null) assumptions.Add("Ngân sách so với tổng ước tính gồm tấm pin, inverter, dịch vụ tính theo kWp và thuế theo thuế suất cấu hình.");
        assumptions.Add($"Thuế ước tính theo thuế suất cấu hình {Fmt(options.TaxRatePercent)}% (không có giảm giá).");
        var perJobServices = catalog.Services.Where(s => !IsPerKwp(s.Unit)).ToList();
        if (perJobServices.Count > 0)
            limitations.Add("Chưa gồm các dịch vụ tính theo công việc / chuyến (" + string.Join(", ", perJobServices.Select(s => s.Name).Distinct().Take(5))
                            + "): số lượng do kỹ thuật viên xác định sau khảo sát.");

        // ---- configurations
        var configs = new List<AdvisorConfiguration>();
        int? maxByAreaBest = null;
        foreach (var panel in catalog.Panels)
        {
            var footprint = panel.WidthMm * panel.HeightMm / 1_000_000m;
            int? nArea = area is { } a ? (int)Math.Floor(a / footprint) : null;
            if (nArea is { } na) maxByAreaBest = Math.Max(maxByAreaBest ?? 0, na);
            int? nCons = requiredKwp is { } kwp ? (int)Math.Ceiling(kwp * 1000m / panel.RatedPowerW) : null;

            int n;
            string limitedBy;
            if (nCons is { } c) { n = c; limitedBy = "CONSUMPTION"; }
            else if (nArea is { } ar) { n = ar; limitedBy = "AREA"; }
            else if (input2.BudgetVnd is not null && !wantsProduction) { n = options.MaxPanels; limitedBy = "BUDGET"; }
            else continue;
            if (nArea is { } cap && n > cap) { n = cap; limitedBy = "AREA"; }
            n = Math.Min(n, options.MaxPanels);

            var withinBudget = true;
            if (input2.BudgetVnd is { } budget)
            {
                // The largest count up to n whose estimate (with a verified-compatible inverter) fits; none fits → keep n and flag it.
                var fit = n;
                while (fit >= 1 && (Cost(panel, fit, catalog) is var trial && (trial.Inverter is null || trial.Total > budget))) fit--;
                if (fit >= 1 && fit < n) { n = fit; limitedBy = "BUDGET"; }
                else if (fit < 1) withinBudget = false;
            }
            if (n < 1) continue;
            var cost = Cost(panel, n, catalog);
            var systemKwp = Math.Round(n * panel.RatedPowerW / 1000m, 2);
            decimal? annual = yield is { } yy ? Math.Round(systemKwp * yy, 0) : null;
            decimal? coverage = annual is { } an && input2.MonthlyConsumptionKwh is { } m && m > 0 ? Math.Round(an / (m * 12m) * 100m, 0) : null;
            configs.Add(new AdvisorConfiguration(
                new AdvisorProductRef(panel.ProductId, panel.Sku, panel.Name, panel.Brand, panel.UnitPrice), panel.RatedPowerW, n, systemKwp,
                Math.Round(n * footprint, 1),
                cost.Inverter is { } inv ? new AdvisorProductRef(inv.ProductId, inv.Sku, inv.Name, inv.Brand, inv.UnitPrice) : null,
                cost.Inverter?.RatedAcPowerW, cost.Inverter?.MaxDcInputPowerW,
                cost.Inverter is null ? "Không có inverter ĐANG BÁN nào có công suất DC vào tối đa (theo thông số) đủ cho dàn pin này." : null,
                cost.Lines, cost.Subtotal, options.TaxRatePercent, cost.Tax, cost.Total, annual, annual is { } yr ? Math.Round(yr / 12m, 0) : null, coverage,
                limitedBy, input2.BudgetVnd is null || (withinBudget && cost.Inverter is not null && cost.Total <= input2.BudgetVnd)));
            sources.Add(new("CATALOG_PRODUCT", panel.ProductId.ToString(), $"{panel.Sku}: {Fmt(panel.RatedPowerW)} W, {Fmt(panel.WidthMm)}×{Fmt(panel.HeightMm)} mm, {Money(panel.UnitPrice)} (cập nhật {panel.UpdatedAt:yyyy-MM-dd})"));
            if (cost.Inverter is { } used && sources.All(s => s.Reference != used.ProductId.ToString()))
                sources.Add(new("CATALOG_PRODUCT", used.ProductId.ToString(), $"{used.Sku}: AC {Fmt(used.RatedAcPowerW ?? 0)} W, DC tối đa {Fmt(used.MaxDcInputPowerW ?? 0)} W, {Money(used.UnitPrice)} (cập nhật {used.UpdatedAt:yyyy-MM-dd})"));
        }

        var ranked = (requiredKwp is not null
                ? configs.OrderByDescending(c => c.WithinBudget).ThenByDescending(c => c.ConsumptionCoveragePercent >= 100).ThenBy(c => c.Inverter is null).ThenBy(c => c.EstimatedTotal)
                : configs.OrderByDescending(c => c.WithinBudget).ThenBy(c => c.Inverter is null).ThenByDescending(c => c.SystemKwp).ThenBy(c => c.EstimatedTotal))
            .Take(Math.Max(1, options.MaxConfigurations)).ToList();
        var keep = ranked.SelectMany(c => new[] { c.Panel.ProductId, c.Inverter?.ProductId ?? Guid.Empty }).ToHashSet();
        sources.RemoveAll(s => s.Kind == "CATALOG_PRODUCT" && !keep.Contains(Guid.Parse(s.Reference)));
        if (input2.BudgetVnd is not null && ranked.Count > 0 && ranked.All(c => !c.WithinBudget))
            limitations.Add("Ngân sách chưa đủ cho cấu hình nhỏ nhất có inverter tương thích trong danh mục.");

        var status = missing.Count > 0 ? SolarAdvisorStatuses.NeedsInput
            : ranked.Count == 0 ? SolarAdvisorStatuses.Blocked
            : SolarAdvisorStatuses.Ready;
        if (ranked.Count == 0 && missing.Count == 0) blocked.Add("Không tính được cấu hình nào từ dữ liệu danh mục hiện có.");
        return View(status, input2, missing, ranked, yield, yieldSource, requiredKwp, maxByAreaBest, assumptions, limitations, blocked, sources, clock, providerMs);
    }

    private (AdvisorInverter? Inverter, IReadOnlyList<AdvisorCostLine> Lines, decimal Subtotal, decimal Tax, decimal Total) Cost(AdvisorPanel panel, int n, AdvisorCatalog catalog)
    {
        var dcW = n * panel.RatedPowerW;
        var kwp = Math.Round(dcW / 1000m, 2);
        var inverter = catalog.Inverters.Where(i => i.MaxDcInputPowerW is { } max && max >= dcW).OrderBy(i => i.UnitPrice).ThenBy(i => i.Sku, StringComparer.Ordinal).FirstOrDefault();
        var lines = new List<AdvisorCostLine> { new("PANEL", panel.Name, n, "PANEL", panel.UnitPrice, n * panel.UnitPrice, panel.ProductId) };
        if (inverter is not null) lines.Add(new("INVERTER", inverter.Name, 1, "UNIT", inverter.UnitPrice, inverter.UnitPrice, inverter.ProductId));
        foreach (var s in catalog.Services.Where(s => IsPerKwp(s.Unit)))
            lines.Add(new(s.ProductType, s.Name, kwp, "KWP", s.UnitPrice, Math.Round(kwp * s.UnitPrice, 0), s.ProductId));
        var subtotal = lines.Sum(l => l.Amount);
        var tax = Math.Round(subtotal * options.TaxRatePercent / 100m, 0);
        return (inverter, lines, subtotal, tax, subtotal + tax);
    }

    private static bool IsPerKwp(string unit) => unit.Trim().Equals("KWP", StringComparison.OrdinalIgnoreCase);

    private SolarAdvisorView View(string status, SolarAdvisorInput input, List<AdvisorMissingInput> missing, IReadOnlyList<AdvisorConfiguration> configs, decimal? yield,
        string? yieldSource, decimal? requiredKwp, int? maxByArea, List<string> assumptions, List<string> limitations, List<string> blocked, List<AdvisorSource> sources,
        Stopwatch clock, long providerMs)
        => new(status, input, missing, configs, yield, yieldSource, requiredKwp, maxByArea, assumptions, limitations, blocked, sources, Disclaimer, clock.ElapsedMilliseconds, providerMs);

    internal static string Fmt(decimal value) => value.ToString(value == Math.Round(value) ? "#,0" : "#,0.##", Vi);
    internal static string Money(decimal value) => Math.Round(value).ToString("#,0", Vi) + " ₫";
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    /// <summary>Reads the inverter power limits of a catalog spec (null when absent or malformed).</summary>
    public static (decimal? RatedAcPowerW, decimal? MaxDcInputPowerW, string? Phase) InverterSpec(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson)) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(specJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
            static decimal? Num(JsonElement root, string name)
                => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) && d > 0 ? d : null;
            var phase = doc.RootElement.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return (Num(doc.RootElement, "ratedAcPowerW"), Num(doc.RootElement, "maxDcInputPowerW"), phase);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }
}

/// <summary>
/// Deterministic recognition of advisor questions in Vietnamese / English (no model call): roof area (m², mét vuông), budget (triệu / tỷ / đồng /
/// VND), monthly consumption (kWh/tháng), battery wishes, and sizing phrases. A question counts as an advisor question when it states a sizing
/// value, or asks what to install (e.g. "nên lắp bao nhiêu tấm", "nên chọn inverter nào", "bao nhiêu kWp") without being about warranty,
/// contracts or payments.
/// </summary>
public static partial class SolarAdvisorTextExtractor
{
    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:m2|m²|mét\s*vuông|met\s*vuong)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AreaPattern();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(tỷ|ty|tỉ|triệu|trieu|tr)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BudgetWordPattern();

    [GeneratedRegex(@"(\d{1,3}(?:[.,]\d{3})+|\d{6,})\s*(?:đ|₫|vnd|vnđ|đồng|dong)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BudgetMoneyPattern();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*(?:kwh|kw\s*h|số\s*điện|so\s*dien|số)\s*(?:/|mỗi|moi|một|mot|trên|tren|per|a|\s)*\s*(?:tháng|thang|month)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsumptionPattern();

    [GeneratedRegex(@"\b(battery|batteries|ắc\s*quy|ac\s*quy|pin\s*lưu\s*trữ|pin\s*luu\s*tru|lưu\s*trữ|luu\s*tru|dự\s*phòng|du\s*phong|storage)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatteryPattern();

    [GeneratedRegex(@"(bao\s*nhiêu\s*(tấm|kwp|kw\b)|bao\s*nhieu\s*(tam|kwp)|nên\s*(lắp|chọn|dùng)|nen\s*(lap|chon|dung)|hệ\s*thống\s*(nào|bao\s*nhiêu)|cấu\s*hình|how\s+many\s+panels|which\s+(inverter|panel|system))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizingPhrase();

    [GeneratedRegex(@"(?:nghiêng|nghieng|tilt|độ\s*dốc|do\s*doc)\s*(?:khoảng|khoang|là|la|:)?\s*(\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TiltPattern();

    [GeneratedRegex(@"(?:hướng|huong|azimuth)\s*(?:về|ve|là|la|:)?\s*(\d+(?:[.,]\d+)?)\s*(?:độ|do|°)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AzimuthDegreePattern();

    [GeneratedRegex(@"(?:hướng|huong)\s*(?:về\s*|ve\s*)?(đông\s*nam|dong\s*nam|tây\s*nam|tay\s*nam|đông\s*bắc|dong\s*bac|tây\s*bắc|tay\s*bac|nam|bắc|bac|đông|dong|tây|tay)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AzimuthWordPattern();

    /// <summary>Compass bearing of a named direction (definition, not an estimate): N 0°, E 90°, S 180°, W 270°.</summary>
    private static decimal? Bearing(string word) => Regex.Replace(word.ToLowerInvariant(), @"\s+", " ") switch
    {
        "bắc" or "bac" => 0m, "đông bắc" or "dong bac" => 45m, "đông" or "dong" => 90m, "đông nam" or "dong nam" => 135m,
        "nam" => 180m, "tây nam" or "tay nam" => 225m, "tây" or "tay" => 270m, "tây bắc" or "tay bac" => 315m, _ => null,
    };

    [GeneratedRegex(@"(bảo\s*hành|bao\s*hanh|warranty|hợp\s*đồng|hop\s*dong|thanh\s*toán|thanh\s*toan|payment|hoàn\s*tiền|refund)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OtherTopic();

    public static SolarAdvisorInput Extract(string text)
    {
        decimal? area = null, budget = null, consumption = null;
        if (AreaPattern().Match(text) is { Success: true } a) area = Number(a.Groups[1].Value);
        if (ConsumptionPattern().Match(text) is { Success: true } c) consumption = Number(c.Groups[1].Value);
        if (BudgetWordPattern().Match(text) is { Success: true } b && Number(b.Groups[1].Value) is { } amount)
            budget = b.Groups[2].Value.ToLowerInvariant() is "tỷ" or "ty" or "tỉ" ? amount * 1_000_000_000m : amount * 1_000_000m;
        else if (BudgetMoneyPattern().Match(text) is { Success: true } m)
            budget = Number(m.Groups[1].Value.Replace(".", "").Replace(",", ""));
        decimal? tilt = TiltPattern().Match(text) is { Success: true } t ? Number(t.Groups[1].Value) : null;
        decimal? azimuth = AzimuthDegreePattern().Match(text) is { Success: true } az ? Number(az.Groups[1].Value)
            : AzimuthWordPattern().Match(text) is { Success: true } w ? Bearing(w.Groups[1].Value) : null;
        if (tilt is < 0 or > 90) tilt = null;
        if (azimuth is < 0 or > 360) azimuth = null;
        return new SolarAdvisorInput(area, budget, consumption, TiltDegree: tilt, AzimuthDegree: azimuth, WantsBattery: BatteryPattern().IsMatch(text));
    }

    /// <summary>True when the question is an advisor question (see the class summary).</summary>
    public static bool IsAdvisorQuestion(string text, SolarAdvisorInput extracted)
        => !OtherTopic().IsMatch(text) && (extracted.HasSizingInput || SizingPhrase().IsMatch(text));

    private static decimal? Number(string raw)
    {
        var normalized = raw.Replace(',', '.');
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}

/// <summary>Renders an advisor result as Vietnamese text (deterministic template; every figure is from the result).</summary>
public static class SolarAdvisorRenderer
{
    public static string Render(SolarAdvisorView v)
    {
        var sb = new StringBuilder();
        if (v.Status == SolarAdvisorStatuses.NeedsInput && v.Configurations.Count == 0)
        {
            sb.AppendLine("Để tư vấn cấu hình dựa trên dữ liệu thực, tôi cần thêm thông tin:");
            foreach (var m in v.MissingInputs) sb.AppendLine($"- {m.Question}");
            foreach (var b in v.Blocked) sb.AppendLine($"- Chưa thể tư vấn: {b}");
            sb.AppendLine().Append(SolarAdvisorService.Disclaimer);
            return sb.ToString().Trim();
        }
        if (v.Status == SolarAdvisorStatuses.Blocked && v.Configurations.Count == 0)
        {
            sb.AppendLine("Hiện tôi chưa thể đề xuất cấu hình an toàn vì thiếu dữ liệu đã xác minh:");
            foreach (var b in v.Blocked) sb.AppendLine($"- {b}");
            sb.AppendLine().Append(SolarAdvisorService.Disclaimer);
            return sb.ToString().Trim();
        }

        if (v.RequiredKwpForConsumption is { } kwp && v.SpecificYieldKwhPerKwpYear is { } y)
            sb.AppendLine($"Với mức dùng {SolarAdvisorService.Fmt(v.Input.MonthlyConsumptionKwh!.Value)} kWh/tháng và sản lượng riêng {SolarAdvisorService.Fmt(y)} kWh/kWp/năm, "
                          + $"hệ thống cần khoảng {SolarAdvisorService.Fmt(kwp)} kWp (kWp là công suất lắp đặt; kWh là điện năng).");
        if (v.TheoreticalMaxPanelsByArea is { } maxPanels && v.Input.RoofAreaM2 is { } area)
            sb.AppendLine($"Diện tích {SolarAdvisorService.Fmt(area)} m² chứa tối đa khoảng {maxPanels} tấm theo kích thước tấm lớn nhất phù hợp (giới hạn trên, chưa trừ khe hở, lề và lối đi).");
        var i = 0;
        foreach (var c in v.Configurations)
        {
            sb.AppendLine();
            sb.AppendLine($"Phương án {++i}: {c.PanelCount} tấm {c.Panel.Name} ({SolarAdvisorService.Fmt(c.PanelRatedPowerW)} W) = {SolarAdvisorService.Fmt(c.SystemKwp)} kWp, "
                          + $"diện tích tấm ~{SolarAdvisorService.Fmt(c.ModuleFootprintM2)} m².");
            sb.AppendLine(c.Inverter is { } inv
                ? $"- Inverter: {inv.Name} (AC {SolarAdvisorService.Fmt(c.InverterRatedAcPowerW ?? 0)} W, DC vào tối đa {SolarAdvisorService.Fmt(c.InverterMaxDcInputPowerW ?? 0)} W)."
                : $"- Inverter: {c.InverterNote}");
            if (c.AnnualProductionKwh is { } annual)
                sb.AppendLine($"- Sản lượng ước tính: ~{SolarAdvisorService.Fmt(annual)} kWh/năm (~{SolarAdvisorService.Fmt(c.AverageMonthlyProductionKwh ?? 0)} kWh/tháng)"
                              + (c.ConsumptionCoveragePercent is { } cov ? $", khoảng {SolarAdvisorService.Fmt(cov)}% điện năng tiêu thụ." : "."));
            sb.AppendLine($"- Chi phí tham khảo: {SolarAdvisorService.Money(c.Subtotal)} + thuế {SolarAdvisorService.Fmt(c.TaxRatePercent)}% = {SolarAdvisorService.Money(c.EstimatedTotal)}"
                          + (v.Input.BudgetVnd is { } budget ? (c.WithinBudget && c.EstimatedTotal <= budget ? " (trong ngân sách)." : " (vượt ngân sách).") : "."));
            sb.AppendLine($"- Giới hạn bởi: {c.LimitedBy switch { "CONSUMPTION" => "nhu cầu điện", "AREA" => "diện tích mái", "BUDGET" => "ngân sách", _ => c.LimitedBy }}.");
        }
        if (v.MissingInputs.Count > 0)
        {
            sb.AppendLine().AppendLine("Để tính đầy đủ hơn, vui lòng cho biết:");
            foreach (var m in v.MissingInputs) sb.AppendLine($"- {m.Question}");
        }
        if (v.Blocked.Count > 0)
        {
            sb.AppendLine().AppendLine("Chưa thể tư vấn:");
            foreach (var b in v.Blocked) sb.AppendLine($"- {b}");
        }
        sb.AppendLine().AppendLine("Giả định:");
        foreach (var a in v.Assumptions) sb.AppendLine($"- {a}");
        sb.AppendLine("Giới hạn:");
        foreach (var l in v.Limitations) sb.AppendLine($"- {l}");
        sb.AppendLine().Append(SolarAdvisorService.Disclaimer);
        sb.AppendLine().Append("Bạn có thể đặt lịch khảo sát trực tiếp hoặc làm tiền khảo sát để nhận báo giá chính thức.");
        return sb.ToString().Trim();
    }
}
