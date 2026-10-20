namespace SmartSolar.Modules.Installations;

/// <summary>
/// Installation of a PAID contract (one project per contract) carried out by ONE responsible technician.
/// Lifecycle (every transition is revision-checked and recorded in <see cref="InstallationEvent"/>):
/// PENDING_ASSIGNMENT → (Manager assigns) PENDING_TECHNICIAN_ACCEPTANCE → (technician accepts) SCHEDULED
/// → (technician starts) IN_PROGRESS → (completion report) PENDING_CUSTOMER_ACCEPTANCE → (customer accepts) COMPLETED,
/// or → (customer requests rework) REWORK_REQUIRED → (new evidence + report) PENDING_CUSTOMER_ACCEPTANCE.
/// A decline returns PENDING_TECHNICIAN_ACCEPTANCE to PENDING_ASSIGNMENT; a Manager may CANCEL before IN_PROGRESS.
/// Only the customer completes a project; the technician can only report completion.
/// </summary>
public sealed class InstallationProject
{
    public Guid Id { get; set; }
    public Guid ContractId { get; set; }
    public Guid SurveyRequestId { get; set; }
    public Guid CustomerUserId { get; set; }
    public Guid PropertySiteId { get; set; }
    public Guid ManagerUserId { get; set; }
    public string Status { get; set; } = null!;

    /// <summary>Active operational zone of the site's commune (resolved at creation, refreshed on every assignment).</summary>
    public Guid? OperationalZoneId { get; set; }

    /// <summary>Technician of the current (PENDING or ACCEPTED) assignment; null while nobody is assigned.</summary>
    public Guid? ResponsibleTechnicianUserId { get; set; }

    /// <summary>When the current assignment was accepted; null while it waits for the technician.</summary>
    public DateTimeOffset? TechnicianAcceptedAt { get; set; }

    public DateTimeOffset? ScheduledStartsAt { get; set; }
    public DateTimeOffset? ScheduledEndsAt { get; set; }

    /// <summary>Local dates of the scheduled interval (derived; kept for lists and filters).</summary>
    public DateOnly? PlannedStartDate { get; set; }
    public DateOnly? PlannedEndDate { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AcceptanceRequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? HandoverAt { get; set; }

    /// <summary>Number of completion reports so far. Evidence for the next report belongs to round <c>AcceptanceRound + 1</c>.</summary>
    public int AcceptanceRound { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }

    /// <summary>The evidence round the next completion report is judged on.</summary>
    public int CurrentRound => AcceptanceRound + 1;
}

/// <summary>
/// One responsible-technician assignment of a project for an interval (history is kept). At most one current
/// (PENDING or ACCEPTED) assignment per project (unique index); in PostgreSQL an exclusion constraint forbids two
/// current assignments of one technician with overlapping intervals. Survey scheduling treats current assignments
/// as busy intervals (with the buffer), and both sides serialise on the technician_profile row lock.
/// </summary>
public sealed class InstallationAssignment
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = null!;
    public Guid AssignedByUserId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }

    /// <summary>Manager's reason (required for a reassignment or reschedule).</summary>
    public string? AssignmentReason { get; set; }

    /// <summary>When the technician accepted or declined.</summary>
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>The technician's decline reason.</summary>
    public string? ResponseReason { get; set; }

    /// <summary>When the assignment stopped being current (REPLACED, COMPLETED, CANCELLED).</summary>
    public DateTimeOffset? ClosedAt { get; set; }
}

