using System.Threading.RateLimiting;
using SmartSolar.Modules.Common.RateLimiting;

namespace SmartSolar.Api.Extensions;

/// <summary>
/// Cluster-wide fixed window: every API replica counts into the same Redis counter, so a user gets
/// <c>PermitLimit</c> requests per window in total, not per instance.
/// <para>
/// Redis failure policy: if Redis is unreachable, slow (&gt; 250 ms) or errors, the request is decided by
/// a per-instance fixed window with the same limit (fail to local). Requests never fail because Redis
/// is down, and limits keep holding per replica; cost-bearing operations are additionally bounded by
/// their PostgreSQL ledgers. The fallback is logged at most once a minute per process.
/// </para>
/// </summary>
public sealed class DistributedFixedWindowRateLimiter(
    IDistributedRateLimitCounter counter, string key, int permitLimit, TimeSpan window, ILogger logger) : RateLimiter
{
    private static readonly TimeSpan RedisBudget = TimeSpan.FromMilliseconds(250);
    private static long _lastFallbackLog;

    private readonly FixedWindowRateLimiter _local = new(new FixedWindowRateLimiterOptions
    {
        PermitLimit = permitLimit, Window = window, QueueLimit = 0, AutoReplenishment = true,
    });

    public override TimeSpan? IdleDuration => _local.IdleDuration;

    public override RateLimiterStatistics? GetStatistics() => _local.GetStatistics();

    /// <summary>The synchronous path cannot reach Redis; report "not acquired" so the middleware awaits <see cref="AcquireAsyncCore"/>.</summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Lease.NotYet;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(RedisBudget);
            var count = await counter.IncrementAsync(key, window, budget.Token).WaitAsync(budget.Token);
            return count.Count <= permitLimit ? Lease.Acquired : Lease.Rejected(count.ResetIn);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastFallbackLog);
            if (now - last > 60_000 && Interlocked.CompareExchange(ref _lastFallbackLog, now, last) == last)
                logger.LogWarning("Distributed rate limiting unavailable ({ExceptionType}); using per-instance limits.", ex.GetType().Name);
            return await _local.AcquireAsync(permitCount, cancellationToken);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _local.Dispose();
        base.Dispose(disposing);
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public static readonly Lease Acquired = new(true, null);
        public static readonly Lease NotYet = new(false, null);

        public static Lease Rejected(TimeSpan resetIn) => new(false, resetIn < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : resetIn);

        public override bool IsAcquired { get; } = acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
