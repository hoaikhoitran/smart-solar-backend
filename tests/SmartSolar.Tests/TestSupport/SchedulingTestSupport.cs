using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SmartSolar.Tests.TestSupport;

/// <summary>Fixed, adjustable clock for scheduling tests.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Captures every formatted log message (used to prove secrets are never logged).</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
    }
}

/// <summary>
/// SYNTHETIC travel-time estimator for tests: duration = fixed minutes per configured route, or
/// a deterministic default derived from straight-line distance (2.5 min/km + 5 min). Never calls
/// any provider. Can be switched into failure modes and records every request.
/// </summary>
public sealed class FakeTravelTimeEstimator : SmartSolar.Modules.Scheduling.Travel.ITravelTimeEstimator
{
    private readonly object _gate = new();
    private readonly List<SmartSolar.Modules.Scheduling.Travel.TravelEstimateRequest> _requests = new();

    public string? FailWith { get; set; }

    /// <summary>Per-request failure: return a non-OK status to fail just that leg, or null to answer normally.</summary>
    public Func<SmartSolar.Modules.Scheduling.Travel.TravelEstimateRequest, string?>? FailFor { get; set; }

    /// <summary>Congestion returned with OK results; null by default (no invented traffic data).</summary>
    public SmartSolar.Modules.Scheduling.Travel.CongestionSummary? Congestion { get; set; }

    /// <summary>Optional override per request: return minutes, or null to use the default.</summary>
    public Func<SmartSolar.Modules.Scheduling.Travel.TravelEstimateRequest, double?>? Minutes { get; set; }

    /// <summary>Optional hook run before answering (used to interleave concurrent changes).</summary>
    public Func<Task>? BeforeAnswer { get; set; }

    public IReadOnlyList<SmartSolar.Modules.Scheduling.Travel.TravelEstimateRequest> Requests { get { lock (_gate) return _requests.ToList(); } }

    public async Task<SmartSolar.Modules.Scheduling.Travel.TravelEstimateResult> EstimateAsync(
        SmartSolar.Modules.Scheduling.Travel.TravelEstimateRequest request, CancellationToken cancellationToken)
    {
        lock (_gate) _requests.Add(request);
        if (BeforeAnswer is { } hook) await hook();

        if ((FailWith ?? FailFor?.Invoke(request)) is { } status)
        {
            return new(status, "VIETMAP", "v4", "motorcycle", request.DepartureAtUtc, DateTimeOffset.UtcNow, null, null, null, "synthetic failure");
        }

        var meters = request.Origin.StraightLineMetersTo(request.Destination) * 1.3;
        var minutes = Minutes?.Invoke(request) ?? Math.Round(meters / 1000 * 2.5 + 5, 1);
        return new(SmartSolar.Modules.Scheduling.Travel.RouteCalculationStatuses.Ok, "VIETMAP", "v4", "motorcycle", request.DepartureAtUtc,
            DateTimeOffset.UtcNow, Math.Round(meters, 1), TimeSpan.FromMinutes(minutes),
            Congestion, null);
    }
}
