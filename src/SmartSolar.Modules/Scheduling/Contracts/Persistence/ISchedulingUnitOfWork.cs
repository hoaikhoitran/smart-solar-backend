using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Zones;

namespace SmartSolar.Modules.Scheduling.Contracts.Persistence;

/// <summary>Raised when the database rejects a scheduling write because of a concurrent change.</summary>
public sealed class SchedulingConflictException(string kind, Exception inner) : Exception($"Scheduling conflict: {kind}.", inner)
{
    /// <summary>PostgreSQL 23P01: the exclusion constraint saw two overlapping holds of one technician.</summary>
    public const string TechnicianOverlap = "TECHNICIAN_OVERLAP";

    /// <summary>23505 on ux_technician_appointment_active_survey_request: a second active appointment for one request.</summary>
    public const string ActiveAppointmentExists = "ACTIVE_APPOINTMENT_EXISTS";

    /// <summary>The revision token changed since the row was read.</summary>
    public const string StaleAppointment = "STALE_APPOINTMENT";

    /// <summary>23505 on another unique index (zone code, zone membership).</summary>
    public const string Duplicate = "DUPLICATE";

    /// <summary>23505 on ux_operational_zone_commune_active: a commune got an active mapping to another zone meanwhile.</summary>
    public const string CommuneAlreadyMapped = "COMMUNE_ALREADY_MAPPED";

    public string Kind { get; } = kind;
}

public sealed record AppointmentSnapshot(
    Guid Id,
    string AppointmentType,
    Guid? SurveyRequestId,
    Guid PropertySiteId,
    Guid? OperationalZoneId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    Guid? ReservedTechnicianUserId,
    Guid? AssignedTechnicianUserId,
    Guid? AssignedBySaleUserId,
    string Status,
    string? CustomerNote,
    Guid BookedByUserId,
    DateTimeOffset BookedAt,
    DateTimeOffset? AcceptanceDeadlineAt,
    DateTimeOffset? EscalatedAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? AssignedAt,
    string? AssignmentSource,
    string? AssignmentReason,
    DateTimeOffset? CompletedAt,
    int Revision);

/// <summary>A survey request seen from the scheduling side (owner, site, active appointment).</summary>
public sealed record SurveyRequestSchedulingContext(
    Guid SurveyRequestId,
    Guid PreSurveyId,
    Guid PropertySiteId,
    Guid CustomerUserId,
    Guid? AssignedSaleId,
    SurveyRequestStatus Status,
    AppointmentSnapshot? ActiveAppointment);

public sealed record PropertySiteInfo(Guid PropertySiteId, Guid CustomerUserId, string? ProvinceCode, string? CommuneCode);

public sealed record ZoneRef(Guid Id, string Code, string Name, bool IsServiceable = true);

/// <summary>
/// Coverage of a site: site → commune_code → the ACTIVE mapping of that commune → its zone, which must be active.
/// <see cref="HasLocation"/> is false when the site has no commune code, or its code is not an active official commune of the
/// site's province (then <see cref="Zone"/> is null). <see cref="Zone"/> is null when no active zone maps the commune; it is set
/// (with <see cref="ZoneRef.IsServiceable"/>) otherwise. <see cref="ZoneIds"/> is the zone as a set (empty or one id) for the
/// eligibility rules and the FOR SHARE lock.
/// </summary>
public sealed record SiteCoverage(string? ProvinceCode, string? CommuneCode, bool HasLocation, ZoneRef? Zone)
{
    public static readonly SiteCoverage NoLocation = new(null, null, false, null);

    public IReadOnlyList<Guid> ZoneIds => Zone is null ? [] : [Zone.Id];

    /// <summary>SITE_LOCATION_REQUIRED, NO_ZONE_COVERAGE, ZONE_NOT_SERVICEABLE or COVERED (the technician-independent part).</summary>
    public string Status => !HasLocation ? CoverageStatuses.SiteLocationRequired
        : Zone is null ? CoverageStatuses.NoZoneCoverage
        : !Zone.IsServiceable ? CoverageStatuses.ZoneNotServiceable
        : CoverageStatuses.Covered;
}

/// <summary>
/// Which technicians to load. <see cref="SchedulableOnly"/> = active account + TECHNICIAN role + scheduling
/// enabled (the database prefilter for candidate searches); false loads <see cref="TechnicianIds"/> whatever
/// their state, so the rules can explain why they are ineligible.
/// <see cref="ZoneIds"/> keeps technicians with an active membership in one of these (active) zones.
/// </summary>
public sealed record TechnicianScope(IReadOnlyCollection<Guid>? TechnicianIds = null, IReadOnlyCollection<Guid>? ZoneIds = null, bool SchedulableOnly = true);

