namespace SmartSolar.Api.Contracts.AfterSales;

// ---- Customer
public sealed record CreateFaultRequest(Guid ProjectId, string? Description, Guid? SuspectedAssetId, IReadOnlyList<Guid>? EvidenceFileIds,
    Guid? WarrantySourceRequestId, string? DisasterType, DateTimeOffset? IncidentOccurredAt);
public sealed record ReasonRequest(string? Reason);
public sealed record CompletionDecisionRequest(bool Accept, string? Reason, IReadOnlyList<Guid>? EvidenceFileIds);
public sealed record ReconsiderationRequest(string? Note, IReadOnlyList<Guid>? EvidenceFileIds);
public sealed record FaultFromMaintenanceRequest(string? Description);
public sealed record PreferredTimeRequest(DateTimeOffset PreferredStartsAt, string? Note);
public sealed record OneTimeMaintenanceRequest(Guid ProjectId, DateTimeOffset PreferredStartsAt, string? Note);
public sealed record PurchaseRequest(Guid ContractId, Guid? MaintenancePackageId, Guid? DisasterPackageId, bool IncludeContract);

// ---- Technician
public sealed record VisitResponseRequest(bool Accept, string? Reason);
public sealed record EvidenceRequest(Guid FileId, string? Caption);
public sealed record DiagnosedItemRequest(Guid? AssetId, string? Description, string? Diagnosis, string? RequiredParts);
/// <param name="StayOnSite">Keep the visit open to repair during the same visit once the work is authorized.</param>
public sealed record InspectionReportRequest(string? Report, bool NoFaultFound, IReadOnlyList<DiagnosedItemRequest>? Items, bool StayOnSite = false);
public sealed record ContinueRepairRequest(int EstimatedMinutes, bool PartsAvailable);
public sealed record FinishOnSiteRequest(string? Note);
public sealed record ItemResultRequest(Guid ItemId, string? WorkResult, string? PartsUsed);
public sealed record ReplacementRequest(Guid ReplacedAssetId, string? Manufacturer, string? Model, string? SerialNumber, string? Specifications);
public sealed record WorkReportRequest(string? Report, IReadOnlyList<ItemResultRequest>? Results, IReadOnlyList<ReplacementRequest>? Replacements, string? FindingNote);
public sealed record AbsenceRequest(string? Note);

// ---- Sales
public sealed record QuotationLineRequest(Guid? ItemId, string? Description, decimal PartsAmount, decimal LaborAmount);
public sealed record IssueQuotationRequest(IReadOnlyList<QuotationLineRequest>? Lines, int? RepairWarrantyMonths, string? Note);
public sealed record FollowUpRequest(DateTimeOffset ContactedAt, string? Result, string? Note, string? Outcome);

// ---- Manager
public sealed record ScheduleVisitRequest(Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string? Reason, int ExpectedRevision,
    bool CrossZoneAuthorized = false, bool CompanyCaused = false);
public sealed record CoverageDecisionRequest(Guid ItemId, string? Coverage, string? Reason);
public sealed record CoverageRequest(IReadOnlyList<CoverageDecisionRequest>? Decisions);
public sealed record DecisionRequest(bool Approve, string? Reason);
public sealed record DuplicateRequest(Guid OriginalRequestId, string? Reason);
public sealed record DisputeReviewRequest(bool RequireRework, string? Note);
/// <summary>Approval cancels unpaid work only; paid work answers 409 AFTER_SALES_PAID_CANCELLATION_UNSUPPORTED (refunds are not handled in this system).</summary>
public sealed record CancellationDecisionRequest(bool Approve, string? Note);
public sealed record StopWorkRequest(string? Reason);
public sealed record AssetCorrectionRequest(string? Manufacturer, string? Model, string? SerialNumber, string? PositionCode, string? Specifications, string? Reason);
/// <summary>Only a rejection is possible: a paid package cannot be cancelled without a refund decision (409 AFTER_SALES_PAID_CANCELLATION_UNSUPPORTED).</summary>
public sealed record PackageCancellationDecisionRequest(bool Approve, string? Note);
public sealed record AssignTaskRequest(Guid SalesUserId, string? Reason);

// ---- Admin
/// <summary>
/// Cover picture: <c>imageUrl</c> (https link, or the stored address of an earlier upload) or <c>imageFileId</c> (a VERIFIED PACKAGE_IMAGE file the
/// caller uploaded; the API stores its public address). Both empty = no picture.
/// </summary>
public sealed record PackageRequest(string? Kind, string? Name, string? Description, decimal Price, int DurationMonths, IReadOnlyList<int>? VisitMonths,
    IReadOnlyList<string>? CoveredDisasterTypes, decimal? CoveragePercent, decimal? PerIncidentLimit, decimal? TermLimit, bool IsActive, int Revision = 0,
    string? ImageUrl = null, Guid? ImageFileId = null);
public sealed record WarrantyOptionRequest(string? Kind, int Months, bool IsActive, bool IsDefault);

/// <summary>Verified manufacturer-warranty activation: ISO 8601 date with offset, a verified PROJECT_DOCUMENT file, and a reason.</summary>
public sealed record WarrantyActivationRequest(string? ActivatedAt, Guid? EvidenceFileId, string? Reason);
