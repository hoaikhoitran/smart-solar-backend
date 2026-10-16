namespace SmartSolar.Modules.Installations;

/// <summary>
/// Installation of a PAID contract (one project per contract). Lifecycle:
/// PLANNING → SCHEDULED (team and shifts booked) → IN_PROGRESS (leader started; milestones locked)
/// → PENDING_CUSTOMER_ACCEPTANCE → COMPLETED, or → REWORK_REQUIRED → (rework) → PENDING_CUSTOMER_ACCEPTANCE.
/// Only the customer completes a project; technicians can only request acceptance.
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
    public DateOnly? PlannedStartDate { get; set; }
    public DateOnly? PlannedEndDate { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AcceptanceRequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? HandoverAt { get; set; }
    public int AcceptanceRound { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>Project team membership. The leader is a per-project relation, not a system role.</summary>
public sealed class InstallationTeamMember
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public bool IsLeader { get; set; }
    public Guid AssignedByUserId { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>A shared working shift of the whole team on one local date (local times, stored as UTC instants).</summary>
public sealed class InstallationShift
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public DateOnly LocalDate { get; set; }
    public TimeOnly LocalStart { get; set; }
    public TimeOnly LocalEnd { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One technician's reservation of one shift. In PostgreSQL an exclusion constraint forbids
/// overlapping ACTIVE assignments of the same technician; survey appointments are serialised with
/// them through the shared technician-profile row lock and checked in every day state.
/// </summary>
public sealed class InstallationShiftAssignment
{
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid TechnicianUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public string Status { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}

public sealed class InstallationMilestone
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public int SortOrder { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public int WeightPercent { get; set; }
    /// <summary>Safety-critical or required steps that can be neither removed nor skipped.</summary>
    public bool IsMandatory { get; set; }
    public string Status { get; set; } = null!;
    public int ProgressPercent { get; set; }
    public string? Notes { get; set; }
    public int ReworkRound { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public Guid? CompletedByUserId { get; set; }
    public int Revision { get; set; }
}

/// <summary>A verified photo proving milestone work. Never deleted: rework adds new evidence in a new round.</summary>
public sealed class MilestoneEvidence
{
    public Guid Id { get; set; }
    public Guid MilestoneId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid FileId { get; set; }
    public int ReworkRound { get; set; }
    public string? Caption { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>An acceptance round: the leader's request and the customer's immutable decision.</summary>
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

/// <summary>Equipment actually installed (no invented data: entered by the leader from the real devices).</summary>
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

public sealed class InstallationEvent
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid ActorUserId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class InstallationStatuses
{
    public const string Planning = "PLANNING";
    public const string Scheduled = "SCHEDULED";
    public const string InProgress = "IN_PROGRESS";
    public const string PendingCustomerAcceptance = "PENDING_CUSTOMER_ACCEPTANCE";
    public const string ReworkRequired = "REWORK_REQUIRED";
    public const string Completed = "COMPLETED";
    public static readonly IReadOnlyList<string> All = [Planning, Scheduled, InProgress, PendingCustomerAcceptance, ReworkRequired, Completed];
}

public static class MilestoneStatuses
{
    public const string NotStarted = "NOT_STARTED";
    public const string InProgress = "IN_PROGRESS";
    public const string Completed = "COMPLETED";
}

public static class AcceptanceDecisions
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string ReworkRequested = "REWORK_REQUESTED";
}

public static class ChecklistStatuses
{
    public const string Pending = "PENDING";
    public const string Satisfied = "SATISFIED";
    public const string NotApplicable = "NOT_APPLICABLE";
}

public static class AssetTypes
{
    public static readonly IReadOnlyList<string> All = ["PANEL", "INVERTER", "BATTERY", "METER", "OTHER"];
}

public static class InstallationErrorCodes
{
    public const string ProjectNotFound = "INSTALLATION_PROJECT_NOT_FOUND";
    public const string ContractNotPaid = "INSTALLATION_CONTRACT_NOT_PAID";
    public const string ProjectExists = "INSTALLATION_PROJECT_EXISTS";
    public const string InvalidState = "INSTALLATION_INVALID_STATE";
    public const string InvalidTeam = "INSTALLATION_INVALID_TEAM";
    public const string InvalidShifts = "INSTALLATION_INVALID_SHIFTS";
    public const string ScheduleConflict = "INSTALLATION_SCHEDULE_CONFLICT";
    public const string NotLeader = "INSTALLATION_NOT_LEADER";
    public const string MilestoneNotFound = "INSTALLATION_MILESTONE_NOT_FOUND";
    public const string EvidenceRequired = "INSTALLATION_EVIDENCE_REQUIRED";
    public const string MilestonesIncomplete = "INSTALLATION_MILESTONES_INCOMPLETE";
    public const string ChecklistIncomplete = "INSTALLATION_CHECKLIST_INCOMPLETE";
    public const string MilestonesLocked = "INSTALLATION_MILESTONES_LOCKED";
    public const string InvalidMilestones = "INSTALLATION_INVALID_MILESTONES";
    public const string AssetsRequired = "INSTALLATION_ASSETS_REQUIRED";
    public const string DuplicateSerial = "INSTALLATION_DUPLICATE_SERIAL";
    public const string StaleRevision = "INSTALLATION_STALE_REVISION";
}

/// <summary>Default plan copied into each new project (projects never share a mutable template).</summary>
public static class DefaultInstallationPlan
{
    public static readonly IReadOnlyList<(string Code, string Name, int Weight, bool Mandatory)> Milestones =
    [
        ("PREPARATION", "Preparation", 10, false),
        ("MOUNTING_STRUCTURE", "Mounting Structure", 20, false),
        ("PANEL_INSTALLATION", "Solar Panel Installation", 25, false),
        ("INVERTER_INSTALLATION", "Inverter Installation", 15, false),
        ("ELECTRICAL_WIRING", "Electrical Wiring", 15, true),
        ("TESTING_COMMISSIONING", "Testing and Commissioning", 10, true),
        ("ACCEPTANCE_PREPARATION", "Acceptance Preparation", 5, true),
    ];

    /// <summary>Not every site needs every approval: items start not required; the manager decides applicability.</summary>
    public static readonly IReadOnlyList<(string Code, string Title)> Checklist =
    [
        ("GRID_CONNECTION_APPROVAL", "Grid connection / EVN approval"),
        ("FIRE_SAFETY_APPROVAL", "Fire safety (PCCC) approval"),
        ("STRUCTURAL_ASSESSMENT", "Roof structural assessment"),
    ];
}