public sealed record TechnicianIdentity(Guid UserId, string FullName, bool IsActiveUser, bool HasTechnicianRole, TechnicianProfile? Profile);

/// <summary>Everything a technician needs for a confirmed job (only loaded for that technician).</summary>
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

public sealed record ScheduleEntry(AppointmentSnapshot Appointment, string PropertyName);

public sealed record TechnicianListItem(Guid UserId, string FullName, string? Phone, bool IsActiveUser, TechnicianProfile? Profile, IReadOnlyList<ZoneRef> Zones);

/// <summary>A non-cancelled commitment of a technician that has not ended yet (pending hold or confirmed job).</summary>
public sealed record UpcomingAppointment(Guid AppointmentId, Guid? SurveyRequestId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, Guid? PropertySiteId = null);

/// <summary>An open appointment as offered to technicians: zone and coarse location only, no customer contact data.</summary>
public sealed record OpenAppointmentRow(
    AppointmentSnapshot Appointment,
    string PropertyName,
    string Province,
    string? District,
    string? Ward,
    InstallationSurfaceType? InstallationSurfaceType,
    decimal? TotalAreaM2,
    decimal? UsableAreaM2,
    ZoneRef? Zone);

/// <summary>An appointment that needs staff (escalated, or pending past its deadline).</summary>
public sealed record EscalatedAppointmentRow(
    AppointmentSnapshot Appointment,
    Guid? AssignedSaleId,
    string CustomerName,
    string PropertyName,
    string Province,
    string? District,
    string? Ward,
    ZoneRef? Zone);

/// <summary>Outcome of <see cref="ISchedulingUnitOfWork.SetSalesOwnerAsync"/>; Updated is also true when the owner already was the target (no change needed).</summary>
public sealed record SalesOwnerUpdate(bool Found, bool Updated, Guid? PreviousSaleId);

/// <summary>Per-zone counts: active commune mappings, active memberships and schedulable members (active account, role, scheduling on).</summary>
public sealed record ZoneStats(Guid ZoneId, int Communes, int Technicians, int SchedulableTechnicians);

/// <summary>
/// Unfinished work: survey appointments (not CANCELLED/COMPLETED) that have not ended yet (open ones only before their start), and
/// installation projects that are not COMPLETED/CANCELLED (Installation Flow only). For a zone it is the work recording the zone;
/// for a commune it is the work on sites located in that commune.
/// </summary>
public sealed record ZoneUsage(int Appointments, int InstallationProjects)
{
    public bool InUse => Appointments > 0 || InstallationProjects > 0;
}

/// <summary><see cref="ZoneUsage"/> of the sites located in one commune.</summary>
public sealed record CommuneUsage(string CommuneCode, int Appointments, int InstallationProjects);

/// <summary>An active commune mapping of a zone with the catalog name of the commune.</summary>
public sealed record ZoneCommuneRow(string CommuneCode, string CommuneName, string UnitType, string ProvinceCode, DateTimeOffset AssignedAt, Guid AssignedByUserId);

/// <summary>An active commune mapping (any zone) with its zone.</summary>
public sealed record CommuneMappingRow(string CommuneCode, Guid ZoneId, string ZoneCode, string ZoneName, bool ZoneIsActive);

/// <summary>An active zone membership with the technician's state.</summary>
public sealed record ZoneMemberRow(Guid TechnicianUserId, string FullName, bool IsActiveUser, bool HasTechnicianRole, bool IsSchedulingActive, DateTimeOffset AssignedAt);

public interface ISchedulingUnitOfWork
{
    // ---- Reads ----
    Task<PropertySiteInfo?> GetPropertySiteAsync(Guid propertySiteId, CancellationToken cancellationToken);

    /// <summary>The site's commune and the active zone mapping it (see <see cref="SiteCoverage"/>); unknown sites have no location.</summary>
    Task<SiteCoverage> GetSiteCoverageAsync(Guid propertySiteId, CancellationToken cancellationToken);

    /// <summary><see cref="GetSiteCoverageAsync"/> for several sites in a constant number of queries (unknown sites map to no location).</summary>
    Task<IReadOnlyDictionary<Guid, SiteCoverage>> GetSiteCoveragesAsync(IReadOnlyCollection<Guid> propertySiteIds, CancellationToken cancellationToken);