/// <summary>A verified INSTALLATION_EVIDENCE photo uploaded by the responsible technician for one round. Never deleted; each file is used once.</summary>
public sealed class InstallationEvidence
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int Round { get; set; }
    public Guid FileId { get; set; }
    public string? Caption { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>An acceptance round: the technician's completion report and the customer's immutable decision.</summary>
public sealed class InstallationAcceptance
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int Round { get; set; }
    public Guid RequestedByUserId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string CompletionReport { get; set; } = null!;
    public string Decision { get; set; } = null!;
    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Equipment actually installed (no invented data: entered by the responsible technician from the real devices).</summary>
public sealed class InstalledAsset
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string AssetType { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public string Manufacturer { get; set; } = null!;
    public string Model { get; set; } = null!;
    public string SerialNumber { get; set; } = null!;
    public string? PositionCode { get; set; }
    public int? WarrantyMonths { get; set; }
    public Guid RegisteredByUserId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}

/// <summary>Configurable pre-start condition (grid connection, fire safety…): applicability and evidence are explicit.</summary>
public sealed class InstallationChecklistItem
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Code { get; set; } = null!;
    public string Title { get; set; } = null!;
    public bool IsRequired { get; set; }
    public string Status { get; set; } = null!;
    public Guid? EvidenceFileId { get; set; }
    public string? Note { get; set; }
    public Guid? VerifiedByUserId { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

/// <summary>Audit trail (staff only). Status transitions carry from/to status.</summary>
public sealed class InstallationEvent
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public string? FromStatus { get; set; }
    public string? ToStatus { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class InstallationStatuses
{
    public const string PendingAssignment = "PENDING_ASSIGNMENT";
    public const string PendingTechnicianAcceptance = "PENDING_TECHNICIAN_ACCEPTANCE";
    public const string Scheduled = "SCHEDULED";
    public const string InProgress = "IN_PROGRESS";
    public const string PendingCustomerAcceptance = "PENDING_CUSTOMER_ACCEPTANCE";
    public const string ReworkRequired = "REWORK_REQUIRED";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All =
        [PendingAssignment, PendingTechnicianAcceptance, Scheduled, InProgress, PendingCustomerAcceptance, ReworkRequired, Completed, Cancelled];

    /// <summary>The Manager may (re)assign the responsible technician or reschedule.</summary>
    public static bool IsAssignable(string status)
        => status is PendingAssignment or PendingTechnicianAcceptance or Scheduled or InProgress or ReworkRequired;

    /// <summary>Work has started (reassignment keeps the status; the new technician must accept).</summary>
    public static bool IsStarted(string status) => status is InProgress or ReworkRequired;

    /// <summary>Cancellation is allowed only before work starts.</summary>
    public static bool IsCancellable(string status) => status is PendingAssignment or PendingTechnicianAcceptance or Scheduled;

    public static bool IsFinal(string status) => status is Completed or Cancelled;
}

public static class AssignmentStatuses
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Declined = "DECLINED";
    public const string Replaced = "REPLACED";
    public const string Completed = "COMPLETED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Pending, Accepted, Declined, Replaced, Completed, Cancelled];

    /// <summary>PENDING or ACCEPTED: the technician's interval is reserved (busy for survey scheduling too).</summary>
    public static bool IsCurrent(string status) => status is Pending or Accepted;
}

public static class AcceptanceDecisions
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string ReworkRequested = "REWORK_REQUESTED";
}

/// <summary>Who closed an acceptance round (views only; the decider id stays staff-internal).</summary>
public static class AcceptanceDeciders
{
    public const string Customer = "CUSTOMER";
    public const string Staff = "STAFF";
}

public static class ChecklistStatuses
{
    public const string Pending = "PENDING";
    public const string Satisfied = "SATISFIED";
    public const string NotApplicable = "NOT_APPLICABLE";
}

public static class AssetTypes
{
    public const string Panel = "PANEL";
    public const string Inverter = "INVERTER";
    public static readonly IReadOnlyList<string> All = [Panel, Inverter, "BATTERY", "METER", "OTHER"];
}

public static class InstallationEventTypes
{
    public const string ProjectCreated = "PROJECT_CREATED";
    public const string TechnicianAssigned = "TECHNICIAN_ASSIGNED";
    public const string TechnicianReassigned = "TECHNICIAN_REASSIGNED";
    public const string Rescheduled = "RESCHEDULED";
    public const string AssignmentAccepted = "ASSIGNMENT_ACCEPTED";
    public const string AssignmentDeclined = "ASSIGNMENT_DECLINED";
    public const string ProjectStarted = "PROJECT_STARTED";
    public const string EvidenceAdded = "EVIDENCE_ADDED";
    public const string AssetsRegistered = "ASSETS_REGISTERED";
    public const string CompletionReported = "COMPLETION_REPORTED";
    public const string CustomerAccepted = "CUSTOMER_ACCEPTED";
    public const string ReworkRequested = "REWORK_REQUESTED";

    /// <summary>A Manager/Admin returned a completion report to rework (PENDING_CUSTOMER_ACCEPTANCE → REWORK_REQUIRED).</summary>
    public const string ReturnedToRework = "RETURNED_TO_REWORK";
    public const string ChecklistUpdated = "CHECKLIST_UPDATED";
    public const string ProjectCancelled = "PROJECT_CANCELLED";
}

public static class InstallationErrorCodes
{
    public const string ProjectNotFound = "INSTALLATION_PROJECT_NOT_FOUND";
    public const string ContractNotFound = "INSTALLATION_CONTRACT_NOT_FOUND";
    public const string ChecklistItemNotFound = "INSTALLATION_CHECKLIST_ITEM_NOT_FOUND";
    public const string ContractNotPaid = "INSTALLATION_CONTRACT_NOT_PAID";
    public const string ProjectExists = "INSTALLATION_PROJECT_EXISTS";
    public const string InvalidState = "INSTALLATION_INVALID_STATE";
    public const string StaleRevision = "INSTALLATION_STALE_REVISION";
    public const string InvalidSchedule = "INSTALLATION_INVALID_SCHEDULE";
    public const string ReasonRequired = "INSTALLATION_REASON_REQUIRED";
    public const string AssignmentNotPending = "INSTALLATION_ASSIGNMENT_NOT_PENDING";
    public const string AssignmentNotAccepted = "INSTALLATION_ASSIGNMENT_NOT_ACCEPTED";
    public const string TooEarlyToStart = "INSTALLATION_TOO_EARLY_TO_START";

    /// <summary>The scheduled interval ended before the work started: the Manager must reschedule (409).</summary>
    public const string ScheduleExpired = "INSTALLATION_SCHEDULE_EXPIRED";
    public const string ChecklistIncomplete = "INSTALLATION_CHECKLIST_INCOMPLETE";
    public const string EvidenceRequired = "INSTALLATION_EVIDENCE_REQUIRED";
    public const string AssetsRequired = "INSTALLATION_ASSETS_REQUIRED";
    public const string DuplicateSerial = "INSTALLATION_DUPLICATE_SERIAL";
    public const string NotHandedOver = "INSTALLATION_NOT_HANDED_OVER";

    /// <summary>No such evidence on this project, or the caller may not see it (404, no existence disclosure).</summary>
    public const string EvidenceNotFound = "INSTALLATION_EVIDENCE_NOT_FOUND";

    /// <summary>The evidence photo's verified object is no longer in storage (410).</summary>
    public const string EvidenceObjectMissing = "INSTALLATION_EVIDENCE_OBJECT_MISSING";

    /// <summary>Same values as the survey scheduling codes, so clients handle coverage uniformly (422).</summary>
    public const string SiteLocationRequired = "SITE_LOCATION_REQUIRED";
    public const string NoZoneCoverage = "NO_ZONE_COVERAGE";
    public const string ZoneNotServiceable = "ZONE_NOT_SERVICEABLE";

    /// <summary>The technician is not a member of the site's zone (or the site has no zone): crossZoneAuthorized = true and a reason are required (409).</summary>
    public const string CrossZoneAuthorizationRequired = Scheduling.Dispatch.DispatchErrorCodes.CrossZoneAuthorizationRequired;
}

/// <summary>Default checklist copied into each new project (projects never share a mutable template).</summary>
public static class DefaultInstallationPlan
{
    /// <summary>Not every site needs every approval: items start not required; the manager decides applicability.</summary>
    public static readonly IReadOnlyList<(string Code, string Title)> Checklist =
    [
        ("GRID_CONNECTION_APPROVAL", "Grid connection / EVN approval"),
        ("FIRE_SAFETY_APPROVAL", "Fire safety (PCCC) approval"),
        ("STRUCTURAL_ASSESSMENT", "Roof structural assessment"),
    ];
}
