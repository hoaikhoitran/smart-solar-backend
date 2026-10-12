using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Modules.Scheduling.Contracts.Persistence;

/// <summary>Raised when the database rejects a scheduling write because of a concurrent change.</summary>
public sealed class SchedulingConflictException(string kind, Exception inner) : Exception($"Scheduling conflict: {kind}.", inner)
{
    public const string TechnicianOverlap = "TECHNICIAN_OVERLAP";
    public const string ActiveAppointmentExists = "ACTIVE_APPOINTMENT_EXISTS";
    public const string StaleAppointment = "STALE_APPOINTMENT";

    public string Kind { get; } = kind;
}

public sealed record AppointmentSnapshot(
    Guid Id,
    string AppointmentType,
    Guid? SurveyRequestId,
    Guid PropertySiteId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    Guid ReservedTechnicianUserId,
    Guid? AssignedTechnicianUserId,
    Guid? AssignedBySaleUserId,
    string Status,
    string? CustomerNote,
    DateTimeOffset BookedAt,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? AcknowledgedAt,
    int Revision);

/// <summary>A survey request seen from the scheduling side (owner, sales, location, active appointment).</summary>
public sealed record SurveyRequestSchedulingContext(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    Guid PropertySiteId,
    Guid CustomerUserId,
    Guid? AssignedSaleId,
    SurveyRequestStatus Status,
    GeoPoint? Location,
    AppointmentSnapshot? ActiveAppointment);

public sealed record PropertySiteLocation(Guid PropertySiteId, Guid CustomerUserId, GeoPoint? Location);

public sealed record TechnicianIdentity(Guid UserId, string FullName, bool IsActiveUser, bool HasTechnicianRole, TechnicianProfile? Profile);

/// <summary>Everything a technician needs for an assigned job (only loaded for that technician).</summary>
public sealed record TechnicianJobDetail(
    AppointmentSnapshot Appointment,
    Guid SurveyRequestId,
    SurveyRequestStatus SurveyRequestStatus,
    string CustomerName,
    string? CustomerPhone,
    string? CustomerEmail,
    Guid PropertySiteId,
    string PropertyName,
    string Province,
    string? District,
    string? Ward,
    string? StreetLine,
    decimal? Latitude,
    decimal? Longitude,
    InstallationSurfaceType? InstallationSurfaceType,
    string? SurfaceMaterial,
    Guid PreSurveyId,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    decimal? SurfaceLengthM,
    decimal? SurfaceWidthM,
    decimal? TiltDegree,
    decimal? AzimuthDegree,
    bool? HasObstruction,
    string? ObstaclesJson,
    SelectedSimulationSummary? SelectedSimulation);

public sealed record CustomerSurveyRequestRow(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    Guid PropertySiteId,
    string PropertyName,
    SurveyRequestStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ScheduledAt,
    AppointmentSnapshot? ActiveAppointment,
    string? AssignedTechnicianName);

public sealed record ScheduleEntry(AppointmentSnapshot Appointment, string PropertyName, GeoPoint? Location, TechnicianTravelEstimate? LatestInboundEstimate);

public sealed record TechnicianListItem(Guid UserId, string FullName, string? Phone, bool IsActiveUser, TechnicianProfile? Profile);

/// <summary>An active (not cancelled) appointment that has not ended yet.</summary>
public sealed record UpcomingAppointment(Guid AppointmentId, Guid? SurveyRequestId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status);

/// <summary>A leased recalculation request; <see cref="RequestedAt"/> identifies the request version.</summary>
public sealed record RecalculationLease(Guid TechnicianUserId, DateOnly LocalDate, DateTimeOffset RequestedAt, int Attempts);

public interface ISchedulingUnitOfWork
{
    // ---- Reads ----
    Task<PropertySiteLocation?> GetPropertySiteAsync(Guid propertySiteId, CancellationToken cancellationToken);
    Task<SurveyRequestSchedulingContext?> GetSurveyRequestContextAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Database prefilter: active users holding the TECHNICIAN role with an active scheduling profile,
    /// plus their active appointments (with locations) and unavailable periods in the window.
    /// </summary>
    Task<IReadOnlyList<TechnicianDayState>> GetTechnicianDayStatesAsync(
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, Guid? onlyTechnicianId, CancellationToken cancellationToken);

