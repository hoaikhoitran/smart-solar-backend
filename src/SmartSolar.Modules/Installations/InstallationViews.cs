namespace SmartSolar.Modules.Installations;

public sealed record TeamMemberView(Guid TechnicianUserId, string FullName, bool IsLeader, DateTimeOffset AssignedAt);
public sealed record ShiftView(Guid ShiftId, DateOnly Date, string Start, string End, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);
public sealed record EvidenceView(Guid EvidenceId, Guid FileId, int ReworkRound, string? Caption, DateTimeOffset AddedAt);
public sealed record MilestoneView(Guid MilestoneId, int SortOrder, string Code, string Name, string? Description, int WeightPercent, bool IsMandatory,
    string Status, int ProgressPercent, string? Notes, int ReworkRound, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, int Revision,
    IReadOnlyList<EvidenceView> Evidence);
public sealed record AcceptanceView(int Round, DateTimeOffset RequestedAt, string CompletionReport, string Decision, DateTimeOffset? DecidedAt, string? Reason);
public sealed record AssetView(Guid AssetId, string AssetType, Guid? ProductId, string Manufacturer, string Model, string SerialNumber, string? PositionCode, int? WarrantyMonths);
public sealed record ChecklistItemView(Guid ItemId, string Code, string Title, bool IsRequired, string Status, Guid? EvidenceFileId, string? Note, DateTimeOffset? VerifiedAt);
public sealed record InstallationEventView(string EventType, Guid ActorUserId, string? Detail, DateTimeOffset CreatedAt);

public sealed record InstallationProjectView(
    Guid ProjectId, Guid ContractId, Guid SurveyRequestId, Guid CustomerUserId, string Status, int ProgressPercent, DateOnly? PlannedStartDate,
    DateOnly? PlannedEndDate, DateTimeOffset? StartedAt, DateTimeOffset? AcceptanceRequestedAt, DateTimeOffset? CompletedAt, DateTimeOffset? HandoverAt,
    int Revision, IReadOnlyList<TeamMemberView> Team, IReadOnlyList<ShiftView> Shifts, IReadOnlyList<MilestoneView> Milestones,
    IReadOnlyList<AcceptanceView> Acceptances, IReadOnlyList<AssetView> Assets, IReadOnlyList<ChecklistItemView>? Checklist,
    IReadOnlyList<InstallationEventView>? Events);

public sealed record SchedulePreviewView(bool IsFeasible, IReadOnlyList<ScheduleConflict> Conflicts, IReadOnlyList<PlacementView> Placements);
public sealed record PlacementView(Guid TechnicianUserId, DateOnly Date, string Start, string End, string InboundOrigin, int? InboundTravelSeconds,
    decimal? InboundDistanceMeters, DateTimeOffset? EarliestArrivalAt);

/// <summary>Handover summary: the integration contract for a future warranty module (no warranty system exists yet).</summary>
public sealed record HandoverView(Guid ProjectId, Guid ContractId, DateTimeOffset HandoverAt, IReadOnlyList<HandoverAssetView> Assets);
public sealed record HandoverAssetView(string AssetType, string Manufacturer, string Model, string SerialNumber, string? PositionCode, int? WarrantyMonths, DateOnly? WarrantyEndsOn);
