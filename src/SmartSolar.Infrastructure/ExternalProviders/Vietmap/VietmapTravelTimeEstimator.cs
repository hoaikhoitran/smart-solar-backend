using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Timeout;
using SmartSolar.Modules.Common.Caching;
using SmartSolar.Modules.Scheduling.Travel;

namespace SmartSolar.Infrastructure.ExternalProviders.Vietmap;

/// <summary>Process-wide cap on simultaneous Vietmap requests.</summary>
public sealed class VietmapRequestLimiter(IOptions<VietmapOptions> options)
{
    public SemaphoreSlim Gate { get; } = new(Math.Max(1, options.Value.MaxConcurrentRequests));
}

/// <summary>
/// Vietmap Route API v4 adapter (GET /api/route/v4).
/// <list type="bullet">
/// <item>Origin and destination are sent as repeated <c>point=latitude,longitude</c> parameters.</item>
/// <item><c>vehicle=motorcycle</c> always; <c>time</c> is the departure in ISO-8601 UTC (bucketed).</item>
/// <item><c>paths[0].distance</c> is meters and <c>paths[0].time</c> is milliseconds.</item>
/// <item>Non-OK codes, HTTP failures, timeouts and malformed bodies become explicit statuses;
/// no duration is ever invented.</item>
/// </list>
/// The request URL contains the API key, so it is never logged (the client is registered without
/// the default HttpClient logging handlers) and exceptions are logged without their messages.
/// </summary>
public sealed class VietmapTravelTimeEstimator : ITravelTimeEstimator
{
    public const string ProviderName = "VIETMAP";
    public const string ProviderVersion = "v4";

    private readonly HttpClient _http;
    private readonly ICacheStore _cache;
    private readonly VietmapOptions _options;
    private readonly VietmapRequestLimiter _limiter;
    private readonly TimeProvider _time;
    private readonly ILogger<VietmapTravelTimeEstimator> _logger;

    public VietmapTravelTimeEstimator(
        HttpClient http,
        ICacheStore cache,
        IOptions<VietmapOptions> options,
        VietmapRequestLimiter limiter,
        TimeProvider time,
        ILogger<VietmapTravelTimeEstimator> logger)
    {
        _http = http;
        _cache = cache;
        _options = options.Value;
        _limiter = limiter;
        _time = time;
        _logger = logger;
    }

    public async Task<TravelEstimateResult> EstimateAsync(TravelEstimateRequest request, CancellationToken cancellationToken)
    {
        var apiKey = VietmapOptions.Clean(_options.ApiKey);
        if (!_options.Enabled || string.IsNullOrWhiteSpace(apiKey))
        {
            return Failure(request, RouteCalculationStatuses.ProviderDisabled, "Vietmap routing is not enabled or has no API key configured.");
        }

        var departure = Bucket(request.DepartureAtUtc);
        var key = string.Join(':', "scheduling", "vietmap", ProviderVersion, VietmapOptions.RequiredVehicle,
            F5(request.Origin.Latitude), F5(request.Origin.Longitude), F5(request.Destination.Latitude), F5(request.Destination.Longitude),
            departure.ToString("yyyyMMddTHHmm", CultureInfo.InvariantCulture), _options.RequestCongestion ? "c1" : "c0");

        var cached = await TryGetCachedAsync(key, cancellationToken);
        if (cached is not null)
        {
            return cached with { DepartureAtUtc = request.DepartureAtUtc };
        }

        var url = "api/route/v4"
            + $"?apikey={Uri.EscapeDataString(apiKey)}"
            + $"&point={F5(request.Origin.Latitude)},{F5(request.Origin.Longitude)}"
            + $"&point={F5(request.Destination.Latitude)},{F5(request.Destination.Longitude)}"
            + $"&vehicle={VietmapOptions.RequiredVehicle}"
            + "&points_encoded=true"
            + $"&time={departure.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}"
            + (_options.RequestCongestion ? "&annotations=congestion,congestion_distance" : "");

        await _limiter.Gate.WaitAsync(cancellationToken);
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = Interpret(request, response.StatusCode, body);

            if (result.IsOk)
            {
                await TrySetCachedAsync(key, result, cancellationToken);
            }
            else
            {
                _logger.LogWarning("Vietmap route estimate not verified: {RouteStatus} (HTTP {StatusCode}).", result.Status, (int)response.StatusCode);
            }

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(request, RouteCalculationStatuses.Timeout, "Vietmap did not respond in time.");
        }
        catch (TimeoutRejectedException)
        {
            return Failure(request, RouteCalculationStatuses.Timeout, "Vietmap did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            // The message is not logged: it is not guaranteed to be free of request details.
            _logger.LogWarning("Vietmap request failed with {ExceptionType}.", ex.GetType().Name);
            return Failure(request, RouteCalculationStatuses.ProviderUnavailable, "Vietmap could not be reached.");
        }
        finally
        {
            _limiter.Gate.Release();
        }
    }

