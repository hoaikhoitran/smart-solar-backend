using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

/// <summary>Operational zone summary shown on appointments (code is the internal zone code, not an administrative code).</summary>
public sealed record ZoneView(Guid ZoneId, string Code, string Name)
{
    public static ZoneView? From(ZoneRef? zone) => zone is null ? null : new(zone.Id, zone.Code, zone.Name);
}

/// <summary>
/// A bookable start time. Local times carry the configured offset (+07:00 by default).
/// <see cref="KnownAvailableTechnicians"/> counts technicians of the site's zone who could hold the slot;
/// EXACT below <c>Scheduling:MaxReportedSlotCapacity</c>, AT_LEAST when the count was capped. Booking always
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

/// <summary>
/// Slots of one date. <see cref="CoverageStatus"/>: COVERED when slots are listed, otherwise why not (SITE_LOCATION_REQUIRED,
/// NO_ZONE_COVERAGE, ZONE_NOT_SERVICEABLE, NO_ZONE_TECHNICIAN, NO_AVAILABLE_TECHNICIAN, SCHEDULE_CONFLICT).
/// <see cref="DispatchAvailable"/>: the customer may create an uncovered-zone dispatch request instead (never for
/// SITE_LOCATION_REQUIRED / ZONE_NOT_SERVICEABLE / COVERED).
/// </summary>
public sealed record SurveySlotsView(
    DateOnly Date,
    string TimeZone,
    int AppointmentDurationMinutes,
    ZoneView? Zone,
    IReadOnlyList<SurveySlotView> Slots,
    string? Note,
    string CoverageStatus,
    bool DispatchAvailable);

public sealed record BookingConfirmation(
    Guid AppointmentId,
    Guid SurveyRequestId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    string AcceptanceStatus,
    DateTimeOffset? AcceptanceDeadlineAt,
    bool Created,
    string Message);

/// <summary>Customer-facing appointment view: the internal capacity holder is never exposed; the technician name only once confirmed.</summary>
public sealed record CustomerAppointmentView(
    Guid AppointmentId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string AcceptanceStatus,
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

public sealed record AvailableTechnicianView(
    Guid TechnicianUserId,
    string FullName,
    bool IsCapacityHolder,
    bool IsAssigned,
    int CommitmentsThatDay,
    bool RequiresHoldRehoming);

public sealed record IneligibleTechnicianView(Guid TechnicianUserId, string FullName, string Reason);

/// <summary>Eligible technicians for the appointment interval, and the technicians of the zone who are not, with reason codes.</summary>
public sealed record TechnicianAvailabilityView(
    Guid SurveyRequestId,
    Guid AppointmentId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    int Revision,
    ZoneView? Zone,
    IReadOnlyList<AvailableTechnicianView> Technicians,
    IReadOnlyList<IneligibleTechnicianView> Ineligible);

public sealed record AssignmentView(
    Guid AppointmentId,
    Guid SurveyRequestId,
    Guid TechnicianUserId,
    string TechnicianName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    string? AssignmentSource,
    string? AssignmentReason,
    string SurveyRequestStatus,
    DateTimeOffset ScheduledAt,
    bool Reassigned,
    bool Changed,
    int Revision);

/// <summary>Result of a Sales reschedule. <see cref="PreviousAppointmentId"/> is the cancelled original (kept as history).</summary>
public sealed record SalesRescheduleView(
    Guid AppointmentId,
    Guid? PreviousAppointmentId,
    Guid SurveyRequestId,
    Guid? TechnicianUserId,
    string? TechnicianName,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string AppointmentStatus,
    string AcceptanceStatus,
    string SurveyRequestStatus,
    DateTimeOffset? ScheduledAt);

/// <summary>An appointment needing staff: ESCALATED, or still pending after its acceptance deadline.</summary>
public sealed record EscalatedAppointmentView(
    Guid AppointmentId,
    Guid SurveyRequestId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    DateTimeOffset? AcceptanceDeadlineAt,
    DateTimeOffset? EscalatedAt,
    int Revision,
    ZoneView? Zone,
    string CustomerName,
    string PropertyName,
    string Province,
    string? District,
    string? Ward,
    Guid? AssignedSaleId,
    bool OwnedByMe,
    bool HasEligibleTechnician);

public sealed record TechnicianAppointmentListItem(
    Guid AppointmentId,
    string AppointmentType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string? AssignmentSource,
    string PropertyName,
    string Province,
    string? District,
    DateTimeOffset? AssignedAt);

public sealed record TechnicianJobView(
    Guid AppointmentId,
    string AppointmentType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    string? AssignmentSource,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? AcceptedAt,
    string? CustomerNote,
    Guid SurveyRequestId,
    int Revision,
    TechnicianJobCustomer Customer,
    TechnicianJobProperty Property,
    TechnicianJobPreSurvey PreSurvey,
    SelectedSimulationSummary? SelectedSimulation,
    string? SimulationDetailPath);

public sealed record TechnicianJobCustomer(string FullName, string? Phone, string? Email);

public sealed record TechnicianJobProperty(
    Guid PropertySiteId, string Name, string Province, string? District, string? Ward, string? StreetLine,
    decimal? Latitude, decimal? Longitude, string? InstallationSurfaceType, string? SurfaceMaterial);

public sealed record TechnicianJobPreSurvey(
    Guid PreSurveyId, decimal? TotalAreaM2, decimal? UsableAreaM2, decimal? SurfaceLengthM, decimal? SurfaceWidthM,
    decimal? SurfaceTiltDegree, decimal? SurfaceAzimuthDegree, bool? HasObstruction, int ObstacleCount);

/// <summary>
/// An open appointment offered to a technician of the zone: coarse location and survey facts only (no
/// street address or customer contact until accepted). <see cref="CanAccept"/> is advisory: accepting
/// re-checks everything and may still lose to someone faster.
/// </summary>
public sealed record OpenAppointmentView(
    Guid AppointmentId,
    Guid SurveyRequestId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    DateTimeOffset? AcceptanceDeadlineAt,
    int Revision,
    ZoneView? Zone,
    string PropertyName,
    string Province,
    string? District,
    string? Ward,
    string? InstallationSurfaceType,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    bool CanAccept,
    string? Reason,
    bool RequiresHoldRehoming);

public sealed record ReleaseView(Guid AppointmentId, string AppointmentStatus, bool HasNewHolder, string Message);

public sealed record ScheduleAppointmentView(
    Guid AppointmentId,
    string AppointmentType,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    string Status,
    bool IsCapacityHold,
    string PropertyName,
    bool PotentialConflict);

/// <summary>One technician-day: commitments and buffer conflicts (previous end + buffer &gt; next start).</summary>
public sealed record TechnicianScheduleView(
    Guid TechnicianUserId,
    DateOnly Date,
    string TimeZone,
    int AppointmentCount,
    int BufferBetweenAppointmentsMinutes,
    int ConflictCount,
    IReadOnlyList<ScheduleAppointmentView> Appointments,
    string Note);