    Task<SurveyRequestSchedulingContext?> GetSurveyRequestContextAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Technician-days in the window: commitments (non-cancelled survey appointments they hold, plus
    /// installation assignments when Installation Flow is on), leave, weekly schedule and the active
    /// zones they belong to.
    /// </summary>
    Task<IReadOnlyList<TechnicianDayState>> GetTechnicianDayStatesAsync(
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, TechnicianScope scope, CancellationToken cancellationToken);

    Task<TechnicianIdentity?> GetTechnicianAsync(Guid userId, CancellationToken cancellationToken);
    Task<AppointmentSnapshot?> GetAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CustomerSurveyRequestRow>> ListCustomerSurveyRequestsAsync(Guid customerUserId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AppointmentSnapshot>> ListAppointmentHistoryAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>ACCEPTED, ASSIGNED and COMPLETED appointments of this technician ending after <paramref name="fromUtc"/>.</summary>
    Task<IReadOnlyList<(AppointmentSnapshot Appointment, string PropertyName, string Province, string? District)>> ListTechnicianAppointmentsAsync(
        Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken);

    /// <summary>A confirmed (ACCEPTED/ASSIGNED/COMPLETED) job of this technician, or null.</summary>
    Task<TechnicianJobDetail?> GetTechnicianJobAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken);

    /// <summary>The technician's confirmed jobs on the day; <paramref name="includeHolds"/> adds the pending capacity holds (staff dashboards only).</summary>
    Task<IReadOnlyList<ScheduleEntry>> GetTechnicianScheduleAsync(Guid technicianUserId, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, bool includeHolds, CancellationToken cancellationToken);

    /// <summary>Technicians (TECHNICIAN role, not deleted), optionally only the active members of <paramref name="zoneId"/>, with their active memberships.</summary>
    Task<IReadOnlyList<TechnicianListItem>> ListTechniciansAsync(Guid? zoneId, CancellationToken cancellationToken);

    /// <summary>
    /// Open (PENDING_ACCEPTANCE/ESCALATED) future appointments on sites whose commune is actively mapped to one of
    /// <paramref name="zoneIds"/>; optionally only <paramref name="appointmentId"/>.
    /// </summary>
    Task<IReadOnlyList<OpenAppointmentRow>> ListOpenAppointmentsAsync(IReadOnlyCollection<Guid> zoneIds, DateTimeOffset fromUtc, Guid? appointmentId, CancellationToken cancellationToken);

    /// <summary>ESCALATED appointments plus PENDING_ACCEPTANCE ones past their deadline that have not started yet.</summary>
    Task<IReadOnlyList<EscalatedAppointmentRow>> ListEscalatedAppointmentsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Technicians with an active membership in one of these zones (any account state).</summary>
    Task<IReadOnlyList<Guid>> ListZoneMemberIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);