    private TravelEstimateResult Interpret(TravelEstimateRequest request, HttpStatusCode status, string body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return status switch
            {
                HttpStatusCode.TooManyRequests => Failure(request, RouteCalculationStatuses.RateLimited, "Vietmap rate limit reached."),
                >= HttpStatusCode.InternalServerError => Failure(request, RouteCalculationStatuses.ProviderUnavailable, $"Vietmap returned HTTP {(int)status}."),
                _ => Failure(request, RouteCalculationStatuses.InvalidResponse, "Vietmap returned a body that is not JSON."),
            };
        }

        var code = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString()
            : null;

        switch (code)
        {
            case "OK":
                break;
            case "ZERO_RESULTS":
                return Failure(request, RouteCalculationStatuses.RouteNotFound, "Vietmap found no route between the points.");
            case "OVER_DAILY_LIMIT":
                return Failure(request, RouteCalculationStatuses.QuotaExceeded, "Vietmap daily request limit exceeded.");
            case "INVALID_REQUEST":
            case "MAX_POINTS_EXCEED":
                return Failure(request, RouteCalculationStatuses.RejectedRequest, $"Vietmap rejected the request ({code}).");
            case "ERROR_UNKNOWN":
                return Failure(request, RouteCalculationStatuses.ProviderUnavailable, "Vietmap reported an unknown error.");
            default:
                return status switch
                {
                    HttpStatusCode.TooManyRequests => Failure(request, RouteCalculationStatuses.RateLimited, "Vietmap rate limit reached."),
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Failure(request, RouteCalculationStatuses.RejectedRequest, "Vietmap refused the API key."),
                    >= HttpStatusCode.InternalServerError => Failure(request, RouteCalculationStatuses.ProviderUnavailable, $"Vietmap returned HTTP {(int)status}."),
                    _ => Failure(request, RouteCalculationStatuses.InvalidResponse, "Vietmap response has no recognised status code."),
                };
        }

