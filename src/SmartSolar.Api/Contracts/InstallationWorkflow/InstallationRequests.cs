namespace SmartSolar.Api.Contracts.InstallationWorkflow;

/// <summary>
/// Assign / reassign / reschedule the responsible technician. Times are ISO-8601 instants with offset (UTC+7 in practice).
/// <c>reason</c> is required when the project already has a technician or has started. <c>expectedRevision</c> is the project revision.
/// A technician outside the site's operational zone (candidates flagged <c>crossZone</c>) needs <c>crossZoneAuthorized</c> = true and a
/// <c>reason</c> (recorded as a cross-zone authorization); otherwise 409 CROSS_ZONE_AUTHORIZATION_REQUIRED.
/// </summary>
public sealed record AssignTechnicianRequest(Guid TechnicianUserId, DateTimeOffset? ScheduledStartsAt, DateTimeOffset? ScheduledEndsAt, string? Reason, int? ExpectedRevision,
    bool? CrossZoneAuthorized = null);

public sealed record CancelInstallationRequest(string? Reason, int? ExpectedRevision);

/// <summary>Manager/Admin returns a completion report waiting for the customer to rework: <c>reason</c> required (≤500).</summary>
public sealed record ReturnToReworkRequest(string? Reason, int? ExpectedRevision);

/// <summary>Manager approval of the installed equipment's differences from the contract (reason required).</summary>
public sealed record SubstitutionApprovalRequest(string? Reason, int? ExpectedRevision);

public sealed record ChecklistRequest(int Revision, bool IsRequired, string? Status, Guid? EvidenceFileId, string? Note);

/// <summary>Technician accept: <c>expectedRevision</c> is optional.</summary>
public sealed record AcceptAssignmentRequest(int? ExpectedRevision);

public sealed record DeclineAssignmentRequest(string? Reason, int? ExpectedRevision);

public sealed record EvidenceRequest(Guid FileId, string? Caption);

/// <summary>
/// One serialised device (<c>serialNumber</c> set, <c>quantity</c> omitted or 1) or one lot of identical units of a catalog product confirmed by
/// count (<c>serialNumber</c> omitted, <c>productId</c> and <c>quantity</c> 1–10000 required). Serial numbers are never invented for a lot.
/// </summary>
public sealed record AssetRequest(string? AssetType, Guid? ProductId, string? Manufacturer, string? Model, string? SerialNumber, string? PositionCode, int? WarrantyMonths,
    int? Quantity = null);

public sealed record AssetsRequest(IReadOnlyList<AssetRequest>? Assets);

public sealed record CompletionReportRequest(string? CompletionReport, int? ExpectedRevision);

/// <summary>decision: ACCEPT or REQUEST_REWORK (reason required).</summary>
/// <summary>
/// Manager/Admin internal acceptance of a reported completion: <c>note</c> = basis of the acceptance (required, ≤ 500), <c>evidenceFileId</c> = optional
/// VERIFIED PROJECT_DOCUMENT (e.g. handover minutes signed on site).
/// </summary>
/// <summary>Customer acceptance: ACCEPT, REQUEST_REWORK or REJECT (reason required for the last two).</summary>
public sealed record CustomerAcceptanceRequest(string? Decision, string? Reason, IReadOnlyList<Guid>? EvidenceFileIds, int? ExpectedRevision);

/// <summary>
/// The installation date the technician agreed with the customer. <c>technicianUserId</c> is used only for a Manager/Admin; <c>reason</c> is
/// required to change an existing schedule; <c>expectedRevision</c> = the live schedule's revision (optional).
/// </summary>
public sealed record InstallationScheduleRequest(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? CustomerAgreementNote, string? Reason,
    int? ExpectedRevision, Guid? TechnicianUserId = null);

public sealed record InstallationScheduleReasonRequest(string? Reason);

public sealed record InstallationScheduleChangeRequest(string? Note);

/// <summary>30/70 plan: ONLINE (PayOS QR / link) or CASH (a Manager/Admin confirms the receipt).</summary>
public sealed record InstallmentPaymentRequest(string? Method);

/// <summary>
/// Technician cash receipt (2026-11-27): the open CASH payment it belongs to, the number on the physical receipt, the technician's VERIFIED
/// PAYMENT_EVIDENCE photo, when the cash was taken (optional, default now; never in the future) and an optional note.
/// </summary>
public sealed record CashReceiptRequest(Guid? PaymentAttemptId, string? ReceiptNumber, Guid? EvidenceFileId, DateTimeOffset? CollectedAt, string? Note);

/// <summary>Number of pages of the printed contract (1–50), optional local signing date and note.</summary>
public sealed record StartSignatureSetRequest(int? ExpectedPageCount, DateOnly? SignedOn, string? Note);

/// <summary>A VERIFIED SIGNED_CONTRACT_PAGE file registered by the caller.</summary>
public sealed record SignedPageRequest(Guid? FileId);

public sealed record FinalizeSignatureSetRequest(int? ExpectedRevision);

/// <summary>Collection activity: kind CONTACT | NEGOTIATION | AGREEMENT | NOTE.</summary>
public sealed record CollectionActivityRequest(string? Kind, string? Channel, string? Summary, DateOnly? PromisedPaymentDate, IReadOnlyList<Guid>? EvidenceFileIds,
    int? ExpectedRevision);

public sealed record CollectionDecisionRequest(string? Reason, int? ExpectedRevision);
