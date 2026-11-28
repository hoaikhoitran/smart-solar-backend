namespace SmartSolar.Modules.Common.Realtime;

/// <summary>
/// Optional realtime delivery (2026-11-28). A transient "something changed, refetch" signal sent to the exact recipients the business code already
/// chose when it persisted a notification row. It is never the source of truth: PostgreSQL rows and the REST endpoints are. Delivery is best effort
/// (at most once per connected tab, possibly duplicated across retries or missed while disconnected); clients refetch on every event and on reconnect.
/// </summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>Off by default. Read at startup: changing it needs an API restart (the hub and the interceptor are registered once).</summary>
    public bool Enabled { get; set; }

    /// <summary>Upper bound for one post-commit publication (all recipients of one commit). 100–10,000 ms; default 2,000 ms.</summary>
    public int PublishTimeoutMilliseconds { get; set; } = 2000;

    /// <summary>At most this many events are published for one commit; the rest are dropped with a warning (clients still refetch). 1–5,000.</summary>
    public int MaxEventsPerCommit { get; set; } = 500;

    public void Validate()
    {
        if (PublishTimeoutMilliseconds is < 100 or > 10_000)
            throw new InvalidOperationException("'Realtime:PublishTimeoutMilliseconds' must be between 100 and 10000.");
        if (MaxEventsPerCommit is < 1 or > 5_000)
            throw new InvalidOperationException("'Realtime:MaxEventsPerCommit' must be between 1 and 5000.");
    }
}

/// <summary>Where a committed notification row came from (the table that stores it).</summary>
public static class RealtimeSources
{
    /// <summary><c>after_sales_notification</c>: the in-app notifications (customer list <c>GET /api/customers/me/after-sales/notifications</c>; staff rows have no list endpoint).</summary>
    public const string InApp = "IN_APP";
    /// <summary><c>scheduling_notification</c>: appointment events for technicians / staff (no list endpoint; refetch the appointment views).</summary>
    public const string Scheduling = "SCHEDULING";
    /// <summary><c>dispatch_notification</c>: dispatch events for technicians / staff (no list endpoint; refetch the dispatch views).</summary>
    public const string Dispatch = "DISPATCH";
}

public static class RealtimeEventTypes
{
    /// <summary>A notification row for this user was committed.</summary>
    public const string NotificationCreated = "NotificationCreated";
    /// <summary>One of this user's in-app notifications was marked read (sync other tabs).</summary>
    public const string NotificationRead = "NotificationRead";
}

public static class RealtimeResourceTypes
{
    public const string Notification = "Notification";
    public const string ServiceRequest = "ServiceRequest";
    public const string ServiceSubscription = "ServiceSubscription";
    public const string Appointment = "Appointment";
    public const string DispatchRequest = "DispatchRequest";
}

/// <summary>One committed notification for one recipient (server side; the recipient is never taken from a client).</summary>
public sealed record RealtimeNotification(Guid RecipientUserId, Guid NotificationId, string EventType, string Source, string NotificationType,
    string ResourceType, Guid? ResourceId, DateTimeOffset OccurredAt);

/// <summary>
/// The versioned wire envelope (method <c>realtime</c>). Invalidation only: ids and type codes, no message text, amounts, contract content,
/// personal data or URLs. <see cref="EventId"/> is the notification row id (stable across duplicates: clients may de-duplicate on it).
/// </summary>
public sealed record RealtimeEnvelope(int Version, Guid EventId, string EventType, string Source, string NotificationType, string ResourceType,
    Guid? ResourceId, DateTimeOffset OccurredAt)
{
    public const int CurrentVersion = 1;

    public static RealtimeEnvelope From(RealtimeNotification n)
        => new(CurrentVersion, n.NotificationId, n.EventType, n.Source, n.NotificationType, n.ResourceType, n.ResourceId, n.OccurredAt.ToUniversalTime());
}

/// <summary>Delivers committed notifications. Implementations never throw to the caller and finish within the configured timeout.</summary>
public interface IRealtimeNotificationPublisher
{
    Task PublishAsync(IReadOnlyList<RealtimeNotification> notifications, CancellationToken cancellationToken);
}

/// <summary>Used when realtime is disabled: does nothing.</summary>
public sealed class NoOpRealtimeNotificationPublisher : IRealtimeNotificationPublisher
{
    public Task PublishAsync(IReadOnlyList<RealtimeNotification> notifications, CancellationToken cancellationToken) => Task.CompletedTask;
}