        if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array || paths.GetArrayLength() == 0)
        {
            return Failure(request, RouteCalculationStatuses.InvalidResponse, "Vietmap returned OK without any path.");
        }

        var path = paths[0];
        if (!TryNumber(path, "distance", out var meters) || !TryNumber(path, "time", out var milliseconds)
            || meters < 0 || milliseconds < 0 || double.IsNaN(meters) || double.IsInfinity(meters) || double.IsInfinity(milliseconds))
        {
            return Failure(request, RouteCalculationStatuses.InvalidResponse, "Vietmap path has a missing or negative distance or time.");
        }

        return new TravelEstimateResult(
            RouteCalculationStatuses.Ok,
            ProviderName,
            ProviderVersion,
            VietmapOptions.RequiredVehicle,
            request.DepartureAtUtc,
            _time.GetUtcNow(),
            meters,
            TimeSpan.FromMilliseconds(milliseconds),
            ReadCongestion(path),
            null);
    }

    private static readonly HashSet<string> CongestionLevels = new(StringComparer.Ordinal) { "low", "moderate", "heavy", "severe", "unknown" };

    /// <summary>
    /// Reads <c>paths[0].annotations.congestion</c> and the optional <c>annotations.congestion_distance</c>.
    /// Elements may be plain values (as documented) or <c>{ "value", "first", "last" }</c> objects (as
    /// returned live). Aligned arrays (same length and point ranges) give meters per level; otherwise
    /// the distances are treated as the documented heavy-or-worse list. Returns null when no usable
    /// congestion data was sent; never derives congestion from the duration. Malformed annotations are
    /// ignored rather than failing an otherwise valid route.
    /// </summary>
    private static CongestionSummary? ReadCongestion(JsonElement path)
    {
        if (!path.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Object
            || !annotations.TryGetProperty("congestion", out var levels) || levels.ValueKind != JsonValueKind.Array
            || levels.GetArrayLength() == 0)
        {
            return null;
        }

        var segments = new List<(string Level, int? First, int? Last)>();
        foreach (var item in levels.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var v) ? v : item;
            if (value.ValueKind != JsonValueKind.String || !CongestionLevels.Contains(value.GetString()!)) return null;
            segments.Add((value.GetString()!, Range(item, "first"), Range(item, "last")));
        }

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var segment in segments) counts[segment.Level] = counts.GetValueOrDefault(segment.Level) + 1;

        if (!annotations.TryGetProperty("congestion_distance", out var distances) || distances.ValueKind != JsonValueKind.Array)
        {
            return new CongestionSummary(counts, null);
        }

        var meters = new List<(double Meters, int? First, int? Last)>();
        foreach (var item in distances.EnumerateArray())
        {
            var value = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("value", out var v) ? v : item;
            if (value.ValueKind != JsonValueKind.Number || value.GetDouble() < 0 || double.IsInfinity(value.GetDouble()))
            {
                return new CongestionSummary(counts, null);
            }
            meters.Add((value.GetDouble(), Range(item, "first"), Range(item, "last")));
        }

        var aligned = meters.Count == segments.Count
                      && segments.Zip(meters).All(p => p.First.First == p.Second.First && p.First.Last == p.Second.Last);
        if (!aligned)
        {
            return new CongestionSummary(counts, Math.Round(meters.Sum(m => m.Meters), 1));
        }

        var byLevel = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (segment, distance) in segments.Zip(meters))
        {
            byLevel[segment.Level] = byLevel.GetValueOrDefault(segment.Level) + distance.Meters;
        }
        foreach (var level in byLevel.Keys.ToList()) byLevel[level] = Math.Round(byLevel[level], 1);
        var heavy = byLevel.GetValueOrDefault("heavy") + byLevel.GetValueOrDefault("severe");
        return new CongestionSummary(counts, Math.Round(heavy, 1), byLevel);
    }

    private static int? Range(JsonElement item, string name)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out value);
    }

    private TravelEstimateResult Failure(TravelEstimateRequest request, string status, string message)
        => new(status, ProviderName, ProviderVersion, VietmapOptions.RequiredVehicle, request.DepartureAtUtc, _time.GetUtcNow(), null, null, null, message);

    private DateTimeOffset Bucket(DateTimeOffset departure)
    {
        var minutes = Math.Max(1, _options.DepartureBucketMinutes);
        var utc = departure.ToUniversalTime();
        var floored = utc.Ticks - utc.Ticks % TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTimeOffset(floored, TimeSpan.Zero);
    }

    private static string F5(decimal value) => Math.Round(value, 5).ToString("0.00000", CultureInfo.InvariantCulture);

    private async Task<TravelEstimateResult?> TryGetCachedAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await _cache.GetAsync<TravelEstimateResult>(key, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Route cache read failed with {ExceptionType}.", ex.GetType().Name);
            return null;
        }
    }

    private async Task TrySetCachedAsync(string key, TravelEstimateResult result, CancellationToken cancellationToken)
    {
        try
        {
            await _cache.SetAsync(key, result, TimeSpan.FromMinutes(Math.Max(1, _options.CacheMinutes)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Route cache write failed with {ExceptionType}.", ex.GetType().Name);
        }
    }
}
