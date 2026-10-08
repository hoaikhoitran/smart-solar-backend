using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.SolarSimulation.Energy;

namespace SmartSolar.Infrastructure.ExternalProviders;

/// <summary>
/// NASA POWER climatology (historical monthly means, not a forecast). Parameters:
/// <list type="bullet">
/// <item>ALLSKY_SFC_SW_DWN: all-sky surface shortwave downward irradiance, kW-hr/m^2/day.</item>
/// <item>T2M: temperature at 2 meters, °C.</item>
/// <item>PRECTOTCORR: corrected precipitation, mm/day (a daily rate, converted to mm per month).</item>
/// </list>
/// Units are checked; an unexpected unit is a failure rather than a silent misconversion.
/// </summary>
public sealed class NasaPowerClimatologyClient : IClimateContextProvider
{
    public const string Irradiation = "ALLSKY_SFC_SW_DWN";
    public const string Temperature = "T2M";
    public const string Precipitation = "PRECTOTCORR";

    private static readonly string[] MonthKeys = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly Dictionary<string, string> ExpectedUnits = new()
    {
        [Irradiation] = "kW-hr/m^2/day",
        [Temperature] = "C",
        [Precipitation] = "mm/day",
    };

    private readonly HttpClient _http;
    private readonly ICacheStore _cache;
    private readonly NasaPowerOptions _options;
    private readonly ILogger<NasaPowerClimatologyClient> _logger;

    public NasaPowerClimatologyClient(HttpClient http, ICacheStore cache, IOptions<NasaPowerOptions> options, ILogger<NasaPowerClimatologyClient> logger)
    {
        _http = http;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    public ClimateSettings Settings => new("NASA POWER", _options.ApiTag, _options.StartYear, _options.EndYear, _options.Community,
        [Irradiation, Temperature, Precipitation]);

    public async Task<ClimateContextResult> GetMonthlyClimatologyAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken)
    {
        // POWER grids are far coarser than 0.01°, so nearby sites share one request and cache entry.
        var lat = Math.Round(latitude, 2, MidpointRounding.AwayFromZero);
        var lon = Math.Round(longitude, 2, MidpointRounding.AwayFromZero);
        var parameters = string.Join(',', Irradiation, Temperature, Precipitation);
        var key = string.Join(':', "solar", "nasa-power", _options.ApiTag, $"{_options.StartYear}-{_options.EndYear}",
            _options.Community, lat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            lon.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), parameters);

        var cached = await TryGetCachedAsync(key, cancellationToken);
        if (cached is not null)
        {
            return ClimateContextResult.Success(cached);
        }

