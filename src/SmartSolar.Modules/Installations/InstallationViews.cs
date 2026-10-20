namespace SmartSolar.Modules.Installations;

// Role-scoped views. Staff (Manager/Admin) see everything; the responsible technician sees what they need to do the
// work; the customer sees progress, evidence, assets and acceptance — never the checklist, the audit trail, the
// assignment history or internal ids of technicians.

public sealed record EvidenceView(Guid EvidenceId, Guid FileId, int Round, string? Caption, DateTimeOffset AddedAt);
/// <param name="DecidedBy">CUSTOMER, STAFF (a Manager returned the report to rework) or null while pending.</param>
public sealed record AcceptanceView(int Round, DateTimeOffset RequestedAt, string CompletionReport, string Decision, DateTimeOffset? DecidedAt, string? Reason,
    string? DecidedBy = null);
public sealed record AssetView(Guid AssetId, string AssetType, Guid? ProductId, string Manufacturer, string Model, string SerialNumber, string? PositionCode, int? WarrantyMonths);
public sealed record ChecklistItemView(Guid ItemId, string Code, string Title, bool IsRequired, string Status, Guid? EvidenceFileId, string? Note, DateTimeOffset? VerifiedAt);
public sealed record InstallationEventView(string EventType, Guid ActorUserId, string? FromStatus, string? ToStatus, string? Detail, DateTimeOffset CreatedAt);
public sealed record AssignmentView(Guid AssignmentId, Guid TechnicianUserId, string TechnicianName, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status,
    Guid AssignedByUserId, DateTimeOffset AssignedAt, string? AssignmentReason, DateTimeOffset? RespondedAt, string? ResponseReason, DateTimeOffset? ClosedAt);
public sealed record ResponsibleTechnicianView(Guid TechnicianUserId, string FullName, string AssignmentStatus, DateTimeOffset? AcceptedAt);
public sealed record ZoneSummary(Guid ZoneId, string Code, string Name);

/// <summary>Manager/Admin view of one project.</summary>
public sealed record InstallationStaffView(
    Guid ProjectId, Guid ContractId, Guid SurveyRequestId, Guid CustomerUserId, string Status, ZoneSummary? Zone,
    ResponsibleTechnicianView? ResponsibleTechnician, DateTimeOffset? ScheduledStartsAt, DateTimeOffset? ScheduledEndsAt,
    DateOnly? PlannedStartDate, DateOnly? PlannedEndDate, DateTimeOffset? StartedAt, DateTimeOffset? AcceptanceRequestedAt,
    DateTimeOffset? CompletedAt, DateTimeOffset? HandoverAt, DateTimeOffset? CancelledAt, string? CancellationReason, int CurrentRound, int Revision,
    InstallationSiteInfo? Site, IReadOnlyList<AssignmentView> Assignments, IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<AssetView> Assets,
    IReadOnlyList<AcceptanceView> Acceptances, IReadOnlyList<ChecklistItemView> Checklist, IReadOnlyList<InstallationEventView> Events);

/// <summary>Row of the Manager project list.</summary>
public sealed record InstallationProjectSummary(Guid ProjectId, Guid ContractId, string Status, Guid? ResponsibleTechnicianUserId, string? ResponsibleTechnicianName,
    bool TechnicianAccepted, DateTimeOffset? ScheduledStartsAt, DateTimeOffset? ScheduledEndsAt, Guid? OperationalZoneId, DateTimeOffset CreatedAt, int Revision);

/// <summary>
/// The responsible technician's view. <see cref="CanStart"/>/<see cref="StartBlockedReason"/> replace the staff checklist
/// (the checklist itself is staff-only).
/// </summary>
public sealed record InstallationTechnicianView(
    Guid ProjectId, string Status, string MyAssignmentStatus, Guid AssignmentId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string? ZoneName, InstallationSiteInfo? Site, DateTimeOffset? StartedAt, DateTimeOffset? AcceptanceRequestedAt, DateTimeOffset? CompletedAt,
    int CurrentRound, int Revision, bool CanStart, string? StartBlockedReason,
    IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<AssetView> Assets, IReadOnlyList<AcceptanceView> Acceptances);

/// <summary>The customer's view: the technician's name only once they accepted the job.</summary>
public sealed record InstallationCustomerView(
    Guid ProjectId, Guid ContractId, string Status, DateTimeOffset? ScheduledStartsAt, DateTimeOffset? ScheduledEndsAt, string? TechnicianName,
    DateTimeOffset? StartedAt, DateTimeOffset? AcceptanceRequestedAt, DateTimeOffset? CompletedAt, DateTimeOffset? HandoverAt,
    DateTimeOffset? CancelledAt, int CurrentRound, int Revision,
    IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<AssetView> Assets, IReadOnlyList<AcceptanceView> Acceptances);

/// <summary>A technician of the project's service area and whether they can take the interval (reason code when not).</summary>
/// <summary>
/// A responsible-technician candidate. <see cref="CrossZone"/>: not a member of the site's zone (or the site has no zone); assigning
/// them needs an explicit Manager/Admin cross-zone authorization with a reason (only the zone rule is skipped).
/// </summary>
public sealed record InstallationCandidateView(Guid TechnicianUserId, string FullName, bool IsEligible, string? Reason, bool CrossZone = false);

/// <summary>Handover summary: the integration contract for a future warranty module (no warranty system exists yet).</summary>
public sealed record HandoverView(Guid ProjectId, Guid ContractId, DateTimeOffset HandoverAt, string? TechnicianName, IReadOnlyList<HandoverAssetView> Assets,
    IReadOnlyList<EvidenceView> Evidence, IReadOnlyList<AcceptanceView> Acceptances);
public sealed record HandoverAssetView(string AssetType, Guid? ProductId, string Manufacturer, string Model, string SerialNumber, string? PositionCode,
    int? WarrantyMonths, DateOnly? WarrantyEndsOn);
