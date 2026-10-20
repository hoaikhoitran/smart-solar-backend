using SmartSolar.Modules.Scheduling.PreSurveyAppointments;

namespace SmartSolar.Modules.Scheduling.Dispatch;

public sealed record PreferredWindowView(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>A proposal as the customer sees it (no internal cross-zone flag, no staff notes).</summary>
public sealed record CustomerProposalView(
    Guid ProposalId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string TechnicianName,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RespondedAt,
    int Revision);

/// <summary>
/// The customer's dispatch request: status, the reason (coverage code), the review deadline staff work towards, the preferred
/// windows, the live proposal (null when none) and, once resolved, the resolution and the booked appointment.
/// </summary>
public sealed record CustomerDispatchView(
    Guid DispatchRequestId,
    Guid SurveyRequestId,
    string Status,
    string Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset ReviewDueAt,
    string? CustomerNote,
    IReadOnlyList<PreferredWindowView> PreferredWindows,
    CustomerProposalView? LiveProposal,
    CustomerProposalView? LatestProposal,
    string? Resolution,
    string? ResolutionReason,
    DateTimeOffset? ResolvedAt,
    Guid? AppointmentId,
    int Revision,
    bool Created);

public sealed record StaffProposalView(
    Guid ProposalId,
    Guid TechnicianUserId,
    string TechnicianName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool CrossZone,
    Guid? CrossZoneAuthorizationId,
    Guid ProposedByUserId,
    string? Note,
    DateTimeOffset CreatedAt,
    int ResponseWindowMinutes,
    int SettingsVersion,
    DateTimeOffset ExpiresAt,
    string Status,
    DateTimeOffset? RespondedAt,
    string? FailureReason,
    int Revision);

public sealed record DispatchEventView(Guid EventId, string EventType, Guid? ActorUserId, Guid? ProposalId, string? Detail, DateTimeOffset CreatedAt);

public sealed record CrossZoneAuthorizationView(Guid AuthorizationId, Guid TechnicianUserId, Guid? TargetZoneId, string Reason, Guid AuthorizedByUserId, DateTimeOffset AuthorizedAt);

/// <summary>
/// SLA indicators of a request. <see cref="Overdue"/> is live: OPEN (waiting for staff) and now ≥ review_due_at. The stored
/// <see cref="OverdueAt"/> / <see cref="EscalatedAt"/> / reminders are what the worker recorded. All deadlines are snapshots.
/// </summary>
public sealed record DispatchSlaView(
    int ReviewSlaMinutes,
    DateTimeOffset ReviewDueAt,
    DateTimeOffset EscalationDueAt,
    int ReminderIntervalMinutes,
    int SettingsVersion,
    bool Overdue,
    DateTimeOffset? OverdueAt,
    bool Escalated,
    DateTimeOffset? EscalatedAt,
    int ReminderCount,
    DateTimeOffset? LastReminderAt);

public sealed record DispatchQueueItemView(
    Guid DispatchRequestId,
    Guid SurveyRequestId,
    Guid PropertySiteId,
    string Status,
    string Reason,
    string CustomerName,
    string PropertyName,
    string Province,
    string? CommuneCode,
    ZoneView? Zone,
    DateTimeOffset CreatedAt,
    DispatchSlaView Sla,
    StaffProposalView? LiveProposal,
    int Revision);

public sealed record DispatchQueuePage(IReadOnlyList<DispatchQueueItemView> Items, int Page, int PageSize, int TotalItems, int TotalPages);

/// <summary>Staff detail: the queue item plus windows, every proposal, cross-zone authorizations, history and the site's coverage now.</summary>
public sealed record DispatchStaffView(
    DispatchQueueItemView Request,
    string? CustomerNote,
    IReadOnlyList<PreferredWindowView> PreferredWindows,
    IReadOnlyList<StaffProposalView> Proposals,
    IReadOnlyList<CrossZoneAuthorizationView> CrossZoneAuthorizations,
    IReadOnlyList<DispatchEventView> Events,
    string? Resolution,
    string? ResolutionReason,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedByUserId,
    Guid? AppointmentId,
    string CurrentCoverageStatus,
    Guid? ReleasedAppointmentId = null);

public sealed record WorkingIntervalView(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

/// <summary>
/// A technician who could be dispatched at the requested time, with KNOWN schedule facts only (no travel time, no distance):
/// zone members first (<see cref="CrossZone"/> = false), then technicians of other zones (cross-zone, needs an explicit
/// authorization). <see cref="Reason"/> is the eligibility code when ineligible; <see cref="RequiresHoldRehoming"/> = eligible
/// once their own pending capacity holds are handed to colleagues (done automatically on assignment).
/// </summary>
public sealed record DispatchCandidateView(
    Guid TechnicianUserId,
    string FullName,
    bool CrossZone,
    bool IsEligible,
    string? Reason,
    bool RequiresHoldRehoming,
    IReadOnlyList<ZoneView> Zones,
    int CommitmentsThatDay,
    int MaxAppointmentsPerDay,
    IReadOnlyList<WorkingIntervalView> WorkingIntervals);

public sealed record DispatchCandidatesView(
    Guid DispatchRequestId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    ZoneView? SiteZone,
    string CoverageStatus,
    IReadOnlyList<DispatchCandidateView> Candidates,
    string Note);
