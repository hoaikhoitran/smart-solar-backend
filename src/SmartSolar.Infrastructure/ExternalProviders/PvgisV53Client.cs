using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.SolarSimulation.Energy;
using SmartSolar.Modules.SolarSimulation.Geometry;

namespace SmartSolar.Infrastructure.ExternalProviders;

/// <summary>
/// Version-specific adapter for the PVGIS 5.3 PVcalc tool (GET, JSON output). Converts
/// SmartSolar compass azimuth to the PVGIS south-based aspect. Successful results are
/// cached by every parameter; failures are never cached and never turned into zero energy.
/// </summary>
public sealed class PvgisV53Client : IPvEnergyEstimator
{
    private readonly HttpClient _http;
    private readonly ICacheStore _cache;
    private readonly PvgisOptions _options;
    private readonly ILogger<PvgisV53Client> _logger;

    public PvgisV53Client(HttpClient http, ICacheStore cache, IOptions<PvgisOptions> options, ILogger<PvgisV53Client> logger)
    {
        _http = http;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public PvEnergySettings Settings => new("PVGIS", _options.ProviderVersion, _options.Technology, _options.UseHorizon);

    public async Task<PvEnergyResult> EstimateAsync(PvEnergyRequest request, CancellationToken cancellationToken)
    {
        var aspect = Math.Round((decimal)AzimuthConvention.ToPvgisAspect((double)request.CompassAzimuthDegree), 2);
        var latitude = Math.Round(request.Latitude, 4);
        var longitude = Math.Round(request.Longitude, 4);
        var peakPower = Math.Round(request.PeakPowerKwp, 3);
        var key = string.Join(':', "solar", "pvgis", _options.ProviderVersion,
            JsonReading.Invariant(latitude), JsonReading.Invariant(longitude), JsonReading.Invariant(peakPower),
            JsonReading.Invariant(request.SystemLossPercent), JsonReading.Invariant(request.TiltDegree),
            JsonReading.Invariant(aspect), request.MountingPlace, _options.Technology, _options.UseHorizon ? "h1" : "h0");

        var cached = await TryGetCachedAsync(key, cancellationToken);
        if (cached is not null)
        {
            return PvEnergyResult.Success(cached);
        }

        var url = "PVcalc"
            + $"?lat={JsonReading.Invariant(latitude)}&lon={JsonReading.Invariant(longitude)}"
            + $"&peakpower={JsonReading.Invariant(peakPower)}&loss={JsonReading.Invariant(request.SystemLossPercent)}"
            + $"&angle={JsonReading.Invariant(request.TiltDegree)}&aspect={JsonReading.Invariant(aspect)}"
            + $"&mountingplace={request.MountingPlace}&pvtechchoice={_options.Technology}"
            + $"&usehorizon={(_options.UseHorizon ? 1 : 0)}&outputformat=json";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return MapHttpFailure(response.StatusCode, body);
            }

            using var document = JsonDocument.Parse(body);
            var estimate = Parse(document.RootElement, request, peakPower, aspect);
            if (estimate is null)
            {
                _logger.LogWarning("PVGIS returned a response without the expected monthly and annual energy fields.");
                return PvEnergyResult.Fail(ProviderFailureCodes.ResponseInvalid, "PVGIS response is missing monthly or annual energy values.");
            }

            await TrySetCachedAsync(key, estimate, cancellationToken);
            return PvEnergyResult.Success(estimate);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return PvEnergyResult.Fail(ProviderFailureCodes.Timeout, "PVGIS did not respond in time.");
        }
        catch (TimeoutRejectedException)
        {
            return PvEnergyResult.Fail(ProviderFailureCodes.Timeout, "PVGIS did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "PVGIS request failed.");
            return PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, "PVGIS could not be reached.");
        }
        catch (JsonException)
        {
            _logger.LogWarning("PVGIS returned malformed JSON.");
            return PvEnergyResult.Fail(ProviderFailureCodes.ResponseInvalid, "PVGIS returned malformed JSON.");
        }
    }

    private PvEnergyResult MapHttpFailure(HttpStatusCode status, string body)
    {
        var message = TryReadMessage(body);
        _logger.LogWarning("PVGIS returned HTTP {StatusCode}: {ProviderMessage}", (int)status, message);

        return status switch
        {
            HttpStatusCode.BadRequest when message?.Contains("spatial coverage", StringComparison.OrdinalIgnoreCase) == true
                => PvEnergyResult.Fail(ProviderFailureCodes.LocationNotCovered, message),
            HttpStatusCode.BadRequest
                => PvEnergyResult.Fail(ProviderFailureCodes.RejectedRequest, message ?? "PVGIS rejected the request."),
            HttpStatusCode.TooManyRequests
                => PvEnergyResult.Fail(ProviderFailureCodes.RateLimited, "PVGIS rate limit reached."),
            _ => PvEnergyResult.Fail(ProviderFailureCodes.Unavailable, $"PVGIS returned HTTP {(int)status}."),
        };
    }

    private static string? TryReadMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var message = JsonReading.String(JsonReading.Path(document.RootElement, "message"));
            return message is null ? null : message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private PvEnergyEstimate? Parse(JsonElement root, PvEnergyRequest request, decimal peakPower, decimal aspect)
    {
        if (JsonReading.Path(root, "outputs", "monthly", "fixed") is not { ValueKind: JsonValueKind.Array } months
            || JsonReading.Path(root, "outputs", "totals", "fixed") is not { ValueKind: JsonValueKind.Object } totals)
        {
            return null;
        }

        var monthly = new List<PvMonthlyEnergy>();
        foreach (var item in months.EnumerateArray())
        {
            var month = JsonReading.Int(JsonReading.Path(item, "month"));
            var energy = JsonReading.Decimal(JsonReading.Path(item, "E_m"));
            if (month is not (>= 1 and <= 12) || energy is not >= 0) return null;

            monthly.Add(new PvMonthlyEnergy(
                month.Value,
                energy.Value,
                JsonReading.Decimal(JsonReading.Path(item, "SD_m")),
                JsonReading.Decimal(JsonReading.Path(item, "H(i)_m"))));
        }

        if (monthly.Count != 12 || monthly.Select(m => m.Month).Distinct().Count() != 12) return null;

        var annual = JsonReading.Decimal(JsonReading.Path(totals, "E_y"));
        if (annual is not >= 0) return null;

        var meteo = JsonReading.Path(root, "inputs", "meteo_data");
        var metadata = new PvProviderMetadata(
            "PVGIS",
            _options.ProviderVersion,
            meteo is { } m1 ? JsonReading.String(JsonReading.Path(m1, "radiation_db")) : null,
            meteo is { } m2 ? JsonReading.String(JsonReading.Path(m2, "meteo_db")) : null,
            meteo is { } m3 ? JsonReading.Int(JsonReading.Path(m3, "year_min")) : null,
            meteo is { } m4 ? JsonReading.Int(JsonReading.Path(m4, "year_max")) : null,
            meteo is { } m5 ? JsonReading.String(JsonReading.Path(m5, "horizon_db")) : null,
            request.MountingPlace,
            _options.Technology,
            _options.UseHorizon,
            peakPower,
            request.SystemLossPercent,
            request.TiltDegree,
            aspect,
            DateTimeOffset.UtcNow);

        return new PvEnergyEstimate(
            annual.Value,
            JsonReading.Decimal(JsonReading.Path(totals, "SD_y")),
            JsonReading.Decimal(JsonReading.Path(totals, "H(i)_y")),
            JsonReading.Decimal(JsonReading.Path(totals, "l_total")),
            monthly.OrderBy(x => x.Month).ToList(),
            metadata);
    }

    private async Task<PvEnergyEstimate?> TryGetCachedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetAsync<PvEnergyEstimate>(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PVGIS cache read failed for key {CacheKey}.", key);
            return null;
        }
    }

    private async Task TrySetCachedAsync(string key, PvEnergyEstimate estimate, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.SetAsync(key, estimate, TimeSpan.FromHours(_options.CacheHours), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PVGIS cache write failed for key {CacheKey}.", key);
        }
    }
}
