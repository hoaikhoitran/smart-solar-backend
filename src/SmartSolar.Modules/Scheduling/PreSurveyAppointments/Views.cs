using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>
/// A bookable start time. Local times carry the configured offset (+07:00 by default).
/// <see cref="KnownAvailableTechnicians"/> counts technicians verified to be able to take the slot;
/// it is an exact count only when <see cref="CapacityStatus"/> is EXACT, otherwise a lower bound
/// (the search stopped early, was capped, or some routes could not be verified). Booking always
/// re-validates, so a listed slot can still be taken by someone else first.
/// </summary>
public sealed record SurveySlotView(
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset EndsAtUtc,
    bool CanBook,
    string CapacityStatus,
    int KnownAvailableTechnicians,
    string Status);

public static class CapacityStatuses
{
    public const string Exact = "EXACT";
    public const string AtLeast = "AT_LEAST";
}

public sealed record SurveySlotsView(
    DateOnly Date,
    string TimeZone,
    int AppointmentDurationMinutes,
    IReadOnlyList<SurveySlotView> Slots,
    string RoutingStatus,
    int UnverifiedSlotCount,
    string? Note);

public sealed record BookingConfirmation(
    Guid AppointmentId,
    Guid SurveyRequestId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    bool Created,
    string Message);

/// <summary>Customer-facing appointment view: no internal technician reservation is exposed.</summary>
public sealed record CustomerAppointmentView(
    Guid AppointmentId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    bool TechnicianAssigned,
    string? AssignedTechnicianName,
    string? CustomerNote,
    DateTimeOffset BookedAt,
    bool CanCustomerChange);

public sealed record CustomerSurveyRequestView(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    Guid PropertySiteId,
    string PropertyName,
    string Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ScheduledAt,
    CustomerAppointmentView? Appointment);

public sealed record CustomerAppointmentHistoryItem(Guid AppointmentId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, DateTimeOffset BookedAt);

public sealed record CustomerSurveyRequestDetailView(CustomerSurveyRequestView Request, IReadOnlyList<CustomerAppointmentHistoryItem> History);

/// <summary>Dashboard-ready route estimate (historical estimate at EstimatedAt, not live traffic).</summary>
public sealed record TravelEstimateView(
    string Provider,
    string ProviderVersion,
    string Vehicle,
    string OriginKind,
    Guid? OriginAppointmentId,
    decimal OriginLatitude,
    decimal OriginLongitude,
    decimal DestinationLatitude,
    decimal DestinationLongitude,
    DateTimeOffset DepartureAt,
    DateTimeOffset EstimatedAt,
    decimal? DistanceMeters,
    int? DurationSeconds,
    int SafetyBufferMinutes,
    DateTimeOffset? EstimatedArrivalAt,
    string RouteCalculationStatus,
    TravelCongestionView Congestion,
    string Kind);

/// <summary>
/// Provider congestion data, or an explicit "not returned" state. Never inferred from durations.
/// </summary>
public sealed record TravelCongestionView(string Status, IReadOnlyDictionary<string, int>? SegmentsByLevel, double? HeavyOrWorseMeters,
    IReadOnlyDictionary<string, double>? MetersByLevel);

public sealed record AvailableTechnicianView(
    Guid TechnicianUserId,
    string FullName,
    bool IsCurrentReservation,
    int ActiveAppointmentsThatDay,
    DateTimeOffset EarliestArrivalAt,
    TravelEstimateView Inbound,
    TravelEstimateView? Outbound);

public sealed record TechnicianAvailabilityView(
    Guid SurveyRequestId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    bool MatchesBookedAppointment,
    IReadOnlyList<AvailableTechnicianView> Technicians,
    string RoutingStatus);

public sealed record AssignmentView(
    Guid AppointmentId,
    Guid SurveyRequestId,
    Guid TechnicianUserId,
    string TechnicianName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    string SurveyRequestStatus,
    DateTimeOffset ScheduledAt,
    bool Reassigned,
    bool AcknowledgementRequired);

/// <summary>Result of a Sales reschedule. <see cref="PreviousAppointmentId"/> is the cancelled original (kept as history).</summary>
public sealed record SalesRescheduleView(
    Guid AppointmentId,
    Guid? PreviousAppointmentId,
    Guid SurveyRequestId,
    Guid TechnicianUserId,
    string TechnicianName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    string SurveyRequestStatus,
    DateTimeOffset? ScheduledAt,
    bool Reassigned,
    bool AcknowledgementRequired);

public sealed record TechnicianAppointmentListItem(
    Guid AppointmentId,
    string AppointmentType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string PropertyName,
    string Province,
    string? District,
    DateTimeOffset? AcknowledgedAt);

public sealed record TechnicianJobView(
    Guid AppointmentId,
    string AppointmentType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? AcknowledgedAt,
    string? CustomerNote,
    Guid SurveyRequestId,
    TechnicianJobCustomer Customer,
    TechnicianJobProperty Property,
    TechnicianJobPreSurvey PreSurvey,
    SelectedSimulationSummary? SelectedSimulation,
    string? SimulationDetailPath,
    TravelEstimateView? InboundTravelEstimate);

public sealed record TechnicianJobCustomer(string FullName, string? Phone, string? Email);

public sealed record TechnicianJobProperty(
    Guid PropertySiteId, string Name, string Province, string? District, string? Ward, string? StreetLine,
    decimal? Latitude, decimal? Longitude, string? InstallationSurfaceType, string? SurfaceMaterial);

public sealed record TechnicianJobPreSurvey(
    Guid PreSurveyId, decimal? TotalAreaM2, decimal? UsableAreaM2, decimal? SurfaceLengthM, decimal? SurfaceWidthM,
    decimal? SurfaceTiltDegree, decimal? SurfaceAzimuthDegree, bool? HasObstruction, int ObstacleCount);

public sealed record AcknowledgementView(Guid AppointmentId, string Status, DateTimeOffset AcknowledgedAt, bool AlreadyAcknowledged);

public sealed record ScheduleAppointmentView(
    Guid AppointmentId,
    string AppointmentType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string PropertyName,
    decimal? Latitude,
    decimal? Longitude,
    string ExpectedOriginKind,
    Guid? ExpectedOriginAppointmentId,
    TravelEstimateView? InboundTravel,
    TravelEstimateView? NonCurrentEstimate,
    string RoutingStatus,
    int? SnapshotAgeMinutes,
    bool PotentialConflict);

/// <summary>State of a queued route recalculation for the technician-day (null when none is pending).</summary>
public sealed record RecalculationStateView(string Status, string Reason, DateTimeOffset RequestedAt, int Attempts, DateTimeOffset NextAttemptAt, string? LastError);

/// <summary>One technician-day for dashboards: jobs, estimated travel totals and conflict flags.</summary>
public sealed record TechnicianScheduleView(
    Guid TechnicianUserId,
    DateOnly Date,
    string TimeZone,
    int AppointmentCount,
    decimal EstimatedTravelDistanceMeters,
    int EstimatedTravelSeconds,
    int SafetyBufferMinutesTotal,
    int ConflictCount,
    int RoutesNeedingReview,
    DateTimeOffset EarliestBaseDeparture,
    RecalculationStateView? Recalculation,
    IReadOnlyList<ScheduleAppointmentView> Appointments,
    string Note);
