namespace SmartSolar.Modules.AfterSales;

// =====================================================================================================================
// Flow 3 — After-Sales Services. All entities of the module live in this file, grouped by business area:
//   1. Admin configuration   (warranty duration options, service package catalog)
//   2. Purchased packages     (subscriptions with an immutable snapshot, maintenance visit entitlements)
//   3. Service requests       (faults, maintenance visits, one-time maintenance; items, visits, evidence, quotations)
//   4. Disaster protection    (claims with reserved / consumed support)
//   5. Sales queue, refunds, audit events and in-app notifications
// Money is VND in decimal(18,0); every timestamp is UTC; every period end is EXCLUSIVE.
// =====================================================================================================================

// ------------------------------------------------------------------------------------------------ 1. configuration

/// <summary>
/// A warranty duration the Admin offers: installation warranty (Sales picks one per contract) or paid-repair warranty (Sales picks
/// one per repair quotation). The chosen value is copied into the contract / quotation, so later changes never apply retroactively.
/// </summary>
public sealed class WarrantyOption
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = null!;
    public int Months { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class WarrantyOptionKinds
{
    public const string Installation = "INSTALLATION";
    public const string Repair = "REPAIR";
    public static readonly IReadOnlyList<string> All = [Installation, Repair];
}

/// <summary>
/// A sellable package: MAINTENANCE (visits in given plan months) or DISASTER_PROTECTION (repair-cost support for covered disaster
/// types). Editing a package affects future purchases only: every purchase stores its own snapshot.
/// </summary>
public sealed class ServicePackage
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int DurationMonths { get; set; }

    /// <summary>MAINTENANCE: plan months of the included visits, comma separated ("6,12"); one visit per listed month.</summary>
    public string? VisitMonths { get; set; }

    /// <summary>DISASTER_PROTECTION: covered disaster types, comma separated ("STORM,FLOOD").</summary>
    public string? CoveredDisasterTypes { get; set; }

    /// <summary>DISASTER_PROTECTION: share of eligible repair cost supported, in percent (0–100).</summary>
    public decimal? CoveragePercent { get; set; }

    public decimal? PerIncidentLimit { get; set; }
    public decimal? TermLimit { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class ServicePackageKinds
{
    public const string Maintenance = "MAINTENANCE";
    public const string DisasterProtection = "DISASTER_PROTECTION";
    public static readonly IReadOnlyList<string> All = [Maintenance, DisasterProtection];
}

public static class DisasterTypes
{
    public const string Storm = "STORM";
    public const string Flood = "FLOOD";
    public const string Lightning = "LIGHTNING";
    public static readonly IReadOnlyList<string> All = [Storm, Flood, Lightning];
}

// ------------------------------------------------------------------------------------------------ 2. purchased packages

/// <summary>
/// One purchased term of a package for one installation (contract + project). The price and benefits are a snapshot taken at
/// purchase. The term starts when payment is verified AND the installation is handed over (a renewal starts when the previous
/// term ends): <see cref="StartsAt"/>/<see cref="EndsAt"/> stay null until then. Only one term of a kind may cover any instant
/// (PostgreSQL exclusion constraint + application check).
/// </summary>
public sealed class ServiceSubscription
{
    public Guid Id { get; set; }
    public Guid CustomerUserId { get; set; }
    public Guid ContractId { get; set; }

    /// <summary>Null while the installation project does not exist yet (package bought together with the contract).</summary>
    public Guid? ProjectId { get; set; }

    public Guid PackageId { get; set; }
    public string Kind { get; set; } = null!;
    public string Status { get; set; } = null!;

    // ---- snapshot (never changes after purchase)
    public string PackageName { get; set; } = null!;
    public decimal Price { get; set; }
    public int DurationMonths { get; set; }
    public string? VisitMonths { get; set; }
    public string? CoveredDisasterTypes { get; set; }
    public decimal? CoveragePercent { get; set; }
    public decimal? PerIncidentLimit { get; set; }
    public decimal? TermLimit { get; set; }

    // ---- term
    public DateTimeOffset? StartsAt { get; set; }
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>DISASTER_PROTECTION: incidents before this instant are not covered (waiting period; none for a continuous renewal).</summary>
    public DateTimeOffset? CoverageStartsAt { get; set; }

    /// <summary>The term this one renews (continuous renewal: starts exactly when the previous term ends).</summary>
    public Guid? RenewsSubscriptionId { get; set; }

    // ---- payment
    public Guid? CurrentPaymentAttemptId { get; set; }
    public DateTimeOffset? CheckoutExpiresAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }

    // ---- disaster support balance of this term (reserved by issued quotations, consumed at customer acceptance)
    public decimal SupportReserved { get; set; }
    public decimal SupportConsumed { get; set; }

    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? ExpiryReminderSentAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Optimistic concurrency token: balance changes, payment and status changes never overwrite each other.</summary>
    public int Revision { get; set; }

    public decimal SupportAvailable => (TermLimit ?? 0) - SupportReserved - SupportConsumed;
}

public static class SubscriptionStatuses
{
    /// <summary>Created at checkout; nothing is granted until PayOS payment is verified.</summary>
    public const string AwaitingPayment = "AWAITING_PAYMENT";

    /// <summary>The checkout expired without a verified payment (a new purchase is needed).</summary>
    public const string CheckoutExpired = "CHECKOUT_EXPIRED";

    /// <summary>Paid; waits for handover and/or its start date.</summary>
    public const string Paid = "PAID";

    public const string Active = "ACTIVE";
    public const string Expired = "EXPIRED";

    /// <summary>Customer asked to cancel before activation; a Manager decides.</summary>
    public const string CancellationRequested = "CANCELLATION_REQUESTED";

    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [AwaitingPayment, CheckoutExpired, Paid, Active, Expired, CancellationRequested, Cancelled];

    /// <summary>States that hold (or will hold) a term: they block overlapping purchases.</summary>
    public static bool HoldsTerm(string status) => status is Paid or Active or CancellationRequested;
}

/// <summary>
/// One included maintenance visit of a MAINTENANCE term, for the plan month it belongs to. Period bounds are calendar-month
/// anniversaries of the term start (start + (month − 1) months … start + month months, end exclusive). A visit is consumed only
/// when the customer accepts the completed maintenance; it never moves to another term.
/// </summary>
public sealed class MaintenanceEntitlement
{
    public Guid Id { get; set; }
    public Guid SubscriptionId { get; set; }
    public int PlanMonth { get; set; }
    public DateTimeOffset PeriodStartsAt { get; set; }
    public DateTimeOffset PeriodEndsAt { get; set; }
    public string Status { get; set; } = null!;

    /// <summary>The booking currently using this visit (null while none).</summary>
    public Guid? CurrentRequestId { get; set; }

    /// <summary>SmartSolar rescheduled a valid booking: the visit may be completed until this instant (after the term end).</summary>
    public DateTimeOffset? ExtendedUntil { get; set; }

    public DateTimeOffset? NotifiedAt { get; set; }

    /// <summary>When the Sales follow-up task was created for this visit (created at most once).</summary>
    public DateTimeOffset? FollowUpCreatedAt { get; set; }

    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class EntitlementStatuses
{
    public const string Available = "AVAILABLE";
    public const string Booked = "BOOKED";
    public const string Consumed = "CONSUMED";

    /// <summary>Sales could not reach the customer for this planned month (the visit stays bookable while the term is active).</summary>
    public const string NotCompleted = "NOT_COMPLETED";

    /// <summary>The term ended without the visit being used.</summary>
    public const string Expired = "EXPIRED";

    /// <summary>A SmartSolar-delayed booking was not completed within the extension window.</summary>
    public const string ExpiredUnfulfilled = "EXPIRED_UNFULFILLED";

    public static readonly IReadOnlyList<string> All = [Available, Booked, Consumed, NotCompleted, Expired, ExpiredUnfulfilled];
}

// ------------------------------------------------------------------------------------------------ 3. service requests

/// <summary>
/// One after-sales job on a handed-over installation, with its own status, visits, quotations, payments and final customer acceptance.
/// Kinds: FAULT (customer-reported problem, possibly a disaster claim), MAINTENANCE (one included visit of a subscription) and
/// ONE_TIME_MAINTENANCE (a single paid maintenance). Several requests of one installation may be open at the same time.
/// </summary>
public sealed class ServiceRequest
{
    public Guid Id { get; set; }
    public string RequestNumber { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid CustomerUserId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid PropertySiteId { get; set; }
    public string Description { get; set; } = null!;
    public Guid? SuspectedAssetId { get; set; }

    /// <summary>The customer's preferred start (maintenance bookings and one-time maintenance); a request, not a confirmed appointment.</summary>
    public DateTimeOffset? PreferredStartsAt { get; set; }

    // ---- warranty facts frozen at submission (the submission time decides eligibility, not internal processing time)
    public DateTimeOffset? InstallationWarrantyEndsAt { get; set; }
    public bool WithinInstallationWarranty { get; set; }

    /// <summary>A previous paid repair whose repair warranty may cover this fault (new request, never a reopened one).</summary>
    public Guid? WarrantySourceRequestId { get; set; }

    public bool WithinRepairWarranty { get; set; }

    // ---- links
    public Guid? MaintenanceEntitlementId { get; set; }
    public Guid? OriginRequestId { get; set; }
    public Guid? DuplicateOfRequestId { get; set; }

    // ---- outcome
    public string? Outcome { get; set; }
    public string? CustomerDecisionReason { get; set; }
    public DateTimeOffset? AcceptanceRequestedAt { get; set; }
    public DateTimeOffset? LastAcceptanceReminderAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    // ---- paid-repair warranty of THIS request (starts at the verified repair-completion report)
    public int? RepairWarrantyMonths { get; set; }
    public DateTimeOffset? RepairWarrantyStartsAt { get; set; }
    public DateTimeOffset? RepairWarrantyEndsAt { get; set; }

    // ---- cancellation
    public DateTimeOffset? CancellationRequestedAt { get; set; }
    public string? CancellationReason { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class ServiceRequestKinds
{
    public const string Fault = "FAULT";
    public const string Maintenance = "MAINTENANCE";
    public const string OneTimeMaintenance = "ONE_TIME_MAINTENANCE";
    public static readonly IReadOnlyList<string> All = [Fault, Maintenance, OneTimeMaintenance];
}

/// <summary>
/// Request lifecycle. A FAULT normally goes SUBMITTED → SCHEDULED (inspection) → IN_PROGRESS → AWAITING_DECISION (diagnosis
/// submitted) → AWAITING_QUOTATION / AWAITING_CUSTOMER (paid items) → READY_FOR_WORK → SCHEDULED (repair) → IN_PROGRESS →
/// PENDING_CUSTOMER_ACCEPTANCE → COMPLETED. A rejection goes to DISPUTED, where a Manager orders rework (REWORK_REQUIRED) or
/// returns it to the customer. Maintenance skips the decision and quotation steps (one-time maintenance has a quotation).
/// </summary>
public static class ServiceRequestStatuses
{
    public const string Submitted = "SUBMITTED";
    public const string Scheduled = "SCHEDULED";
    public const string InProgress = "IN_PROGRESS";
    public const string AwaitingDecision = "AWAITING_DECISION";
    public const string AwaitingQuotation = "AWAITING_QUOTATION";
    public const string AwaitingCustomer = "AWAITING_CUSTOMER";
    public const string ReadyForWork = "READY_FOR_WORK";
    public const string PendingCustomerAcceptance = "PENDING_CUSTOMER_ACCEPTANCE";
    public const string Disputed = "DISPUTED";
    public const string ReworkRequired = "REWORK_REQUIRED";
    public const string Completed = "COMPLETED";

    /// <summary>The visit could not be done (customer absent, maintenance not completed); the visit entitlement is not consumed.</summary>
    public const string NotCompleted = "NOT_COMPLETED";

    public const string Cancelled = "CANCELLED";
    public const string ClosedDuplicate = "CLOSED_DUPLICATE";

    public static readonly IReadOnlyList<string> All =
    [
        Submitted, Scheduled, InProgress, AwaitingDecision, AwaitingQuotation, AwaitingCustomer, ReadyForWork, PendingCustomerAcceptance,
        Disputed, ReworkRequired, Completed, NotCompleted, Cancelled, ClosedDuplicate,
    ];

    public static bool IsFinal(string status) => status is Completed or NotCompleted or Cancelled or ClosedDuplicate;
}

public static class ServiceOutcomes
{
    public const string NoFaultFound = "NO_FAULT_FOUND";
    public const string Repaired = "REPAIRED";
    public const string MaintenanceDone = "MAINTENANCE_DONE";
    public const string CustomerAbsent = "CUSTOMER_ABSENT";
}

/// <summary>
/// One diagnosed problem of a request (a request may have several). The technician records it; the Manager sets its coverage;
/// chargeable items go into a Sales quotation that the customer accepts or declines as a whole.
/// </summary>
public sealed class ServiceItem
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? AssetId { get; set; }
    public string Description { get; set; } = null!;
    public string Diagnosis { get; set; } = null!;

    /// <summary>Parts the job needs (free text: part, quantity, model/specification). There is no inventory module.</summary>
    public string? RequiredParts { get; set; }

    public string Coverage { get; set; } = null!;
    public string Status { get; set; } = null!;

    /// <summary>True when found during repair, after the original quotation (it gets a supplementary quotation).</summary>
    public bool IsSupplementary { get; set; }

    public Guid? QuotationId { get; set; }
    public string? CoverageReason { get; set; }
    public string? WorkResult { get; set; }
    public string? PartsUsed { get; set; }
    public Guid ReportedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class ItemCoverages
{
    /// <summary>Waiting for the Manager's decision.</summary>
    public const string Pending = "PENDING";

    /// <summary>Free under the installation warranty.</summary>
    public const string InstallationWarranty = "INSTALLATION_WARRANTY";

    /// <summary>Free under the warranty of a previous paid repair.</summary>
    public const string RepairWarranty = "REPAIR_WARRANTY";

    /// <summary>Repair cost partly supported by an approved disaster-protection claim; the customer pays the rest.</summary>
    public const string DisasterProtection = "DISASTER_PROTECTION";

    public const string Chargeable = "CHARGEABLE";

    public static readonly IReadOnlyList<string> All = [Pending, InstallationWarranty, RepairWarranty, DisasterProtection, Chargeable];

    /// <summary>Coverages that need a customer quotation.</summary>
    public static bool NeedsQuotation(string coverage) => coverage is Chargeable or DisasterProtection;
}

public static class ItemStatuses
{
    public const string Open = "OPEN";
    public const string Quoted = "QUOTED";

    /// <summary>Free, or quoted and paid / accepted: the technician may do the work.</summary>
    public const string Authorized = "AUTHORIZED";

    public const string Declined = "DECLINED";
    public const string Done = "DONE";
    public static readonly IReadOnlyList<string> All = [Open, Quoted, Authorized, Declined, Done];
}

/// <summary>
/// One technician visit (inspection, repair, maintenance or rework). The technician and interval live in an
/// <c>installation_assignment</c> row with <c>service_visit_id</c> set, so every technician commitment shares the existing overlap
/// constraint, locks and busy-interval checks.
/// </summary>
public sealed class ServiceVisit
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public string Purpose { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid? TechnicianUserId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }

    /// <summary>SmartSolar's reason when it had to move a valid customer booking (company-caused reschedule).</summary>
    public string? CompanyRescheduleReason { get; set; }

    /// <summary>The Manager proposed another time than the customer's preferred one: the customer confirms it before the visit.</summary>
    public bool CustomerConfirmationRequired { get; set; }

    public DateTimeOffset? CustomerConfirmedAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public string? Report { get; set; }

    /// <summary>Server time of the completion report (also the start of a paid-repair warranty).</summary>
    public DateTimeOffset? ReportedAt { get; set; }

    /// <summary>A problem the technician noticed during maintenance (the customer decides whether to open a fault request).</summary>
    public string? FindingNote { get; set; }

    public Guid AssignedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class VisitPurposes
{
    public const string Inspection = "INSPECTION";
    public const string Repair = "REPAIR";
    public const string Maintenance = "MAINTENANCE";
    public const string Rework = "REWORK";
    public static readonly IReadOnlyList<string> All = [Inspection, Repair, Maintenance, Rework];
}

public static class VisitStatuses
{
    public const string Scheduled = "SCHEDULED";
    public const string InProgress = "IN_PROGRESS";
    public const string Reported = "REPORTED";
    public const string CustomerAbsent = "CUSTOMER_ABSENT";
    public const string Declined = "DECLINED";
    public const string Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> All = [Scheduled, InProgress, Reported, CustomerAbsent, Declined, Cancelled];

    public static bool IsOpen(string status) => status is Scheduled or InProgress;

    /// <summary>
    /// The technician reported the diagnosis but stayed on site, waiting for the coverage decision (and payment) to repair during the
    /// same visit. The visit and its reserved technician interval stay open.
    /// </summary>
    public static bool IsWaitingOnSite(ServiceVisit visit)
        => visit.Purpose == VisitPurposes.Inspection && visit.Status == InProgress && visit.Report is not null;
}

/// <summary>A verified SERVICE_EVIDENCE photo of a visit, or a customer's photo of a fault / rejection. Each file is used once.</summary>
public sealed class ServiceEvidence
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? VisitId { get; set; }
    public Guid FileId { get; set; }
    public string? Caption { get; set; }
    public Guid AddedByUserId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>
/// A Sales quotation for the chargeable items of a request (or for a one-time maintenance). Issued quotations are immutable;
/// a change is a new version that supersedes the previous unpaid one. A supplementary quotation covers items found later and
/// never changes an earlier paid one. The customer pays <see cref="CustomerAmount"/> through PayOS before the work starts.
/// </summary>
public sealed class ServiceQuotation
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public int Version { get; set; }
    public bool IsSupplementary { get; set; }
    public Guid? SupersedesQuotationId { get; set; }
    public string Status { get; set; } = null!;
    public Guid SalesUserId { get; set; }

    /// <summary>Lines as JSON: [{ itemId, description, partsAmount, laborAmount }].</summary>
    public string LinesJson { get; set; } = null!;

    public decimal PartsAmount { get; set; }
    public decimal LaborAmount { get; set; }
    public decimal TotalAmount { get; set; }

    /// <summary>Part of the total supported by disaster protection (reserved on the claim and subscription while open).</summary>
    public decimal DisasterSupportAmount { get; set; }

    public decimal CustomerAmount { get; set; }
    public Guid? DisasterClaimId { get; set; }
    public int? RepairWarrantyMonths { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public Guid? CurrentPaymentAttemptId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class QuotationStatuses
{
    public const string Issued = "ISSUED";

    /// <summary>Accepted, payment pending (or nothing to pay).</summary>
    public const string Accepted = "ACCEPTED";

    public const string Paid = "PAID";
    public const string Rejected = "REJECTED";
    public const string Superseded = "SUPERSEDED";
    public const string Expired = "EXPIRED";

    /// <summary>The request was cancelled before the paid work (a refund may follow).</summary>
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlyList<string> All = [Issued, Accepted, Paid, Rejected, Superseded, Expired, Cancelled];

    public static bool IsOpen(string status) => status is Issued or Accepted;
}

// ------------------------------------------------------------------------------------------------ 4. disaster protection

/// <summary>
/// A disaster-protection claim of a FAULT request, tied for good to the subscription term that covered the incident (also after
/// that term expires or is renewed). Support is reserved when a quotation with covered items is issued and consumed at customer
/// acceptance; reserved + consumed never exceeds the per-incident limit (check constraint) nor the term limit.
/// </summary>
public sealed class DisasterClaim
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public string DisasterType { get; set; } = null!;
    public DateTimeOffset IncidentOccurredAt { get; set; }
    public string Status { get; set; } = null!;

    /// <summary>Why the claim cannot be processed (no covering term, type not covered, waiting period), or the Manager's reason.</summary>
    public string? DecisionReason { get; set; }

    public Guid? DecidedByUserId { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    /// <summary>The customer may ask once for reconsideration with additional evidence.</summary>
    public DateTimeOffset? ReconsiderationRequestedAt { get; set; }

    public string? ReconsiderationNote { get; set; }
    public string? FirstDecisionReason { get; set; }

    // ---- snapshot of the covering term's benefits
    public decimal CoveragePercent { get; set; }
    public decimal PerIncidentLimit { get; set; }

    public decimal SupportReserved { get; set; }
    public decimal SupportConsumed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class ClaimStatuses
{
    /// <summary>No active term covered the incident (or the type, or the waiting period): it cannot be approved.</summary>
    public const string NotEligible = "NOT_ELIGIBLE";

    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string ReconsiderationRequested = "RECONSIDERATION_REQUESTED";
    public static readonly IReadOnlyList<string> All = [NotEligible, Submitted, Approved, Rejected, ReconsiderationRequested];
}

// ------------------------------------------------------------------------------------------------ 5. sales, refunds, audit

/// <summary>
/// Shared Sales work queue item. Every active Sales user sees OPEN tasks; the first to claim owns it (revision-checked). A Manager
/// may assign an open task or reassign one with a reason (history in <see cref="AfterSalesEvent"/>). No employee SLA timers.
/// </summary>
public sealed class SalesTask
{
    public Guid Id { get; set; }
    public string TaskType { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid? RequestId { get; set; }
    public Guid? EntitlementId { get; set; }
    public Guid CustomerUserId { get; set; }
    public Guid? AssignedSalesUserId { get; set; }
    public DateTimeOffset? ClaimedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Outcome { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class SalesTaskTypes
{
    public const string RepairQuotation = "REPAIR_QUOTATION";
    public const string MaintenanceQuotation = "MAINTENANCE_QUOTATION";
    public const string MaintenanceFollowUp = "MAINTENANCE_FOLLOW_UP";
    public const string AcceptanceFollowUp = "ACCEPTANCE_FOLLOW_UP";
    public static readonly IReadOnlyList<string> All = [RepairQuotation, MaintenanceQuotation, MaintenanceFollowUp, AcceptanceFollowUp];
}

public static class SalesTaskStatuses
{
    public const string Open = "OPEN";
    public const string Claimed = "CLAIMED";
    public const string InProgress = "IN_PROGRESS";
    public const string Done = "DONE";
    public const string Cancelled = "CANCELLED";
    public static readonly IReadOnlyList<string> All = [Open, Claimed, InProgress, Done, Cancelled];

    public static bool IsOpen(string status) => status is Open or Claimed or InProgress;
}

public static class FollowUpOutcomes
{
    public const string Booked = "BOOKED";
    public const string WillBookLater = "WILL_BOOK_LATER";
    public const string Declined = "DECLINED";
    public const string Unreachable = "UNREACHABLE";
    public static readonly IReadOnlyList<string> All = [Booked, WillBookLater, Declined, Unreachable];
}

/// <summary>
/// A manual refund: requested (by the system or a cancellation), approved by a Manager, then executed outside the system and
/// recorded by an Admin. Approval alone is never a refund. PayOS refunds are not called automatically.
/// </summary>
public sealed class RefundRecord
{
    public Guid Id { get; set; }
    public Guid CustomerUserId { get; set; }
    public Guid? PaymentAttemptId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? QuotationId { get; set; }
    public Guid? RequestId { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public string Reason { get; set; } = null!;
    public string Status { get; set; } = null!;
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public string? DecisionNote { get; set; }
    public Guid? RecordedByUserId { get; set; }

    /// <summary>When the money was actually returned (the reporting date of the refund).</summary>
    public DateTimeOffset? RefundedAt { get; set; }

    public string? RefundReference { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public int Revision { get; set; }
}

public static class RefundStatuses
{
    public const string PendingApproval = "PENDING_APPROVAL";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Refunded = "REFUNDED";
    public static readonly IReadOnlyList<string> All = [PendingApproval, Approved, Rejected, Refunded];
}

/// <summary>
/// Audit trail of the module: request and subscription transitions, decisions, reassignments, configuration changes and
/// equipment corrections (old and new values in <see cref="Detail"/>). Never updated or deleted.
/// </summary>
public sealed class AfterSalesEvent
{
    public Guid Id { get; set; }
    public string SubjectType { get; set; } = null!;
    public Guid SubjectId { get; set; }
    public string EventType { get; set; } = null!;
    public Guid? ActorUserId { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class EventSubjects
{
    public const string Request = "REQUEST";
    public const string Subscription = "SUBSCRIPTION";
    public const string Package = "PACKAGE";
    public const string WarrantyOption = "WARRANTY_OPTION";
    public const string SalesTask = "SALES_TASK";
    public const string Refund = "REFUND";
    public const string Asset = "ASSET";
    public const string Claim = "CLAIM";
}

/// <summary>An in-app notification (customer or staff). Delivery channels other than the app are out of scope.</summary>
public sealed class AfterSalesNotification
{
    public Guid Id { get; set; }
    public Guid RecipientUserId { get; set; }
    public string Type { get; set; } = null!;
    public string Message { get; set; } = null!;
    public Guid? RequestId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

public static class AfterSalesErrorCodes
{
    public const string ProjectNotFound = "AFTER_SALES_PROJECT_NOT_FOUND";
    public const string NotHandedOver = "AFTER_SALES_NOT_HANDED_OVER";
    public const string RequestNotFound = "SERVICE_REQUEST_NOT_FOUND";
    public const string ItemNotFound = "SERVICE_ITEM_NOT_FOUND";
    public const string VisitNotFound = "SERVICE_VISIT_NOT_FOUND";
    public const string QuotationNotFound = "SERVICE_QUOTATION_NOT_FOUND";
    public const string PackageNotFound = "SERVICE_PACKAGE_NOT_FOUND";
    public const string SubscriptionNotFound = "SERVICE_SUBSCRIPTION_NOT_FOUND";
    public const string EntitlementNotFound = "MAINTENANCE_ENTITLEMENT_NOT_FOUND";
    public const string TaskNotFound = "SALES_TASK_NOT_FOUND";
    public const string RefundNotFound = "REFUND_NOT_FOUND";
    public const string AssetNotFound = "AFTER_SALES_ASSET_NOT_FOUND";
    public const string OptionNotFound = "WARRANTY_OPTION_NOT_FOUND";

    public const string ValidationFailed = "AFTER_SALES_VALIDATION_FAILED";
    public const string InvalidState = "AFTER_SALES_INVALID_STATE";
    public const string StaleRevision = "AFTER_SALES_STALE_REVISION";
    public const string ReasonRequired = "AFTER_SALES_REASON_REQUIRED";
    public const string PaymentRequired = "AFTER_SALES_PAYMENT_REQUIRED";
    public const string QuotationExpired = "SERVICE_QUOTATION_EXPIRED";
    public const string BookingTooSoon = "MAINTENANCE_BOOKING_TOO_SOON";
    public const string OutsidePeriod = "MAINTENANCE_OUTSIDE_PERIOD";
    public const string ChangeTooLate = "AFTER_SALES_CHANGE_TOO_LATE";
    public const string TermOverlap = "SERVICE_SUBSCRIPTION_OVERLAP";
    public const string PackageInactive = "SERVICE_PACKAGE_INACTIVE";
    public const string SupportLimitReached = "DISASTER_SUPPORT_LIMIT_REACHED";
    public const string ClaimNotApproved = "DISASTER_CLAIM_NOT_APPROVED";
    public const string ReconsiderationUsed = "DISASTER_RECONSIDERATION_USED";
    public const string DuplicateSerial = "AFTER_SALES_DUPLICATE_SERIAL";
    public const string TechnicianConflict = "AFTER_SALES_TECHNICIAN_UNAVAILABLE";
    public const string CrossZoneAuthorizationRequired = "CROSS_ZONE_AUTHORIZATION_REQUIRED";
    public const string AlreadyClaimed = "SALES_TASK_ALREADY_CLAIMED";
    public const string EvidenceRequired = "AFTER_SALES_EVIDENCE_REQUIRED";
    public const string RepairTimeInsufficient = "AFTER_SALES_REPAIR_TIME_INSUFFICIENT";
    public const string PartsUnavailable = "AFTER_SALES_PARTS_UNAVAILABLE";
}
