namespace SmartSolar.Modules.Scheduling.Dispatch;

/// <summary>
/// An uncovered-zone dispatch request: the customer's survey request could not be booked through slots (the site's commune has
/// no active zone, the zone has no schedulable technician, nobody is free in the horizon, or nobody at the wanted times), so
/// Manager/Admin arrange a technician by hand (direct assignment or a proposal the customer accepts).
/// <para>Timing is a snapshot taken at creation from the settings current then: <see cref="ReviewDueAt"/> = created_at +
/// <see cref="ReviewSlaMinutes"/>, <see cref="EscalationDueAt"/> = review_due_at + escalation delay, and the reminder interval;
/// <see cref="SettingsVersion"/> names the settings version. Later settings changes never move them.</para>
/// <para>A dispatch request NEVER expires: no status, job or setting closes it automatically. Only explicit Manager/Admin
/// (assign, unserviceable, close) or customer (accept a proposal, cancel) actions resolve it, all audited.</para>
/// </summary>
public sealed class DispatchRequest
{
    public Guid Id { get; set; }
    public Guid SurveyRequestId { get; set; }
    public Guid PropertySiteId { get; set; }
    public Guid CustomerUserId { get; set; }

    /// <summary>The site's active zone when the request was created (null for NO_ZONE_COVERAGE).</summary>
    public Guid? OperationalZoneId { get; set; }

    /// <summary>The coverage code that made the request necessary (<see cref="DispatchReasons"/>).</summary>
    public string Reason { get; set; } = null!;

    public string Status { get; set; } = null!;
    public string? CustomerNote { get; set; }

    public int ReviewSlaMinutes { get; set; }
    public DateTimeOffset ReviewDueAt { get; set; }
    public DateTimeOffset EscalationDueAt { get; set; }

    /// <summary>Snapshot of <c>dispatch.reminderIntervalMinutes</c> (0 = no reminders for this request).</summary>
    public int ReminderIntervalMinutes { get; set; }

    public int SettingsVersion { get; set; }

    public DateTimeOffset? OverdueAt { get; set; }
    public DateTimeOffset? EscalatedAt { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public int ReminderCount { get; set; }

    public DateTimeOffset? ResolvedAt { get; set; }
    public Guid? ResolvedByUserId { get; set; }

    /// <summary><see cref="DispatchResolutions"/>; set exactly when the status is terminal.</summary>
    public string? Resolution { get; set; }

    public string? ResolutionReason { get; set; }

    /// <summary>The appointment booked from this request (status ASSIGNED).</summary>
    public Guid? AppointmentId { get; set; }

    /// <summary>
    /// For reason ASSIGNMENT_RELEASED: the dispatch-assigned appointment the technician released (kept CANCELLED with its full
    /// history). Its time is offered again as the preferred window; staff reassign it or propose another time.
    /// </summary>
    public Guid? ReleasedAppointmentId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token; every change increments it.</summary>
    public int Revision { get; set; }
}

/// <summary>A time window the customer prefers (at most five, future, inside the booking horizon at creation).</summary>
public sealed class DispatchPreferredWindow
{
    public Guid Id { get; set; }
    public Guid DispatchRequestId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
}

/// <summary>
/// A concrete technician + time offered to the customer. Proposing validates eligibility at that moment but reserves NO capacity:
/// the customer's acceptance re-verifies everything under the scheduling locks and books atomically, or the proposal becomes FAILED
/// and the request is OPEN again. <see cref="ExpiresAt"/> = LEAST(created_at + <see cref="ResponseWindowMinutes"/>, starts_at) is a
/// snapshot; expiry marks only the proposal EXPIRED.
/// </summary>
public sealed class DispatchProposal
{
    public Guid Id { get; set; }
    public Guid DispatchRequestId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>The technician is not a member of the site's zone (or the site has no zone): a cross-zone authorization exists.</summary>
    public bool CrossZone { get; set; }

    public Guid? CrossZoneAuthorizationId { get; set; }
    public Guid ProposedByUserId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int ResponseWindowMinutes { get; set; }
    public int SettingsVersion { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>FAILED: the precise code and message of the failed acceptance; DECLINED/WITHDRAWN: the optional reason.</summary>
    public string? FailureReason { get; set; }

    public int Revision { get; set; }
}

/// <summary>
/// An explicit Manager/Admin authorization to use a technician outside the site's zone, for one dispatch request or one
/// installation project. It bypasses ONLY the zone-membership rule; nothing else (account, scheduling flag, working hours, leave,
/// overlap/buffer, daily limit, existing assignments, locks). Append-only audit.
/// </summary>
public sealed class CrossZoneAuthorization
{
    public Guid Id { get; set; }
    public Guid? DispatchRequestId { get; set; }
    public Guid? InstallationProjectId { get; set; }
    public Guid TechnicianUserId { get; set; }

    /// <summary>The site's zone at authorization time (null when the commune has no active zone).</summary>
    public Guid? TargetZoneId { get; set; }