    /// <summary>The active memberships (technician, zone) in these zones (any account state), in one query.</summary>
    Task<IReadOnlyList<(Guid TechnicianUserId, Guid ZoneId)>> ListZoneMembershipsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);

    /// <summary>The technician's active memberships in ACTIVE zones.</summary>
    Task<IReadOnlyList<ZoneRef>> ListTechnicianZonesAsync(Guid technicianUserId, CancellationToken cancellationToken);

    /// <summary>True while the survey request has an OPEN/PROPOSAL_PENDING uncovered-zone dispatch request (bookings are refused meanwhile).</summary>
    Task<bool> HasOpenDispatchRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>Open (PENDING_ACCEPTANCE) appointments whose persisted acceptance deadline minus <paramref name="leadMinutes"/> passed, before the deadline, without a reminder yet.</summary>
    Task<IReadOnlyList<Guid>> ListDueAcceptanceRemindersAsync(DateTimeOffset nowUtc, int leadMinutes, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Conditional: acceptance_reminder_sent_at = now while still PENDING_ACCEPTANCE, before the deadline and without a reminder (the
    /// revision is NOT bumped: a reminder never invalidates an acceptance in flight). False when someone else got there first.
    /// </summary>
    Task<bool> TryMarkAcceptanceReminderAsync(Guid appointmentId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>True while the request's on-site survey report is IN_PROGRESS (the confirmed technician started it on site).</summary>
    Task<bool> HasOnSiteSurveyInProgressAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    /// <summary>PENDING_ACCEPTANCE appointments whose deadline passed and that have not started yet (worker input).</summary>
    Task<IReadOnlyList<Guid>> ListDueEscalationsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    /// <summary>Open (PENDING_ACCEPTANCE/ESCALATED) appointments whose start passed: nobody accepted them in time (worker input, oldest first).</summary>
    Task<IReadOnlyList<Guid>> ListStartedOpenAppointmentsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken);

    // ---- Writes ----

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (inside the retrying execution
    /// strategy) and commits only when it returns Commit = true.
    /// </summary>
    Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken);

    /// <summary>Row-locks the technicians' scheduling profiles (sorted, to avoid deadlocks). PostgreSQL only; SQLite serialises writers.</summary>
    Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken);

    /// <summary>Row-locks the property site (PostgreSQL only) so its administrative location cannot change under a booking.</summary>
    Task LockPropertySiteAsync(Guid propertySiteId, CancellationToken cancellationToken);

    /// <summary>
    /// Row-locks operational zones (PostgreSQL only, sorted): <paramref name="shared"/> = FOR SHARE (a booking or assignment that
    /// records the zone), otherwise FOR UPDATE (deactivation, serviceability or commune-set change), so a zone cannot lose a commune
    /// or be deactivated while work that uses it commits. Lock order everywhere: technician_profile → property_site → zone.
    /// </summary>
    Task LockZonesAsync(IEnumerable<Guid> zoneIds, bool shared, CancellationToken cancellationToken);

    Task<TechnicianAppointment?> FindAppointmentForUpdateAsync(Guid appointmentId, CancellationToken cancellationToken);
    void AddAppointment(TechnicianAppointment appointment);

    /// <summary>
    /// Scheduled (with <paramref name="scheduledAt"/>) when a technician is confirmed, Pending (no scheduled time) otherwise.
    /// Conditional: only while the request is Pending or Scheduled. False when it is Completed/Cancelled.
    /// </summary>
    Task<bool> SetSurveyRequestScheduleAsync(Guid surveyRequestId, DateTimeOffset? scheduledAt, CancellationToken cancellationToken);

    /// <summary>Conditional PENDING_ACCEPTANCE → ESCALATED when the deadline passed and the start has not; false when someone else changed it first.</summary>
    Task<bool> TryEscalateAsync(Guid appointmentId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Conditional open → CANCELLED (reason NOT_ACCEPTED_BEFORE_START, cancelled_at = now): only while the appointment is still
    /// PENDING_ACCEPTANCE/ESCALATED, has started (starts_at &lt;= now) and still has <paramref name="expectedRevision"/>.
    /// False when anyone changed it first (a concurrent sweep, acceptance or cancellation), so it is cancelled at most once.
    /// </summary>
    Task<bool> TryCancelNotAcceptedAsync(Guid appointmentId, int expectedRevision, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Saves tracked changes. Throws <see cref="SchedulingConflictException"/> for overlap, duplicate-active, duplicate or stale-revision conflicts.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    // ---- History / notifications ----
    void AddAppointmentEvent(TechnicianAppointmentEvent appointmentEvent);
    void AddNotification(SchedulingNotification notification);
    Task<IReadOnlyList<TechnicianAppointmentEvent>> ListAppointmentEventsAsync(Guid surveyRequestId, CancellationToken cancellationToken);

    // ---- Technician configuration ----

    /// <summary>
    /// Non-cancelled, non-completed survey appointments the technician holds or is confirmed on, ending after <paramref name="fromUtc"/>.
    /// Open holds count only while they start after <paramref name="fromUtc"/> (a started open appointment is auto-cancelled).
    /// </summary>
    Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingAppointmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Current (PENDING/ACCEPTED) installation assignments of the technician (Installation Flow) ending after <paramref name="fromUtc"/>,
    /// with status <see cref="Constants.InstallationCommitments.Status"/> and the project's site; empty while Installation Flow is disabled.
    /// </summary>
    Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingInstallationAssignmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Current (PENDING/ACCEPTED) installation assignments of the technician whose project has started and is not finished
    /// (IN_PROGRESS, REWORK_REQUIRED, PENDING_CUSTOMER_ACCEPTANCE), whatever their interval (it may have ended while the work goes on).
    /// Same shape as <see cref="ListUpcomingInstallationAssignmentsAsync"/>; empty while Installation Flow is disabled.
    /// </summary>
    Task<IReadOnlyList<UpcomingAppointment>> ListStartedInstallationAssignmentsAsync(Guid technicianUserId, CancellationToken cancellationToken);
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

    // ---- Operational zones ----
    Task<IReadOnlyList<OperationalZone>> ListZonesAsync(CancellationToken cancellationToken);
    Task<OperationalZone?> GetZoneAsync(Guid zoneId, CancellationToken cancellationToken);
    Task<OperationalZone?> FindZoneByCodeAsync(string code, CancellationToken cancellationToken);
    Task<OperationalZone?> FindZoneForUpdateAsync(Guid zoneId, CancellationToken cancellationToken);
    Task<IReadOnlyList<OperationalZone>> GetZonesAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);
    void AddZone(OperationalZone zone);
    Task<IReadOnlyList<ZoneStats>> ListZoneStatsAsync(CancellationToken cancellationToken);

    /// <summary>Unfinished work recording the zone (see <see cref="ZoneUsage"/>); open appointments that already started are not counted (they are auto-cancelled).</summary>
    Task<ZoneUsage> GetZoneUsageAsync(Guid zoneId, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>Unfinished work on sites located in each of these communes (communes without work are left out).</summary>
    Task<IReadOnlyList<CommuneUsage>> GetCommuneUsageAsync(IReadOnlyCollection<string> communeCodes, DateTimeOffset nowUtc, CancellationToken cancellationToken);

    /// <summary>The zone's active commune mappings with catalog names, by commune name.</summary>
    Task<IReadOnlyList<ZoneCommuneRow>> ListZoneCommunesAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>Active mappings (any zone) of these communes, with their zone.</summary>
    Task<IReadOnlyList<CommuneMappingRow>> FindCommuneMappingsAsync(IReadOnlyCollection<string> communeCodes, CancellationToken cancellationToken);

    /// <summary>Active mappings of these communes in the given zones, tracked for update (soft removal).</summary>
    Task<IReadOnlyList<OperationalZoneCommune>> FindActiveMappingsForUpdateAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken);
    void AddZoneCommune(OperationalZoneCommune mapping);

    /// <summary>Communes active on <paramref name="asOf"/> without an active mapping to an ACTIVE zone (optionally of one province), by name.</summary>
    Task<IReadOnlyList<SmartSolar.Modules.AdministrativeUnits.AdministrativeCommune>> ListUnmappedCommunesAsync(string? provinceCode, DateOnly asOf,
        CancellationToken cancellationToken);

    /// <summary>The zone's active memberships with each technician's state, by name.</summary>
    Task<IReadOnlyList<ZoneMemberRow>> ListZoneMembersAsync(Guid zoneId, CancellationToken cancellationToken);

    /// <summary>True when the user is an active, non-deleted account holding one of the roles.</summary>
    Task<bool> IsActiveUserWithRoleAsync(Guid userId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken);

    /// <summary>Active (not removed) zone memberships of the technician, tracked for update.</summary>
    Task<IReadOnlyList<TechnicianZoneMembership>> FindActiveMembershipsForUpdateAsync(Guid technicianUserId, CancellationToken cancellationToken);
    void AddMembership(TechnicianZoneMembership membership);

    // ---- Sites ----

    /// <summary>True when any survey request of the site has a PENDING/ESCALATED/ACCEPTED/ASSIGNED appointment.</summary>
    Task<bool> SiteHasActiveAppointmentAsync(Guid propertySiteId, CancellationToken cancellationToken);

    /// <summary>The site's PENDING_ACCEPTANCE/ESCALATED/ACCEPTED/ASSIGNED appointments, tracked for update.</summary>
    Task<IReadOnlyList<TechnicianAppointment>> FindActiveSiteAppointmentsForUpdateAsync(Guid propertySiteId, CancellationToken cancellationToken);

    /// <summary>True when an installation project of the site is not COMPLETED/CANCELLED (Installation Flow only; false while it is disabled).</summary>
    Task<bool> SiteHasActiveInstallationProjectAsync(Guid propertySiteId, CancellationToken cancellationToken);
    Task<PropertySite?> FindPropertySiteForUpdateAsync(Guid propertySiteId, CancellationToken cancellationToken);

    // ---- Commercial ownership ----

    /// <summary>
    /// Sets the Sales owner (Manager/Admin action, inside the caller's transaction): the request row is locked (FOR UPDATE on
    /// PostgreSQL), the current owner read, and the owner changed with a conditional update on that previous owner, so the
    /// returned <see cref="SalesOwnerUpdate.PreviousSaleId"/> is the owner that was really replaced. Updated = false when a
    /// concurrent claim or owner change won in between (nothing written; re-read and retry), Found = false when the request does not exist.
    /// </summary>
    Task<SalesOwnerUpdate> SetSalesOwnerAsync(Guid surveyRequestId, Guid saleUserId, DateTimeOffset nowUtc, CancellationToken cancellationToken);
    void AddSurveyRequestEvent(SurveyRequestEvent surveyRequestEvent);
}
