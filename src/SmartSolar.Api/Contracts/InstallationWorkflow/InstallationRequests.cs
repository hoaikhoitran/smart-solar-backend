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
public sealed record AcceptanceDecisionRequest(string? Decision, string? Reason);