        var url = $"?parameters={parameters}&community={_options.Community}"
            + $"&longitude={lon.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&latitude={lat.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"
            + $"&start={_options.StartYear}&end={_options.EndYear}&format=JSON";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("NASA POWER returned HTTP {StatusCode}.", (int)response.StatusCode);
                return response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => ClimateContextResult.Fail(ProviderFailureCodes.RateLimited, "NASA POWER rate limit reached."),
                    HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity
                        => ClimateContextResult.Fail(ProviderFailureCodes.RejectedRequest, "NASA POWER rejected the request."),
                    _ => ClimateContextResult.Fail(ProviderFailureCodes.Unavailable, $"NASA POWER returned HTTP {(int)response.StatusCode}."),
                };
            }

            using var document = JsonDocument.Parse(body);
            var (context, problem) = Parse(document.RootElement, lat, lon);
            if (context is null)
            {
                _logger.LogWarning("NASA POWER response rejected: {Problem}", problem);
                return ClimateContextResult.Fail(ProviderFailureCodes.ResponseInvalid, problem!);
            }

            await TrySetCachedAsync(key, context, cancellationToken);
            return ClimateContextResult.Success(context);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ClimateContextResult.Fail(ProviderFailureCodes.Timeout, "NASA POWER did not respond in time.");
        }
        catch (TimeoutRejectedException)
        {
            return ClimateContextResult.Fail(ProviderFailureCodes.Timeout, "NASA POWER did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NASA POWER request failed.");
            return ClimateContextResult.Fail(ProviderFailureCodes.Unavailable, "NASA POWER could not be reached.");
        }
        catch (JsonException)
        {
            return ClimateContextResult.Fail(ProviderFailureCodes.ResponseInvalid, "NASA POWER returned malformed JSON.");
        }
    }

    private (ClimateContext? Context, string? Problem) Parse(JsonElement root, decimal lat, decimal lon)
    {
        var fill = JsonReading.Decimal(JsonReading.Path(root, "header", "fill_value")) ?? -999m;
        var units = new Dictionary<string, string>();
        var series = new Dictionary<string, decimal?[]>();
        var annual = new Dictionary<string, decimal?>();

        foreach (var (parameter, expectedUnit) in ExpectedUnits)
        {
            var unit = JsonReading.String(JsonReading.Path(root, "parameters", parameter, "units"));
            if (unit != expectedUnit)
            {
                return (null, $"Parameter {parameter} has unit '{unit ?? "missing"}', expected '{expectedUnit}'.");
            }

            if (JsonReading.Path(root, "properties", "parameter", parameter) is not { ValueKind: JsonValueKind.Object } values)
            {
                return (null, $"Parameter {parameter} is missing.");
            }

            var monthly = new decimal?[12];
            for (var i = 0; i < 12; i++)
            {
                if (!values.TryGetProperty(MonthKeys[i], out var raw))
                {
                    return (null, $"Parameter {parameter} has no value for {MonthKeys[i]}.");
                }
                var value = JsonReading.Decimal(raw);
                monthly[i] = value is { } v && v != fill ? v : null;
            }

            var ann = values.TryGetProperty("ANN", out var annRaw) ? JsonReading.Decimal(annRaw) : null;
            annual[parameter] = ann is { } a && a != fill ? a : null;
            units[parameter] = unit!;
            series[parameter] = monthly;
        }

        var months = new List<ClimateMonthly>(12);
        for (var i = 0; i < 12; i++)
        {
            var days = ClimatologyConversion.MeanDaysInMonth(i + 1, _options.StartYear, _options.EndYear);
            months.Add(new ClimateMonthly(
                i + 1,
                days,
                series[Irradiation][i],
                ClimatologyConversion.MonthlyTotal(series[Irradiation][i], days),
                series[Temperature][i],
                series[Precipitation][i],
                ClimatologyConversion.MonthlyTotal(series[Precipitation][i], days)));
        }

        var sources = JsonReading.Path(root, "header", "sources") is { ValueKind: JsonValueKind.Array } list
            ? list.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString()!).ToList()
            : [];

        var metadata = new ClimateMetadata(
            "NASA POWER",
            JsonReading.String(JsonReading.Path(root, "header", "api", "version")),
            _options.StartYear,
            _options.EndYear,
            _options.Community,
            sources,
            lat,
            lon,
            units,
            DateTimeOffset.UtcNow);

        return (new ClimateContext(
            months,
            SumOrNull(months.Select(m => m.IrradiationKwhPerM2)),
            SumOrNull(months.Select(m => m.PrecipitationMm)),
            annual[Temperature],
            metadata), null);
    }

    private static decimal? SumOrNull(IEnumerable<decimal?> values)
    {
        var list = values.ToList();
        return list.Any(v => v is null) ? null : list.Sum(v => v!.Value);
    }

    private async Task<ClimateContext?> TryGetCachedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetAsync<ClimateContext>(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NASA POWER cache read failed for key {CacheKey}.", key);
            return null;
        }
    }

    private async Task TrySetCachedAsync(string key, ClimateContext context, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.SetAsync(key, context, TimeSpan.FromHours(_options.CacheHours), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "NASA POWER cache write failed for key {CacheKey}.", key);
        }
    }
}
