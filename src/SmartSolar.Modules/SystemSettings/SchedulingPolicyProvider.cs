using System.Collections.Frozen;
using SmartSolar.Modules.Common.Persistence;

namespace SmartSolar.Modules.SystemSettings;

/// <summary>
/// Immutable effective scheduling policy at one settings <see cref="Version"/> (0 = bootstrap defaults). Every timing event
/// that derives a deadline from it must persist the derived deadline (and, where useful, the value and the version): later
/// updates never change an existing deadline.
/// </summary>
public sealed class SchedulingPolicySnapshot
{
    public SchedulingPolicySnapshot(int version, IReadOnlyDictionary<string, int> values)
    {
        Version = version;
        Values = values.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public int Version { get; }

    public IReadOnlyDictionary<string, int> Values { get; }

    /// <summary>The value of a registered key; throws for an unknown key (a programming error).</summary>
    public int Get(string key) => Values.TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Unknown system setting '{key}'.");

    public int DispatchReviewSlaMinutes => Get(SchedulingPolicyKeys.DispatchReviewSlaMinutes);
    public int DispatchEscalationDelayMinutes => Get(SchedulingPolicyKeys.DispatchEscalationDelayMinutes);
    public int DispatchReminderIntervalMinutes => Get(SchedulingPolicyKeys.DispatchReminderIntervalMinutes);
    public int DispatchProposalResponseWindowMinutes => Get(SchedulingPolicyKeys.DispatchProposalResponseWindowMinutes);
    public int AcceptanceWindowMinutes => Get(SchedulingPolicyKeys.AcceptanceWindowMinutes);
    public int AcceptanceMinimumLeadMinutes => Get(SchedulingPolicyKeys.AcceptanceMinimumLeadMinutes);
    public int AcceptanceReminderLeadMinutes => Get(SchedulingPolicyKeys.AcceptanceReminderLeadMinutes);
    public int CustomerChangeLeadMinutes => Get(SchedulingPolicyKeys.CustomerChangeLeadMinutes);
    public int MinimumBookingLeadMinutes => Get(SchedulingPolicyKeys.MinimumBookingLeadMinutes);
    public int BookingHorizonDays => Get(SchedulingPolicyKeys.BookingHorizonDays);
    public int BufferBetweenAppointmentsMinutes => Get(SchedulingPolicyKeys.BufferBetweenAppointmentsMinutes);
    public int OnSiteSurveyEarlyStartMinutes => Get(SchedulingPolicyKeys.OnSiteSurveyEarlyStartMinutes);
    public int InstallationMaxAssignmentDays => Get(SchedulingPolicyKeys.InstallationMaxAssignmentDays);

    /// <summary>Booking horizon and minimum lead, as the calendar takes them.</summary>
    public Scheduling.Domain.BookingLimits Booking => new(BookingHorizonDays, MinimumBookingLeadMinutes);
}

/// <summary>
/// Reads the effective scheduling policy. Handlers read it per request and workers on every tick; values are cached for at most
/// 15 s per process and invalidated immediately on this instance after an update. Other instances see an update within the TTL.
/// </summary>
public interface ISchedulingPolicyProvider
{
    Task<SchedulingPolicySnapshot> GetAsync(CancellationToken cancellationToken);

    /// <summary>Drops the cached snapshot on this instance (called after a successful update).</summary>
    void Invalidate();
}

/// <summary>Storage of the typed settings (implemented in Infrastructure).</summary>
public interface ISystemSettingsUnitOfWork : ITransactionalUnitOfWork
{
    Task<IReadOnlyList<SystemSetting>> GetSettingsAsync(bool track, CancellationToken cancellationToken);

    /// <summary>The global version row, row-locked FOR UPDATE on PostgreSQL (SQLite serialises writers) and tracked; null at bootstrap.</summary>
    Task<SystemSettingsVersion?> LockVersionAsync(CancellationToken cancellationToken);

    Task<SystemSettingsVersion?> FindVersionAsync(CancellationToken cancellationToken);

    /// <summary>Audit rows, newest version first, optionally for one key.</summary>
    Task<(IReadOnlyList<SystemSettingRevision> Items, int Total)> ListRevisionsAsync(string? key, int skip, int take, CancellationToken cancellationToken);

    void Add<TEntity>(TEntity entity) where TEntity : class;
}

/// <summary>
/// Process-wide cache of the snapshot (TTL clamped to 0–15 s; 0 disables caching). A generation counter stops a read that started
/// before an invalidation from caching the old value.
/// </summary>
public sealed class SchedulingPolicyCache(TimeSpan ttl)
{
    public static readonly TimeSpan MaxTtl = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private SchedulingPolicySnapshot? _value;
    private DateTimeOffset _expiresAt;
    private long _generation;

    public TimeSpan Ttl { get; } = ttl < TimeSpan.Zero ? TimeSpan.Zero : ttl > MaxTtl ? MaxTtl : ttl;

    public bool TryGet(DateTimeOffset now, out SchedulingPolicySnapshot? value, out long generation)
    {
        lock (_gate)
        {
            generation = _generation;
            value = _value is not null && now < _expiresAt ? _value : null;
            return value is not null;
        }
    }

    public void Set(SchedulingPolicySnapshot value, DateTimeOffset now, long generation)
    {
        if (Ttl == TimeSpan.Zero) return;
        lock (_gate)
        {
            if (generation != _generation) return;
            _value = value;
            _expiresAt = now + Ttl;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _value = null;
        }
    }
}

public sealed class SchedulingPolicyProvider(ISystemSettingsUnitOfWork unitOfWork, SchedulingPolicyDefinitions definitions, SchedulingPolicyCache cache,
    TimeProvider time) : ISchedulingPolicyProvider
{
    public async Task<SchedulingPolicySnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGet(time.GetUtcNow(), out var cached, out var generation)) return cached!;
        var snapshot = Build(definitions, await unitOfWork.GetSettingsAsync(false, cancellationToken));
        cache.Set(snapshot, time.GetUtcNow(), generation);
        return snapshot;
    }

    public void Invalidate() => cache.Invalidate();

    /// <summary>
    /// One read of the rows gives a consistent snapshot: each update stamps every changed key with the new global version, so the
    /// highest row version is the global version.
    /// </summary>
    public static SchedulingPolicySnapshot Build(SchedulingPolicyDefinitions definitions, IReadOnlyList<SystemSetting> rows)
        => new(rows.Count == 0 ? 0 : rows.Max(r => r.Version), definitions.Effective(rows));
}
