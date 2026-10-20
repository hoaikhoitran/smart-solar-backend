namespace SmartSolar.Modules.Scheduling.Entities;

/// <summary>
/// Scheduling data for a user with the TECHNICIAN role. Identity data stays in user_account;
/// this only adds what scheduling needs. Coverage comes from operational-zone memberships
/// (<see cref="Zones.TechnicianZoneMembership"/>), not from a base location.
/// </summary>
public sealed class TechnicianProfile
{
    public Guid UserId { get; set; }
    public bool IsSchedulingActive { get; set; }

    /// <summary>Optional per-technician cap; the configured default applies when null.</summary>
    public int? MaxAppointmentsPerDay { get; set; }

    /// <summary>
    /// True when <see cref="TechnicianWorkingPeriod"/> rows define this technician's week (a day
    /// without rows is a day off). False = the configured default schedule applies.
    /// </summary>
    public bool HasCustomWorkingSchedule { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Leave or other time the technician cannot take appointments.</summary>
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
/// <para><see cref="ReservedTechnicianUserId"/> is the CAPACITY HOLDER: while the appointment is open it
/// reserves one real technician's time so every pending appointment can still be served (the exclusion
/// constraint guards it). It is internal: never shown to customers and never an acceptance. Once a
/// technician is confirmed (ACCEPTED/ASSIGNED/COMPLETED) holder = <see cref="AssignedTechnicianUserId"/>.
/// An ESCALATED appointment may have no holder.</para>
/// Rescheduling cancels this row and creates a new one, so history is kept.
/// </summary>
public sealed class TechnicianAppointment
{
    public Guid Id { get; set; }
    public string AppointmentType { get; set; } = null!;
    public Guid? SurveyRequestId { get; set; }
    public Guid PropertySiteId { get; set; }

    /// <summary>The active operational zone of the site's commune when the appointment was booked (null for legacy bookings).</summary>
    public Guid? OperationalZoneId { get; set; }

    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public Guid? ReservedTechnicianUserId { get; set; }
    public Guid? AssignedTechnicianUserId { get; set; }

    /// <summary>The staff user (Sales or Manager/Admin) who assigned the technician; null for self-acceptance.</summary>
    public Guid? AssignedBySaleUserId { get; set; }

    public string Status { get; set; } = null!;
    public string? CustomerNote { get; set; }
    public Guid BookedByUserId { get; set; }
    public DateTimeOffset BookedAt { get; set; }

    /// <summary>After this instant an unaccepted appointment counts as escalated. A snapshot: settings changes never move it.</summary>
    public DateTimeOffset? AcceptanceDeadlineAt { get; set; }

    /// <summary>Snapshot of <c>scheduling.acceptanceWindowMinutes</c> used for <see cref="AcceptanceDeadlineAt"/> (null for legacy rows and direct assignments).</summary>
    public int? AcceptanceWindowMinutes { get; set; }

    /// <summary>The system settings version the acceptance deadline was derived from (null for legacy rows and direct assignments).</summary>
    public int? SettingsVersion { get; set; }

    /// <summary>When the acceptance reminder was recorded (at most once per acceptance window; reset when a release opens a new window).</summary>
    public DateTimeOffset? AcceptanceReminderSentAt { get; set; }

    /// <summary>The manual cross-zone authorization this appointment was booked under (uncovered-zone dispatch), if any.</summary>
    public Guid? CrossZoneAuthorizationId { get; set; }

    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }

    /// <summary>SELF_ACCEPTED, SALES_ASSIGNED, MANAGER_OVERRIDE or MANAGER_DISPATCH; set while a technician is confirmed.</summary>
    public string? AssignmentSource { get; set; }

    /// <summary>Required for staff assignments.</summary>
    public string? AssignmentReason { get; set; }

    /// <summary>Legacy: acknowledgment of the removed V1 acknowledge step (kept for history).</summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
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
/// Append-only appointment history: booking, acceptance, staff assignment, reassignment, hold moves,
/// release, escalation, reschedule, cancellation and completion. Kept even when the appointment row is replaced.
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
    public const string Accepted = "ACCEPTED";
    public const string Assigned = "ASSIGNED";
    public const string Reassigned = "REASSIGNED";

    /// <summary>The internal capacity hold moved to another technician (previous/new = holders).</summary>
    public const string HoldMoved = "HOLD_MOVED";

    public const string Released = "RELEASED";
    public const string Escalated = "ESCALATED";
    public const string Rescheduled = "RESCHEDULED";
    public const string Cancelled = "CANCELLED";
    public const string Completed = "COMPLETED";

    /// <summary>Reminder to the capacity holder before the acceptance deadline (<c>scheduling.acceptanceReminderLeadMinutes</c>; once per window).</summary>
    public const string AcceptanceReminder = "ACCEPTANCE_REMINDER";
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
