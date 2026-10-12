namespace SmartSolar.Modules.Scheduling.Entities;

/// <summary>
/// Scheduling data for a user with the TECHNICIAN role. Identity data stays in user_account;
/// this only adds what scheduling needs. The base location must be a real configured
/// coordinate; without it the technician is excluded from automatic scheduling.
/// </summary>
public sealed class TechnicianProfile
{
    public Guid UserId { get; set; }
    public decimal? BaseLatitude { get; set; }
    public decimal? BaseLongitude { get; set; }
    public string? BaseLabel { get; set; }
    public bool IsSchedulingActive { get; set; }

    /// <summary>Optional per-technician cap; the configured default applies when null.</summary>
    public int? MaxAppointmentsPerDay { get; set; }

    /// <summary>
    /// True when <see cref="TechnicianWorkingPeriod"/> rows define this technician's week (a day
    /// without rows is a day off). False = the configured default schedule applies.
    /// </summary>
    public bool HasCustomWorkingSchedule { get; set; }

    /// <summary>Local time-of-day before which the technician may not leave the base; null = configured default.</summary>
    public TimeOnly? EarliestBaseDeparture { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Leave or other time the technician cannot take appointments or travel.</summary>
public sealed class TechnicianUnavailablePeriod
{
    public Guid Id { get; set; }
    public Guid TechnicianUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string? Reason { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A technician appointment (shared by every appointment type; PRE_SURVEY in Flow 1).
/// <see cref="ReservedTechnicianUserId"/> is the technician whose capacity the appointment
/// currently occupies: chosen internally at booking and moved atomically when Sales assigns a
/// different technician. Rescheduling cancels this row and creates a new one, so history is kept.
/// </summary>
public sealed class TechnicianAppointment
{
    public Guid Id { get; set; }
    public string AppointmentType { get; set; } = null!;
    public Guid? SurveyRequestId { get; set; }
    public Guid PropertySiteId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public Guid ReservedTechnicianUserId { get; set; }
    public Guid? AssignedTechnicianUserId { get; set; }
    public Guid? AssignedBySaleUserId { get; set; }
    public string Status { get; set; } = null!;
    public string? CustomerNote { get; set; }
    public Guid BookedByUserId { get; set; }
    public DateTimeOffset BookedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }

    /// <summary>When rescheduled, the appointment that replaced this one.</summary>
    public Guid? ReplacedByAppointmentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token; every update increments it.</summary>
    public int Revision { get; set; }
}

/// <summary>
/// Minimal route-estimate snapshot (not the raw provider response, no geometry). Each row is the
/// inbound leg to <see cref="AppointmentId"/> as estimated at <see cref="EstimatedAt"/> for the
/// planned departure — a historical estimate, not live traffic.
/// </summary>
public sealed class TechnicianTravelEstimate
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public string Context { get; set; } = null!;
    public string OriginKind { get; set; } = null!;
    public Guid? OriginAppointmentId { get; set; }
    public decimal OriginLatitude { get; set; }
    public decimal OriginLongitude { get; set; }
    public decimal DestinationLatitude { get; set; }
    public decimal DestinationLongitude { get; set; }
    public DateTimeOffset DepartureAt { get; set; }
    public DateTimeOffset EstimatedAt { get; set; }
    public decimal? DistanceMeters { get; set; }
    public int? DurationSeconds { get; set; }
    public int SafetyBufferMinutes { get; set; }
    public DateTimeOffset? EstimatedArrivalAt { get; set; }
    public string Status { get; set; } = null!;
    public string Provider { get; set; } = null!;
    public string ProviderVersion { get; set; } = null!;
    public string Vehicle { get; set; } = null!;
    public string? CongestionSummary { get; set; }

    /// <summary>
    /// Set when the schedule changed so this estimate no longer describes the current route
    /// (cancellation, reschedule, reassignment, base change, or a newer estimate for the same
    /// appointment). Superseded rows are history only and are never shown as current.
    /// </summary>
    public DateTimeOffset? SupersededAt { get; set; }
}

/// <summary>
/// One working interval of a technician's custom weekly schedule, in local time
/// (<see cref="Options.SchedulingOptions.TimeZoneId"/>). Several rows per day are allowed (for
/// example a lunch break); they must not overlap.
/// </summary>
public sealed class TechnicianWorkingPeriod
{
    public Guid Id { get; set; }
    public Guid TechnicianUserId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday (System.DayOfWeek).</summary>
    public int DayOfWeek { get; set; }

    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
}

/// <summary>
/// Durable request to re-estimate one technician-day after its route chain changed. The row is
/// written in the same transaction as the business change, so it survives restarts; the inline
/// attempt and the background worker both process it, and it is deleted only after the new
/// estimates are committed.
/// </summary>
public sealed class TechnicianScheduleRecalculation
{
    public Guid TechnicianUserId { get; set; }
    public DateOnly LocalDate { get; set; }
    public string Status { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public DateTimeOffset RequestedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Lease: while in the future another worker is processing this row.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class RecalculationStatuses
{
    /// <summary>Waiting for (another) attempt.</summary>
    public const string Pending = "PENDING";

    /// <summary>The last attempt could not verify every route; retried with backoff.</summary>
    public const string Failed = "FAILED";
}

/// <summary>
/// Append-only appointment history: booking, assignment, reassignment, reschedule, cancellation,
/// acknowledgment and acknowledgment invalidation. Kept even when the appointment row is replaced.
/// </summary>
public sealed class TechnicianAppointmentEvent
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid? SurveyRequestId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public Guid? PreviousTechnicianUserId { get; set; }
    public Guid? NewTechnicianUserId { get; set; }
    public DateTimeOffset? PreviousStartsAt { get; set; }
    public DateTimeOffset? NewStartsAt { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class AppointmentEventTypes
{
    public const string Booked = "BOOKED";
    public const string Assigned = "ASSIGNED";
    public const string Reassigned = "REASSIGNED";
    public const string Rescheduled = "RESCHEDULED";
    public const string Cancelled = "CANCELLED";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string AcknowledgementInvalidated = "ACKNOWLEDGEMENT_INVALIDATED";
}

/// <summary>
/// A notification owed to a user about an appointment change. No delivery channel exists yet, so
/// rows stay <see cref="SchedulingNotificationStatuses.NotDelivered"/>: the state is recorded
/// honestly instead of claiming delivery.
/// </summary>
public sealed class SchedulingNotification
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid EventId { get; set; }
    public string Type { get; set; } = null!;
    public string DeliveryStatus { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

public static class SchedulingNotificationStatuses
{
    public const string NotDelivered = "NOT_DELIVERED";
}

public static class TravelEstimateContexts
{
    public const string Booking = "BOOKING";
    public const string Assignment = "ASSIGNMENT";
    public const string Reschedule = "RESCHEDULE";
    public const string Recalculation = "RECALCULATION";
}