    public string Reason { get; set; } = null!;
    public Guid AuthorizedByUserId { get; set; }
    public DateTimeOffset AuthorizedAt { get; set; }
}

/// <summary>Append-only history of a dispatch request (creation, SLA, proposals, resolution).</summary>
public sealed class DispatchEvent
{
    public Guid Id { get; set; }
    public Guid DispatchRequestId { get; set; }
    public Guid? ProposalId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A notification owed about a dispatch request (same pattern as scheduling_notification): no delivery channel exists yet, so rows
/// stay NOT_DELIVERED — recorded honestly instead of claiming delivery.
/// </summary>
public sealed class DispatchNotification
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid DispatchRequestId { get; set; }
    public Guid EventId { get; set; }
    public string Type { get; set; } = null!;
    public string DeliveryStatus { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Dispatch request lifecycle (no EXPIRED status exists):
/// OPEN → PROPOSAL_PENDING (proposal sent) → OPEN (declined / expired / withdrawn / failed) or ASSIGNED (accepted);
/// OPEN → ASSIGNED (direct assignment); OPEN | PROPOSAL_PENDING → UNSERVICEABLE (staff) or CLOSED (staff close, customer cancel).
/// ASSIGNED, UNSERVICEABLE and CLOSED are terminal.
/// </summary>
public static class DispatchStatuses
{
    public const string Open = "OPEN";
    public const string ProposalPending = "PROPOSAL_PENDING";
    public const string Assigned = "ASSIGNED";
    public const string Unserviceable = "UNSERVICEABLE";
    public const string Closed = "CLOSED";

    public static readonly IReadOnlyList<string> All = [Open, ProposalPending, Assigned, Unserviceable, Closed];

    /// <summary>Not resolved yet: at most one per survey request (partial unique index).</summary>
    public static bool IsActive(string status) => status is Open or ProposalPending;

    public static bool IsTerminal(string status) => !IsActive(status);
}

public static class DispatchResolutions
{
    public const string Assigned = "ASSIGNED";
    public const string Unserviceable = "UNSERVICEABLE";
    public const string ClosedByStaff = "CLOSED_BY_STAFF";
    public const string CancelledByCustomer = "CANCELLED_BY_CUSTOMER";

    public static readonly IReadOnlyList<string> All = [Assigned, Unserviceable, ClosedByStaff, CancelledByCustomer];
}

/// <summary>The dispatchable coverage codes a request is created for.</summary>
public static class DispatchReasons
{
    /// <summary>A technician released a dispatch-assigned (manager/cross-zone) appointment: the work is back in the staff queue.</summary>
    public const string AssignmentReleased = "ASSIGNMENT_RELEASED";

    public static IReadOnlyList<string> All { get; } = [.. Zones.CoverageStatuses.Dispatchable, AssignmentReleased];
}

public static class ProposalStatuses
{
    public const string Proposed = "PROPOSED";
    public const string Accepted = "ACCEPTED";
    public const string Declined = "DECLINED";
    public const string Expired = "EXPIRED";
    public const string Withdrawn = "WITHDRAWN";
    public const string Failed = "FAILED";

    public static readonly IReadOnlyList<string> All = [Proposed, Accepted, Declined, Expired, Withdrawn, Failed];
}

public static class DispatchEventTypes
{
    public const string Created = "CREATED";

    /// <summary>The technician released the appointment booked from a dispatch request; recorded on the old and the reopened request.</summary>
    public const string AssignmentReleased = "ASSIGNMENT_RELEASED";
    public const string Overdue = "OVERDUE";
    public const string Escalated = "ESCALATED";
    public const string Reminder = "REMINDER";
    public const string ProposalCreated = "PROPOSAL_CREATED";
    public const string ProposalAccepted = "PROPOSAL_ACCEPTED";
    public const string ProposalDeclined = "PROPOSAL_DECLINED";
    public const string ProposalExpired = "PROPOSAL_EXPIRED";
    public const string ProposalWithdrawn = "PROPOSAL_WITHDRAWN";
    public const string ProposalFailed = "PROPOSAL_FAILED";
    public const string CrossZoneAuthorized = "CROSS_ZONE_AUTHORIZED";
    public const string Assigned = "ASSIGNED";
    public const string Unserviceable = "UNSERVICEABLE";
    public const string Closed = "CLOSED";
    public const string CancelledByCustomer = "CANCELLED_BY_CUSTOMER";
}

public static class DispatchNotificationStatuses
{
    public const string NotDelivered = "NOT_DELIVERED";
}

public static class DispatchErrorCodes
{
    public const string DispatchRequestNotFound = "DISPATCH_REQUEST_NOT_FOUND";
    public const string ProposalNotFound = "DISPATCH_PROPOSAL_NOT_FOUND";

    /// <summary>The site is covered and bookable at the wanted times: book a slot instead.</summary>
    public const string DispatchNotNeeded = "DISPATCH_NOT_NEEDED";

    /// <summary>The action is not valid in the request's current status (terminal, or a proposal pending).</summary>
    public const string InvalidState = "DISPATCH_INVALID_STATE";

    public const string StaleRevision = "DISPATCH_STALE_REVISION";

    /// <summary>A live (PROPOSED) proposal exists: withdraw it or wait for the customer.</summary>
    public const string ProposalAlreadyPending = "PROPOSAL_ALREADY_PENDING";

    /// <summary>The proposal is no longer PROPOSED (accepted, declined, withdrawn, failed or expired).</summary>
    public const string ProposalNotPending = "PROPOSAL_NOT_PENDING";

    public const string ProposalExpired = "PROPOSAL_EXPIRED";

    /// <summary>The technician is outside the site's zone (or the site has no zone): crossZoneAuthorized = true and a reason are required.</summary>
    public const string CrossZoneAuthorizationRequired = "CROSS_ZONE_AUTHORIZATION_REQUIRED";

    public const string ReasonRequired = "DISPATCH_REASON_REQUIRED";
}
