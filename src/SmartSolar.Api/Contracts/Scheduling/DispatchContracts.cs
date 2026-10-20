namespace SmartSolar.Api.Contracts.Scheduling;

/// <summary>A preferred window: ISO 8601 timestamps with an explicit offset; at most 5, future, at most 24 h each, inside the booking horizon.</summary>
public sealed record PreferredWindowRequest(string? StartsAt, string? EndsAt);

/// <summary>Customer dispatch request for an uncovered / unavailable area. Both fields are optional.</summary>
public sealed record CreateDispatchRequestRequest(IReadOnlyList<PreferredWindowRequest>? PreferredWindows, string? Note);

/// <summary>expectedRevision: the proposal revision the customer saw (optional).</summary>
public sealed record AcceptDispatchProposalRequest(int? ExpectedRevision);

public sealed record DeclineDispatchProposalRequest(string? Reason, int? ExpectedRevision);

public sealed record CancelDispatchRequestRequest(string? Reason);

/// <summary>
/// Manager/Admin: book (assign) or propose a technician at <c>startsAt</c> (ISO 8601 with offset; the end is start + appointment duration).
/// <c>crossZoneAuthorized</c> = true and a <c>reason</c> are required for a technician outside the site's zone; the reason is always
/// required for an assignment. <c>expectedRevision</c> is the dispatch request revision.
/// </summary>
public sealed record DispatchTechnicianRequest(Guid TechnicianUserId, string? StartsAt, bool? CrossZoneAuthorized, string? Reason, int? ExpectedRevision, string? Note = null);

public sealed record WithdrawDispatchProposalRequest(string? Reason);

/// <summary>Unserviceable / close: reason required (at most 500 characters).</summary>
public sealed record ResolveDispatchRequestRequest(string? Reason, int? ExpectedRevision);
