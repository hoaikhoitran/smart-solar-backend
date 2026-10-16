namespace SmartSolar.Api.Contracts.InstallationWorkflow;

/// <summary>A shared team shift: local date (yyyy-MM-dd) and local times (HH:mm) in Asia/Ho_Chi_Minh.</summary>
public sealed record ShiftRequest(string? Date, string? Start, string? End);

public sealed record SchedulePlanRequest(int? Revision, IReadOnlyList<Guid>? Members, Guid LeaderUserId, IReadOnlyList<ShiftRequest>? Shifts);

public sealed record LeaderRequest(int Revision, Guid LeaderUserId);

public sealed record MilestoneRequest(Guid? MilestoneId, string? Name, string? Description, int WeightPercent);

public sealed record MilestonePlanRequest(int Revision, IReadOnlyList<MilestoneRequest>? Milestones, string? Reason);

public sealed record ChecklistRequest(int Revision, bool IsRequired, string? Status, Guid? EvidenceFileId, string? Note);

public sealed record ProgressRequest(int Revision, int ProgressPercent, string? Notes);

public sealed record EvidenceRequest(Guid FileId, string? Caption);

public sealed record AssetRequest(string? AssetType, Guid? ProductId, string? Manufacturer, string? Model, string? SerialNumber, string? PositionCode, int? WarrantyMonths);

public sealed record AssetsRequest(IReadOnlyList<AssetRequest>? Assets);

public sealed record AcceptanceRequest(string? CompletionReport);

/// <summary>decision: ACCEPT or REQUEST_REWORK (reason required).</summary>
public sealed record AcceptanceDecisionRequest(string? Decision, string? Reason);
