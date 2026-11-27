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

    /// <summary>The Manager/Admin who created the project; null when the system created it because the contract became PAID with an agreed schedule.</summary>
    public Guid? ManagerUserId { get; set; }
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

    /// <summary>
    /// The round shown to people: while a report waits for the customer, and after completion, that is the round being judged / the
    /// last round (<see cref="AcceptanceRound"/>); otherwise the round being worked on (<see cref="CurrentRound"/>).
    /// </summary>
    public int DisplayRound => Status is InstallationStatuses.PendingCustomerAcceptance or InstallationStatuses.Completed ? AcceptanceRound : CurrentRound;
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

    /// <summary>
    /// Server time at which the responsible technician reported the work complete on this assignment (2026-11-25). The assignment stays ACCEPTED
    /// while the customer decides (workflow unchanged), but from this instant the unused rest of [<see cref="StartsAt"/>, <see cref="EndsAt"/>) is no
    /// longer busy for scheduling (<see cref="SmartSolar.Modules.Scheduling.Domain.EffectiveBusy"/>). <see cref="StartsAt"/>/<see cref="EndsAt"/> are never
    /// changed. A rework round needs a new assignment (Manager/Admin) before on-site work resumes.
    /// </summary>
    public DateTimeOffset? WorkReportedAt { get; set; }

    /// <summary>
    /// Set for an after-sales visit (Flow 3) of the handed-over project; null for the installation itself. Sharing this table
    /// gives after-sales visits the same overlap constraint, technician locks and busy-interval checks as installation work.
    /// </summary>
    public Guid? ServiceVisitId { get; set; }
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
    /// <summary>Who decided: CUSTOMER (2026-11-19 customer acceptance), MANAGER (return to rework / internal acceptance history), null for older rows.</summary>
    public string? DecidedByRole { get; set; }
    /// <summary>JSON array of VERIFIED ACCEPTANCE_EVIDENCE file ids the customer attached to a rework request.</summary>
    public string? EvidenceFileIds { get; set; }
}

/// <summary>
/// Equipment actually installed (no invented data: entered by the responsible technician from the real devices). A row is either ONE
/// serialised device (<see cref="SerialNumber"/> set, <see cref="Quantity"/> 1) or a LOT of identical units of one catalog product that the
/// technician confirmed by count (<see cref="SerialNumber"/> null, <see cref="Quantity"/> ≥ 1), e.g. "100 × contracted panel". No serial
/// number is ever invented for a lot.
/// </summary>
public sealed class InstalledAsset
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string AssetType { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public string Manufacturer { get; set; } = null!;
    public string Model { get; set; } = null!;

    /// <summary>The device's serial number; null for a lot confirmed by quantity.</summary>
    public string? SerialNumber { get; set; }

    /// <summary>Units this row stands for: 1 for a serialised device, the confirmed count for a lot.</summary>
    public int Quantity { get; set; } = 1;

    public string? PositionCode { get; set; }
    public int? WarrantyMonths { get; set; }
    public Guid RegisteredByUserId { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }

    /// <summary>ACTIVE, PENDING_ACCEPTANCE (a replacement waiting for the customer's acceptance) or REPLACED (kept as history).</summary>
    public string Status { get; set; } = InstalledAssetStatuses.Active;

    /// <summary>The asset this one replaced (replacement lineage, e.g. INV001 → INV002).</summary>
    public Guid? ReplacesAssetId { get; set; }

    /// <summary>The after-sales request whose repair installed this asset.</summary>
    public Guid? ServiceRequestId { get; set; }

    /// <summary>Essential technical specification of a replacement (rating, capacity…).</summary>
    public string? Specifications { get; set; }

    /// <summary>Source of <see cref="WarrantyMonths"/> (<see cref="EquipmentWarrantySources"/>); null when there is no duration.</summary>
    public string? WarrantySource { get; set; }

    /// <summary>
    /// Start and end of the equipment (manufacturer) warranty. Not set: when it starts (commissioning, handover, invoice, activation) has
    /// not been decided, and no date is guessed.
    /// </summary>
    public DateTimeOffset? WarrantyStartsAt { get; set; }

    public DateTimeOffset? WarrantyEndsAt { get; set; }
}