    Task<TechnicianIdentity?> GetTechnicianAsync(Guid userId, CancellationToken cancellationToken);
    Task<AppointmentSnapshot?> GetAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerSurveyRequestRow>> ListCustomerSurveyRequestsAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AppointmentSnapshot>> ListAppointmentHistoryAsync(Guid surveyRequestId, CancellationToken cancellationToken);
    Task<IReadOnlyList<(AppointmentSnapshot Appointment, string PropertyName, string Province, string? District)>> ListTechnicianAppointmentsAsync(
        Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken);
    Task<TechnicianJobDetail?> GetTechnicianJobAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken);
    Task<TechnicianTravelEstimate?> GetLatestInboundEstimateAsync(Guid appointmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ScheduleEntry>> GetTechnicianScheduleAsync(Guid technicianUserId, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<TechnicianListItem>> ListTechniciansAsync(CancellationToken cancellationToken);

    // ---- Writes ----

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (inside the retrying execution
    /// strategy) and commits only when it returns Commit = true. No provider call may happen inside.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken);

    /// <summary>Row-locks the technicians' scheduling profiles (sorted, to avoid deadlocks). PostgreSQL only; SQLite serialises writers.</summary>
    Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken);

    Task<TechnicianAppointment?> FindAppointmentForUpdateAsync(Guid appointmentId, CancellationToken cancellationToken);
    void AddAppointment(TechnicianAppointment appointment);
    void AddTravelEstimates(IEnumerable<TechnicianTravelEstimate> estimates);

    /// <summary>Moves the survey request to Scheduled when it is still claimed by this salesperson.</summary>
    Task<bool> TryMarkSurveyRequestScheduledAsync(Guid surveyRequestId, Guid saleUserId, DateTimeOffset scheduledAt, CancellationToken cancellationToken);

    /// <summary>Saves tracked changes. Throws <see cref="SchedulingConflictException"/> for overlap, duplicate-active or stale-revision conflicts.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    // ---- Route recalculation (durable queue) ----

    /// <summary>Upserts a PENDING request for the technician-day (part of the caller's transaction; call before SaveChanges).</summary>
    Task RequestRecalculationAsync(Guid technicianUserId, DateOnly localDate, string reason, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Atomically takes the lease of a due request; null when not due or leased by someone else.</summary>
    Task<RecalculationLease?> TryLeaseRecalculationAsync(Guid technicianUserId, DateOnly localDate, DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken);

    /// <summary>Deletes the request when it was not re-requested meanwhile; otherwise only releases the lease.</summary>
    Task CompleteRecalculationAsync(RecalculationLease lease, CancellationToken cancellationToken);

    /// <summary>Marks the request FAILED with a retry time (unless it was re-requested meanwhile, which keeps it PENDING).</summary>
    Task FailRecalculationAsync(RecalculationLease lease, string error, DateTimeOffset nextAttemptAtUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid TechnicianUserId, DateOnly LocalDate)>> ListDueRecalculationsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    Task<TechnicianScheduleRecalculation?> GetRecalculationAsync(Guid technicianUserId, DateOnly localDate, CancellationToken cancellationToken);

    /// <summary>Marks every current estimate of these appointments as superseded (history only from now on).</summary>
    Task SupersedeEstimatesAsync(IReadOnlyCollection<Guid> appointmentIds, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    // ---- History / notifications ----
    void AddAppointmentEvent(TechnicianAppointmentEvent appointmentEvent);
    void AddNotification(SchedulingNotification notification);
    Task<IReadOnlyList<TechnicianAppointmentEvent>> ListAppointmentEventsAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    // ---- Technician configuration ----
    Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingAppointmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<TechnicianWorkingPeriod>> GetWorkingPeriodsAsync(Guid technicianUserId, CancellationToken cancellationToken);

    /// <summary>Replaces the technician's custom weekly periods (part of the caller's transaction).</summary>
    Task ReplaceWorkingPeriodsAsync(Guid technicianUserId, IReadOnlyList<TechnicianWorkingPeriod> periods, CancellationToken cancellationToken);

    Task<IReadOnlyList<TechnicianUnavailablePeriod>> ListUnavailablePeriodsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken);
    Task<TechnicianUnavailablePeriod?> FindUnavailablePeriodAsync(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken);

    // ---- Admin ----
    Task<TechnicianProfile?> FindProfileForUpdateAsync(Guid userId, CancellationToken cancellationToken);
    void AddProfile(TechnicianProfile profile);
    void AddUnavailablePeriod(TechnicianUnavailablePeriod period);
    Task<bool> RemoveUnavailablePeriodAsync(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken);
}