/// <summary>
/// A Manager-approved difference between the installed panels/inverters and the accepted quotation, bound to the exact comparison
/// (<see cref="Fingerprint"/>). The differences are its <see cref="EquipmentSubstitutionItem"/> rows, one per affected product, so any
/// many-to-many change (e.g. 1 contracted inverter → 2 of another model) is represented as is. At most one record per project is active
/// (CUSTOMER_APPROVAL_PENDING or APPROVED); a change of the installed equipment or a customer refusal ends it, never rewrites it.
/// </summary>
public sealed class EquipmentSubstitution
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid ContractId { get; set; }
    /// <summary>The accepted quotation whose immutable lines are the contracted specification.</summary>
    public Guid QuotationId { get; set; }
    /// <summary>Hex fingerprint of the contracted and installed sets (currently 32 characters; up to a full SHA-256).</summary>
    public string Fingerprint { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid ManagerUserId { get; set; }
    public string ManagerReason { get; set; } = null!;
    public DateTimeOffset ManagerApprovedAt { get; set; }
    public Guid? CustomerUserId { get; set; }
    public DateTimeOffset? CustomerApprovedAt { get; set; }
    public DateTimeOffset? CustomerDeclinedAt { get; set; }
    public string? CustomerDeclineReason { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

/// <summary>One product whose installed count differs from the contract (contracted 0 = not contracted; installed 0 = not installed).</summary>
public sealed class EquipmentSubstitutionItem
{
    public Guid Id { get; set; }
    public Guid SubstitutionId { get; set; }
    /// <summary>Null for an installed asset without a catalog product (legacy registrations).</summary>
    public Guid? ProductId { get; set; }
    public string ProductType { get; set; } = null!;
    public string? Name { get; set; }
    public decimal ContractedQuantity { get; set; }
    public int InstalledQuantity { get; set; }
}

public static class EquipmentSubstitutionStatuses
{
    /// <summary>Legacy (before 2026-11-18): Manager approved; the customer had not decided. Now counts as approved (no customer step).</summary>
    public const string CustomerApprovalPending = "CUSTOMER_APPROVAL_PENDING";
    /// <summary>Legacy: Manager and customer approved exactly this comparison.</summary>
    public const string Approved = "APPROVED";
    /// <summary>Legacy: the customer asked for rework instead of agreeing.</summary>
    public const string CustomerDeclined = "CUSTOMER_DECLINED";
    /// <summary>The installed equipment changed afterwards: this approval no longer describes it.</summary>
    public const string Superseded = "SUPERSEDED";
    /// <summary>
    /// Technician-led flow (2026-11-18): a Manager approved exactly this comparison with a reason. Internal, final decision: the customer is
    /// informed (and discussed it with the technician on site) but approves nothing in the app.
    /// </summary>
    public const string InternallyApproved = "INTERNALLY_APPROVED";

    public static readonly IReadOnlyList<string> All = [CustomerApprovalPending, Approved, CustomerDeclined, Superseded, InternallyApproved];

    /// <summary>The project's current substitution decision (at most one per project).</summary>
    public static readonly IReadOnlyList<string> Active = [CustomerApprovalPending, Approved, InternallyApproved];
}

/// <summary>Where an asset's equipment warranty duration comes from.</summary>
public static class EquipmentWarrantySources
{
    /// <summary>The catalog product's warranty, copied when the asset was registered.</summary>
    public const string CatalogProduct = "CATALOG_PRODUCT";
    /// <summary>Registered before the source was recorded (typed by a technician or defaulted): not verified.</summary>
    public const string LegacyUnverified = "LEGACY_UNVERIFIED";

    public static readonly IReadOnlyList<string> All = [CatalogProduct, LegacyUnverified];
}

public static class InstalledAssetStatuses
{
    public const string Active = "ACTIVE";
    public const string PendingAcceptance = "PENDING_ACCEPTANCE";
    public const string Replaced = "REPLACED";
    public static readonly IReadOnlyList<string> All = [Active, PendingAcceptance, Replaced];
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
    /// <summary>
    /// Completion reported, waiting for the INTERNAL acceptance by a Manager/Admin (2026-11-18). The stored value keeps its historical name for
    /// compatibility with existing rows and the live older API; the customer no longer decides anything in this state.
    /// </summary>
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

public static class InstallationLimits
{
    /// <summary>Entries (serialised devices or lots) accepted in one registration.</summary>
    public const int MaxAssetsPerRequest = 500;

    /// <summary>Units one lot may confirm at once (a residential roof carries far fewer; the bound only stops typos such as 1000 instead of 100).</summary>
    public const int MaxLotQuantity = 10_000;
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
    /// <summary>Legacy: the customer accepted the completion in the app (before 2026-11-18).</summary>
    public const string CustomerAccepted = "CUSTOMER_ACCEPTED";
    /// <summary>Legacy: the customer asked for rework in the app (before 2026-11-18).</summary>
    public const string ReworkRequested = "REWORK_REQUESTED";
    /// <summary>A Manager/Admin accepted the reported completion internally (detail: round, basis, evidence file): COMPLETED / handover.</summary>
    public const string InternallyAccepted = "INTERNALLY_ACCEPTED";

    /// <summary>A Manager/Admin returned a completion report to rework (PENDING_CUSTOMER_ACCEPTANCE, i.e. awaiting internal acceptance → REWORK_REQUIRED).</summary>
    public const string ReturnedToRework = "RETURNED_TO_REWORK";
    public const string ChecklistUpdated = "CHECKLIST_UPDATED";
    public const string ProjectCancelled = "PROJECT_CANCELLED";
    /// <summary>Manager approved the installed equipment's differences from the contract (detail "fp:{fingerprint}; reason").</summary>
    public const string SubstitutionApproved = "EQUIPMENT_SUBSTITUTION_APPROVED";
    /// <summary>Legacy: the customer agreed to the approved differences (detail "fp:{fingerprint}").</summary>
    public const string SubstitutionCustomerApproved = "EQUIPMENT_SUBSTITUTION_CUSTOMER_APPROVED";
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
    /// <summary>
    /// Rework was requested after the technician reported completion: the time of the reported assignment was released, so renewed on-site work
    /// needs a new assignment (Manager/Admin assign / reschedule, then the technician accepts) (409).
    /// </summary>
    public const string ReworkScheduleRequired = "INSTALLATION_REWORK_SCHEDULE_REQUIRED";
    /// <summary>No longer returned since 2026-11-17 (starting is not tied to the scheduled dates); kept so clients that map it still compile.</summary>
    public const string TooEarlyToStart = "INSTALLATION_TOO_EARLY_TO_START";

    /// <summary>No longer returned since 2026-11-17: a start after the planned interval is allowed. Kept for client compatibility.</summary>
    public const string ScheduleExpired = "INSTALLATION_SCHEDULE_EXPIRED";
    public const string ChecklistIncomplete = "INSTALLATION_CHECKLIST_INCOMPLETE";
    public const string EvidenceRequired = "INSTALLATION_EVIDENCE_REQUIRED";
    public const string AssetsRequired = "INSTALLATION_ASSETS_REQUIRED";
    public const string DuplicateSerial = "INSTALLATION_DUPLICATE_SERIAL";
    /// <summary>A quantity confirmation (no serial) is invalid: no catalog product, quantity outside 1–<see cref="InstallationLimits.MaxLotQuantity"/>, or a quantity other than 1 together with a serial (400).</summary>
    public const string AssetQuantityInvalid = "INSTALLATION_ASSET_QUANTITY_VALIDATION_FAILED";
    /// <summary>Waiving a prerequisite (not required / not applicable) needs a reason in the note (400).</summary>
    public const string WaiverReasonRequired = "INSTALLATION_CHECKLIST_WAIVER_REASON_REQUIRED";
    /// <summary>An asset names an unknown/deleted catalog product, or a product of another kind (e.g. a PANEL that is an inverter) (400).</summary>
    public const string AssetProductInvalid = "INSTALLATION_ASSET_PRODUCT_VALIDATION_FAILED";
    /// <summary>A mandatory prerequisite cannot be made optional (409).</summary>
    public const string PrerequisiteMandatory = "INSTALLATION_PREREQUISITE_MANDATORY";
    /// <summary>The installed panels / inverters differ from the accepted quotation without a Manager approval of exactly these differences (409).</summary>
    public const string SubstitutionApprovalRequired = "INSTALLATION_SUBSTITUTION_APPROVAL_REQUIRED";
    /// <summary>The customer has not agreed to the approved equipment differences: no handover yet (409).</summary>

    /// <summary>There is no equipment difference to approve (409).</summary>
    public const string NoSubstitution = "INSTALLATION_NO_SUBSTITUTION";
    /// <summary>No approved exemption rule allows this mandatory prerequisite to be NOT_APPLICABLE (409).</summary>
    public const string ExemptionNotAuthorized = "INSTALLATION_PREREQUISITE_EXEMPTION_NOT_AUTHORIZED";
    /// <summary>An exemption of a mandatory prerequisite needs a verified supporting document (400).</summary>
    public const string ExemptionEvidenceRequired = "INSTALLATION_EXEMPTION_EVIDENCE_REQUIRED";
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

    /// <summary>EVN, PCCC and structural assessment are mandatory prerequisites of every installation.</summary>
    public static bool IsMandatory(string code) => Checklist.Any(c => c.Code == code);
}
