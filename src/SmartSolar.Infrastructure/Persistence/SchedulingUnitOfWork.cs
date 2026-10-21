using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.AdministrativeUnits;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>
/// Scheduling persistence. Concurrency is enforced in the database:
/// <list type="bullet">
/// <item>An exclusion constraint (migration SQL) forbids overlapping non-cancelled appointments held by the same technician
/// (<c>reserved_technician_user_id</c>, the capacity holder; NULL holders never conflict).</item>
/// <item>A partial unique index allows one non-cancelled appointment per survey request.</item>
/// <item>Every booking, acceptance, assignment, release and reschedule row-locks the affected technicians' profiles, so
/// daily limits and buffers are re-checked against a schedule nobody else can change.</item>
/// <item>Appointments carry a revision token, so a stale read can never overwrite a newer change.</item>
/// </list>
/// </summary>
public sealed class SchedulingUnitOfWork : ISchedulingUnitOfWork
{
    private const string ExclusionViolation = "23P01";
    private const string UniqueViolation = "23505";
    private const string ActiveSurveyRequestIndex = "ux_technician_appointment_active_survey_request";
    private const string ActiveCommuneMappingIndex = "ux_operational_zone_commune_active";

    private readonly AppDbContext _db;
    private readonly bool _includeInstallationAssignments;
    private readonly TimeProvider _time;

    /// <param name="includeInstallationAssignments">
    /// True once the Installation Flow tables exist (Features:InstallationWorkflowEnabled): current (PENDING/ACCEPTED)
    /// installation assignments are then busy intervals in every technician day state, so survey slots, self-acceptance,
    /// staff assignment and installation assignment share one conflict model (buffer included). False keeps Flow 1
    /// independent of tables that may not exist yet.
    /// </param>
    /// <param name="time">Clock deciding which catalog communes are in force (Vietnam business date); the system clock by default.</param>
    public SchedulingUnitOfWork(AppDbContext db, bool includeInstallationAssignments = false, TimeProvider? time = null)
    {
        _db = db;
        _includeInstallationAssignments = includeInstallationAssignments;
        _time = time ?? TimeProvider.System;
    }

    private DateOnly CatalogToday => AdministrativeCatalogClock.Today(_time);

    private IQueryable<SmartSolar.Modules.Installations.InstallationAssignment> CurrentInstallationAssignments
        => _db.InstallationAssignments.Where(a => a.Status == SmartSolar.Modules.Installations.AssignmentStatuses.Pending
                                                  || a.Status == SmartSolar.Modules.Installations.AssignmentStatuses.Accepted);

    private IQueryable<TechnicianAppointment> Active => _db.TechnicianAppointments.Where(a => a.Status != AppointmentStatuses.Cancelled);

    private static AppointmentSnapshot Snapshot(TechnicianAppointment a) => AppointmentAssignmentService.Snapshot(a);

    private static ZoneRef Ref(OperationalZone z) => new(z.Id, z.Code, z.Name, z.IsServiceable);

    private IQueryable<Guid> TechnicianRoleUserIds => _db.UserRoles.Where(ur => ur.Role.Code == RoleCodes.Technician).Select(ur => ur.UserId);

    /// <summary>Active memberships in ACTIVE zones (what eligibility and the candidate prefilter use).</summary>
    private IQueryable<TechnicianZoneMembership> ActiveMemberships
        => _db.TechnicianZoneMemberships.Where(m => m.RemovedAt == null && _db.OperationalZones.Any(z => z.Id == m.ZoneId && z.IsActive));

    /// <summary>Active commune mappings of ACTIVE zones.</summary>
    private IQueryable<OperationalZoneCommune> ActiveMappings
        => _db.OperationalZoneCommunes.Where(m => m.RemovedAt == null && _db.OperationalZones.Any(z => z.Id == m.ZoneId && z.IsActive));

    public async Task<PropertySiteInfo?> GetPropertySiteAsync(Guid propertySiteId, CancellationToken cancellationToken)
    {
        var row = await _db.PropertySites.AsNoTracking()
            .Where(p => p.Id == propertySiteId)
            .Select(p => new { p.Id, p.Customer.UserId, p.ProvinceCode, p.CommuneCode })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new PropertySiteInfo(row.Id, row.UserId, row.ProvinceCode, row.CommuneCode);
    }

    public async Task<SiteCoverage> GetSiteCoverageAsync(Guid propertySiteId, CancellationToken cancellationToken)
        => (await GetSiteCoveragesAsync([propertySiteId], cancellationToken))[propertySiteId];

    /// <summary>
    /// Constant number of queries: the sites, the in-force catalog communes they reference (inside their own province), and the
    /// active mappings of those communes to active zones.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, SiteCoverage>> GetSiteCoveragesAsync(IReadOnlyCollection<Guid> propertySiteIds, CancellationToken cancellationToken)
    {
        var ids = propertySiteIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, SiteCoverage>();
        var sites = await _db.PropertySites.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.ProvinceCode, p.CommuneCode }).ToListAsync(cancellationToken);
        var communeCodes = sites.Where(s => s.CommuneCode != null).Select(s => s.CommuneCode!).Distinct().ToList();
        var today = CatalogToday;
        var communes = communeCodes.Count == 0
            ? []
            : await _db.AdministrativeCommunes.AsNoTracking()
                .Where(c => communeCodes.Contains(c.Code) && c.ValidFrom <= today && (c.ValidTo == null || c.ValidTo > today))
                .Select(c => new { c.Code, c.ProvinceCode }).ToListAsync(cancellationToken);
        var zones = communeCodes.Count == 0
            ? []
            : await ActiveMappings.AsNoTracking().Where(m => communeCodes.Contains(m.CommuneCode))
                .Join(_db.OperationalZones, m => m.ZoneId, z => z.Id, (m, z) => new { m.CommuneCode, Zone = z })
                .ToListAsync(cancellationToken);
        var bySite = sites.ToDictionary(s => s.Id);
        return ids.ToDictionary(id => id, id =>
        {
            if (!bySite.TryGetValue(id, out var site)) return SiteCoverage.NoLocation;
            var valid = site.CommuneCode is { } code && communes.Any(c => c.Code == code && c.ProvinceCode == site.ProvinceCode);
            if (!valid) return new SiteCoverage(site.ProvinceCode, site.CommuneCode, false, null);
            var zone = zones.FirstOrDefault(z => z.CommuneCode == site.CommuneCode)?.Zone;
            return new SiteCoverage(site.ProvinceCode, site.CommuneCode, true, zone is null ? null : Ref(zone));
        });
    }

    public async Task<SurveyRequestSchedulingContext?> GetSurveyRequestContextAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var row = await _db.SurveyRequests.AsNoTracking()
            .Where(x => x.Id == surveyRequestId)
            .Select(x => new { x.Id, x.PreSurveyId, x.PreSurvey.PropertyId, x.PreSurvey.Property.Customer.UserId, x.AssignedSaleId, x.Status })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        var active = await Active.AsNoTracking().Where(a => a.SurveyRequestId == surveyRequestId).FirstOrDefaultAsync(cancellationToken);
        return new SurveyRequestSchedulingContext(row.Id, row.PreSurveyId, row.PropertyId, row.UserId, row.AssignedSaleId, row.Status,
            active is null ? null : Snapshot(active));
    }

    public async Task<IReadOnlyList<TechnicianDayState>> GetTechnicianDayStatesAsync(
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, TechnicianScope scope, CancellationToken cancellationToken)
    {
        var ids = scope.TechnicianIds?.ToList();
        var zoneIds = scope.ZoneIds?.ToList();
        var roleUsers = TechnicianRoleUserIds;
        var users = _db.UserAccounts.AsNoTracking().Where(u => u.DeletedAt == null);
        if (ids is not null) users = users.Where(u => ids.Contains(u.Id));
        if (scope.SchedulableOnly)
        {
            users = users.Where(u => u.Status == UserStatus.Active && roleUsers.Contains(u.Id)
                                     && _db.TechnicianProfiles.Any(p => p.UserId == u.Id && p.IsSchedulingActive));
        }
        else if (ids is null)
        {
            throw new ArgumentException("Explicit technician ids are required when loading non-schedulable technicians.", nameof(scope));
        }
        if (zoneIds is not null)
        {
            var members = ActiveMemberships.Where(m => zoneIds.Contains(m.ZoneId)).Select(m => m.TechnicianUserId);
            users = users.Where(u => members.Contains(u.Id));
        }

        var technicians = await users
            .Select(u => new { u.Id, u.FullName, IsActive = u.Status == UserStatus.Active, HasRole = roleUsers.Contains(u.Id) })
            .OrderBy(u => u.Id)
            .ToListAsync(cancellationToken);
        if (technicians.Count == 0) return [];

        var userIds = technicians.Select(t => t.Id).ToList();
        var profiles = await _db.TechnicianProfiles.AsNoTracking().Where(p => userIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancellationToken);

        var appointments = await Active.AsNoTracking()
            .Where(a => a.ReservedTechnicianUserId != null && userIds.Contains(a.ReservedTechnicianUserId.Value)
                        && a.StartsAt < windowEndUtc && a.EndsAt > windowStartUtc)
            .Select(a => new { a.Id, Technician = a.ReservedTechnicianUserId!.Value, a.StartsAt, a.EndsAt, a.Status })
            .ToListAsync(cancellationToken);

        var blocked = await _db.TechnicianUnavailablePeriods.AsNoTracking()
            .Where(b => userIds.Contains(b.TechnicianUserId) && b.StartsAt < windowEndUtc && b.EndsAt > windowStartUtc)
            .Select(b => new { b.TechnicianUserId, b.StartsAt, b.EndsAt })
            .ToListAsync(cancellationToken);

        // Installation commitments (Installation Flow): current responsible-technician assignments (possibly multi-day).
        var installation = !_includeInstallationAssignments
            ? []
            : await CurrentInstallationAssignments.AsNoTracking()
                .Where(s => userIds.Contains(s.TechnicianUserId) && s.StartsAt < windowEndUtc && s.EndsAt > windowStartUtc)
                .Select(s => new { s.Id, s.TechnicianUserId, s.StartsAt, s.EndsAt })
                .ToListAsync(cancellationToken);

        var customIds = profiles.Values.Where(p => p.HasCustomWorkingSchedule).Select(p => p.UserId).ToList();
        var weekly = customIds.Count == 0
            ? []
            : await _db.TechnicianWorkingPeriods.AsNoTracking().Where(w => customIds.Contains(w.TechnicianUserId)).ToListAsync(cancellationToken);

        var memberships = await ActiveMemberships.AsNoTracking().Where(m => userIds.Contains(m.TechnicianUserId))
            .Select(m => new { m.TechnicianUserId, m.ZoneId }).ToListAsync(cancellationToken);

        return technicians.Select(t =>
        {
            var profile = profiles.GetValueOrDefault(t.Id);
            return new TechnicianDayState(
                t.Id,
                t.FullName,
                appointments.Where(a => a.Technician == t.Id)
                    .Select(a => new ScheduledStop(a.Id, a.StartsAt, a.EndsAt, IsPendingHold: AppointmentStatuses.IsOpen(a.Status)))
                    .Concat(installation.Where(s => s.TechnicianUserId == t.Id)
                        .Select(s => new ScheduledStop(s.Id, s.StartsAt, s.EndsAt, CountsTowardDailyLimit: false)))
                    .OrderBy(a => a.StartsAtUtc)
                    .ToList(),
                blocked.Where(b => b.TechnicianUserId == t.Id).Select(b => new BlockedPeriod(b.StartsAt, b.EndsAt)).ToList(),
                profile?.MaxAppointmentsPerDay,
                profile is { HasCustomWorkingSchedule: true }
                    ? weekly.Where(w => w.TechnicianUserId == t.Id).Select(w => new WeeklyPeriod((DayOfWeek)w.DayOfWeek, w.StartTime, w.EndTime)).ToList()
                    : null,
                IsActiveAccount: t.IsActive,
                HasTechnicianRole: t.HasRole,
                IsSchedulingActive: profile?.IsSchedulingActive ?? false,
                ZoneIds: memberships.Where(m => m.TechnicianUserId == t.Id).Select(m => m.ZoneId).ToHashSet());
        }).ToList();
    }

    public async Task<TechnicianIdentity?> GetTechnicianAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.UserAccounts.AsNoTracking()
            .Where(u => u.Id == userId && u.DeletedAt == null)
            .Select(u => new { u.Id, u.FullName, IsActive = u.Status == UserStatus.Active, HasRole = u.UserRoles.Any(r => r.Role.Code == RoleCodes.Technician) })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null) return null;
        var profile = await _db.TechnicianProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        return new TechnicianIdentity(user.Id, user.FullName, user.IsActive, user.HasRole, profile);
    }

    public async Task<AppointmentSnapshot?> GetAppointmentAsync(Guid appointmentId, CancellationToken cancellationToken)
    {
        var a = await _db.TechnicianAppointments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == appointmentId, cancellationToken);
        return a is null ? null : Snapshot(a);
    }

    public async Task<IReadOnlyList<CustomerSurveyRequestRow>> ListCustomerSurveyRequestsAsync(Guid customerUserId, CancellationToken cancellationToken)
    {
        var requests = await _db.SurveyRequests.AsNoTracking()
            .Where(x => x.PreSurvey.Property.Customer.UserId == customerUserId)
            .Select(x => new { x.Id, x.PreSurveyId, x.PreSurvey.PropertyId, x.PreSurvey.Property.Name, x.Status, x.SubmittedAt, x.ScheduledAt })
            .ToListAsync(cancellationToken);
        if (requests.Count == 0) return [];

        var ids = requests.Select(r => (Guid?)r.Id).ToList();
        var appointments = await Active.AsNoTracking().Where(a => ids.Contains(a.SurveyRequestId)).ToListAsync(cancellationToken);
        // Only a confirmed technician is ever named to the customer, never the capacity holder.
        var technicianIds = appointments.Where(a => a.AssignedTechnicianUserId != null).Select(a => a.AssignedTechnicianUserId!.Value).Distinct().ToList();
        var names = await _db.UserAccounts.AsNoTracking().Where(u => technicianIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return requests
            .OrderByDescending(r => r.SubmittedAt)
            .Select(r =>
            {
                var appointment = appointments.FirstOrDefault(a => a.SurveyRequestId == r.Id);
                var technicianName = appointment?.AssignedTechnicianUserId is { } tid && names.TryGetValue(tid, out var n) ? n : null;
                return new CustomerSurveyRequestRow(r.Id, r.PreSurveyId, r.PropertyId, r.Name, r.Status, r.SubmittedAt, r.ScheduledAt,
                    appointment is null ? null : Snapshot(appointment), technicianName);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<AppointmentSnapshot>> ListAppointmentHistoryAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => (await _db.TechnicianAppointments.AsNoTracking().Where(a => a.SurveyRequestId == surveyRequestId).ToListAsync(cancellationToken))
            .OrderBy(a => a.BookedAt).ThenBy(a => a.CreatedAt).Select(Snapshot).ToList();

    private static readonly string[] TechnicianVisibleStatuses = [AppointmentStatuses.Accepted, AppointmentStatuses.Assigned, AppointmentStatuses.Completed];

    public async Task<IReadOnlyList<(AppointmentSnapshot Appointment, string PropertyName, string Province, string? District)>> ListTechnicianAppointmentsAsync(
        Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
    {
        var rows = await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.AssignedTechnicianUserId == technicianUserId && TechnicianVisibleStatuses.Contains(a.Status) && a.EndsAt >= fromUtc)
            .Join(_db.PropertySites, a => a.PropertySiteId, p => p.Id, (a, p) => new { a, p.Name, p.Province, p.District })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.a.StartsAt).Select(r => (Snapshot(r.a), r.Name, r.Province, r.District)).ToList();
    }

    public async Task<TechnicianJobDetail?> GetTechnicianJobAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var appointment = await _db.TechnicianAppointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appointmentId
                                      && a.AssignedTechnicianUserId == technicianUserId
                                      && TechnicianVisibleStatuses.Contains(a.Status)
                                      && a.SurveyRequestId != null, cancellationToken);
        if (appointment is null) return null;

        var job = await _db.SurveyRequests.AsNoTracking()
            .Where(x => x.Id == appointment.SurveyRequestId)
            .Select(x => new
            {
                x.Id,
                x.Status,
                User = x.PreSurvey.Property.Customer.User,
                Property = x.PreSurvey.Property,
                PreSurvey = x.PreSurvey,
            })
            .Select(x => new
            {
                x.Id,
                x.Status,
                x.User.FullName,
                x.User.Phone,
                x.User.Email,
                PropertyId = x.Property.Id,
                PropertyName = x.Property.Name,
                x.Property.Province,
                x.Property.District,
                x.Property.Ward,
                x.Property.StreetLine,
                x.Property.Latitude,
                x.Property.Longitude,
                x.Property.InstallationSurfaceType,
                x.Property.SurfaceMaterial,
                PreSurveyId = x.PreSurvey.Id,
                x.PreSurvey.TotalAreaM2,
                x.PreSurvey.UsableAreaM2,
                x.PreSurvey.SurfaceLengthM,
                x.PreSurvey.SurfaceWidthM,
                x.PreSurvey.TiltDegree,
                x.PreSurvey.AzimuthDegree,
                x.PreSurvey.HasObstruction,
                x.PreSurvey.Obstacles,
                x.PreSurvey.SelectedSimulationId,
                x.PreSurvey.GeometryVersion,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (job is null) return null;

        SelectedSimulationSummary? simulation = null;
        if (job.SelectedSimulationId is { } selectedId)
        {
            simulation = await _db.SelectedSimulations.AsNoTracking()
                .Where(s => s.SimulationId == selectedId && s.PreSurveyId == job.PreSurveyId)
                .Select(s => new SelectedSimulationSummary(s.SimulationId, s.Status, s.EnergyStatus, s.PreSurveyGeometryVersion != job.GeometryVersion,
                    s.MountingType, s.ProductSku, s.ProductName, s.PanelCount, s.InstalledCapacityKwp, s.AnnualEnergyKwh, s.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new TechnicianJobDetail(Snapshot(appointment), job.Id, job.Status, job.FullName, job.Phone, job.Email,
            job.PropertyId, job.PropertyName, job.Province, job.District, job.Ward, job.StreetLine, job.Latitude, job.Longitude,
            job.InstallationSurfaceType, job.SurfaceMaterial, job.PreSurveyId, job.TotalAreaM2, job.UsableAreaM2, job.SurfaceLengthM,
            job.SurfaceWidthM, job.TiltDegree, job.AzimuthDegree, job.HasObstruction, job.Obstacles, simulation);
    }

    public async Task<IReadOnlyList<ScheduleEntry>> GetTechnicianScheduleAsync(
        Guid technicianUserId, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, bool includeHolds, CancellationToken cancellationToken)
    {
        var rows = await Active.AsNoTracking()
            .Where(a => a.ReservedTechnicianUserId == technicianUserId && a.StartsAt < dayEndUtc && a.EndsAt > dayStartUtc
                        && (includeHolds || (a.Status != AppointmentStatuses.PendingAcceptance && a.Status != AppointmentStatuses.Escalated)))
            .Join(_db.PropertySites, a => a.PropertySiteId, p => p.Id, (a, p) => new { a, p.Name })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.a.StartsAt).Select(r => new ScheduleEntry(Snapshot(r.a), r.Name)).ToList();
    }

    public async Task<IReadOnlyList<TechnicianListItem>> ListTechniciansAsync(Guid? zoneId, CancellationToken cancellationToken)
    {
        var query = _db.UserAccounts.AsNoTracking().Where(u => u.DeletedAt == null && u.UserRoles.Any(r => r.Role.Code == RoleCodes.Technician));
        if (zoneId is { } id)
        {
            var members = _db.TechnicianZoneMemberships.Where(m => m.RemovedAt == null && m.ZoneId == id).Select(m => m.TechnicianUserId);
            query = query.Where(u => members.Contains(u.Id));
        }
        var users = await query.Select(u => new { u.Id, u.FullName, u.Phone, IsActive = u.Status == UserStatus.Active }).ToListAsync(cancellationToken);
        var ids = users.Select(u => u.Id).ToList();
        var profiles = await _db.TechnicianProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancellationToken);
        var zones = await _db.TechnicianZoneMemberships.AsNoTracking()
            .Where(m => m.RemovedAt == null && ids.Contains(m.TechnicianUserId))
            .Join(_db.OperationalZones, m => m.ZoneId, z => z.Id, (m, z) => new { m.TechnicianUserId, Zone = z })
            .ToListAsync(cancellationToken);
        return users.OrderBy(u => u.FullName)
            .Select(u => new TechnicianListItem(u.Id, u.FullName, u.Phone, u.IsActive, profiles.GetValueOrDefault(u.Id),
                zones.Where(z => z.TechnicianUserId == u.Id).Select(z => Ref(z.Zone)).OrderBy(z => z.Code, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    public async Task<IReadOnlyList<OpenAppointmentRow>> ListOpenAppointmentsAsync(IReadOnlyCollection<Guid> zoneIds, DateTimeOffset fromUtc, Guid? appointmentId,
        CancellationToken cancellationToken)
    {
        if (zoneIds.Count == 0) return [];
        var zoneList = zoneIds.ToList();
        var mapped = ActiveMappings.Where(m => zoneList.Contains(m.ZoneId)).Select(m => m.CommuneCode);
        var rows = await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated)
                        && a.StartsAt > fromUtc && a.SurveyRequestId != null && (appointmentId == null || a.Id == appointmentId))
            .Join(_db.PropertySites, a => a.PropertySiteId, p => p.Id, (a, p) => new { a, p })
            .Where(x => x.p.CommuneCode != null && mapped.Contains(x.p.CommuneCode))
            .Select(x => new { x.a, x.p.Name, x.p.Province, x.p.District, x.p.Ward, x.p.InstallationSurfaceType })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];

        var requestIds = rows.Select(r => r.a.SurveyRequestId).ToList();
        var surveys = await _db.SurveyRequests.AsNoTracking().Where(s => requestIds.Contains(s.Id))
            .Select(s => new { s.Id, s.PreSurvey.TotalAreaM2, s.PreSurvey.UsableAreaM2 }).ToDictionaryAsync(s => s.Id, cancellationToken);
        var recordedZoneIds = rows.Where(r => r.a.OperationalZoneId != null).Select(r => r.a.OperationalZoneId!.Value).Distinct().ToList();
        var recorded = await _db.OperationalZones.AsNoTracking().Where(z => recordedZoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, cancellationToken);

        return rows.OrderBy(r => r.a.StartsAt).Select(r =>
        {
            var survey = surveys.GetValueOrDefault(r.a.SurveyRequestId!.Value);
            return new OpenAppointmentRow(Snapshot(r.a), r.Name, r.Province, r.District, r.Ward, r.InstallationSurfaceType,
                survey?.TotalAreaM2, survey?.UsableAreaM2,
                r.a.OperationalZoneId is { } id && recorded.TryGetValue(id, out var zone) ? Ref(zone) : null);
        }).ToList();
    }

    public async Task<IReadOnlyList<EscalatedAppointmentRow>> ListEscalatedAppointmentsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var rows = await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.SurveyRequestId != null && a.StartsAt > nowUtc
                        && (a.Status == AppointmentStatuses.Escalated
                            || (a.Status == AppointmentStatuses.PendingAcceptance && a.AcceptanceDeadlineAt != null && a.AcceptanceDeadlineAt <= nowUtc)))
            .Join(_db.SurveyRequests, a => a.SurveyRequestId, s => s.Id, (a, s) => new
            {
                a,
                s.AssignedSaleId,
                CustomerName = s.PreSurvey.Property.Customer.User.FullName,
                s.PreSurvey.Property.Name,
                s.PreSurvey.Property.Province,
                s.PreSurvey.Property.District,
                s.PreSurvey.Property.Ward,
            })
            .ToListAsync(cancellationToken);
        var zoneIds = rows.Where(r => r.a.OperationalZoneId != null).Select(r => r.a.OperationalZoneId!.Value).Distinct().ToList();
        var zones = await _db.OperationalZones.AsNoTracking().Where(z => zoneIds.Contains(z.Id)).ToDictionaryAsync(z => z.Id, cancellationToken);
        return rows.OrderBy(r => r.a.StartsAt).Select(r => new EscalatedAppointmentRow(Snapshot(r.a), r.AssignedSaleId, r.CustomerName, r.Name, r.Province,
            r.District, r.Ward, r.a.OperationalZoneId is { } id && zones.TryGetValue(id, out var zone) ? Ref(zone) : null)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> ListZoneMemberIdsAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken)
    {
        if (zoneIds.Count == 0) return [];
        var ids = zoneIds.ToList();
        return await _db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.RemovedAt == null && ids.Contains(m.ZoneId))
            .Select(m => m.TechnicianUserId).Distinct().ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(Guid TechnicianUserId, Guid ZoneId)>> ListZoneMembershipsAsync(IReadOnlyCollection<Guid> zoneIds,
        CancellationToken cancellationToken)
    {
        if (zoneIds.Count == 0) return [];
        var ids = zoneIds.ToList();
        return (await _db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.RemovedAt == null && ids.Contains(m.ZoneId))
                .Select(m => new { m.TechnicianUserId, m.ZoneId }).Distinct().ToListAsync(cancellationToken))
            .Select(m => (m.TechnicianUserId, m.ZoneId)).ToList();
    }

    public async Task<IReadOnlyList<ZoneRef>> ListTechnicianZonesAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await _db.TechnicianZoneMemberships.AsNoTracking()
                .Where(m => m.TechnicianUserId == technicianUserId && m.RemovedAt == null)
                .Join(_db.OperationalZones.Where(z => z.IsActive), m => m.ZoneId, z => z.Id, (m, z) => z)
                .ToListAsync(cancellationToken))
            .OrderBy(z => z.Code, StringComparer.Ordinal).Select(Ref).ToList();

    public Task<bool> HasOpenDispatchRequestAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => _db.Set<SmartSolar.Modules.Scheduling.Dispatch.DispatchRequest>().AsNoTracking()
            .AnyAsync(r => r.SurveyRequestId == surveyRequestId
                           && (r.Status == SmartSolar.Modules.Scheduling.Dispatch.DispatchStatuses.Open
                               || r.Status == SmartSolar.Modules.Scheduling.Dispatch.DispatchStatuses.ProposalPending), cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDueAcceptanceRemindersAsync(DateTimeOffset nowUtc, int leadMinutes, int limit, CancellationToken cancellationToken)
    {
        // deadline - lead <= now < deadline  ⇔  now < deadline <= now + lead (persisted deadline; the lead is the current setting).
        var horizon = nowUtc.AddMinutes(leadMinutes);
        return await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatuses.PendingAcceptance && a.AcceptanceReminderSentAt == null && a.ReservedTechnicianUserId != null
                        && a.AcceptanceDeadlineAt != null && a.AcceptanceDeadlineAt > nowUtc && a.AcceptanceDeadlineAt <= horizon && a.StartsAt > nowUtc)
            .OrderBy(a => a.AcceptanceDeadlineAt)
            .Take(limit)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryMarkAcceptanceReminderAsync(Guid appointmentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await _db.TechnicianAppointments
            .Where(a => a.Id == appointmentId && a.Status == AppointmentStatuses.PendingAcceptance && a.AcceptanceReminderSentAt == null
                        && a.AcceptanceDeadlineAt != null && a.AcceptanceDeadlineAt > nowUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AcceptanceReminderSentAt, nowUtc), cancellationToken) == 1;

    public Task<bool> HasOnSiteSurveyInProgressAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => _db.OnSiteSurveyReports.AsNoTracking()
            .AnyAsync(r => r.SurveyRequestId == surveyRequestId && r.Status == SmartSolar.Modules.OnSiteSurveys.OnSiteSurveyStatuses.InProgress, cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListDueEscalationsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatuses.PendingAcceptance && a.AcceptanceDeadlineAt != null && a.AcceptanceDeadlineAt <= nowUtc
                        && a.StartsAt > nowUtc)
            .OrderBy(a => a.AcceptanceDeadlineAt)
            .Take(limit)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ListStartedOpenAppointmentsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => (a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated) && a.StartsAt <= nowUtc)
            .OrderBy(a => a.StartsAt)
            .Take(limit)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(cancellationToken, async token =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(token);
            try
            {
                var (result, commit) = await work(token);
                if (commit) await transaction.CommitAsync(token);
                else await transaction.RollbackAsync(token);
                return result;
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        });
    }

    public async Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsNpgsql()) return;  // SQLite: one writer at a time already.

        foreach (var id in technicianUserIds.Distinct().OrderBy(id => id))
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM technician_profile WHERE user_id = {id} FOR UPDATE", cancellationToken);
        }
    }

    public async Task LockPropertySiteAsync(Guid propertySiteId, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsNpgsql()) return;
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM property_site WHERE id = {propertySiteId} FOR UPDATE", cancellationToken);
    }

    public async Task LockZonesAsync(IEnumerable<Guid> zoneIds, bool shared, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsNpgsql()) return;
        foreach (var id in zoneIds.Distinct().OrderBy(id => id))
        {
            if (shared)
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM operational_zone WHERE id = {id} FOR SHARE", cancellationToken);
            else
                await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM operational_zone WHERE id = {id} FOR UPDATE", cancellationToken);
        }
    }

    public Task<TechnicianAppointment?> FindAppointmentForUpdateAsync(Guid appointmentId, CancellationToken cancellationToken)
        => _db.TechnicianAppointments.FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

    public void AddAppointment(TechnicianAppointment appointment) => _db.TechnicianAppointments.Add(appointment);

    public async Task<bool> SetSurveyRequestScheduleAsync(Guid surveyRequestId, DateTimeOffset? scheduledAt, CancellationToken cancellationToken)
    {
        var status = scheduledAt is null ? SurveyRequestStatus.Pending : SurveyRequestStatus.Scheduled;
        return await _db.SurveyRequests
            .Where(x => x.Id == surveyRequestId && (x.Status == SurveyRequestStatus.Pending || x.Status == SurveyRequestStatus.Scheduled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.ScheduledAt, scheduledAt), cancellationToken) == 1;
    }

    public async Task<bool> TryEscalateAsync(Guid appointmentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await _db.TechnicianAppointments
            .Where(a => a.Id == appointmentId && a.Status == AppointmentStatuses.PendingAcceptance
                        && a.AcceptanceDeadlineAt != null && a.AcceptanceDeadlineAt <= nowUtc && a.StartsAt > nowUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AppointmentStatuses.Escalated)
                .SetProperty(a => a.EscalatedAt, nowUtc)
                .SetProperty(a => a.UpdatedAt, nowUtc)
                .SetProperty(a => a.Revision, a => a.Revision + 1), cancellationToken) == 1;

    public async Task<bool> TryCancelNotAcceptedAsync(Guid appointmentId, int expectedRevision, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await _db.TechnicianAppointments
            .Where(a => a.Id == appointmentId && a.Revision == expectedRevision && a.StartsAt <= nowUtc
                        && (a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated))
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, AppointmentStatuses.Cancelled)
                .SetProperty(a => a.CancelledAt, nowUtc)
                .SetProperty(a => a.CancellationReason, CancellationReasons.NotAcceptedBeforeStart)
                .SetProperty(a => a.UpdatedAt, nowUtc)
                .SetProperty(a => a.Revision, a => a.Revision + 1), cancellationToken) == 1;

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SchedulingConflictException(SchedulingConflictException.StaleAppointment, ex);
        }
        catch (DbUpdateException ex)
        {
            var (state, constraint) = DbExceptionClassifier.PostgresDetails(ex);
            if (state == ExclusionViolation)
                throw new SchedulingConflictException(SchedulingConflictException.TechnicianOverlap, ex);
            if ((state == UniqueViolation && constraint == ActiveSurveyRequestIndex)
                || DbExceptionClassifier.IsSqliteUniqueOn(ex, "technician_appointment.survey_request_id"))
                throw new SchedulingConflictException(SchedulingConflictException.ActiveAppointmentExists, ex);
            if ((state == UniqueViolation && constraint == ActiveCommuneMappingIndex)
                || DbExceptionClassifier.IsSqliteUniqueOn(ex, "operational_zone_commune.commune_code"))
                throw new SchedulingConflictException(SchedulingConflictException.CommuneAlreadyMapped, ex);
            if (state == UniqueViolation || DbExceptionClassifier.IsUniqueViolation(ex))
                throw new SchedulingConflictException(SchedulingConflictException.Duplicate, ex);
            throw;
        }
    }

    // ---- History / notifications ----

    public void AddAppointmentEvent(TechnicianAppointmentEvent appointmentEvent) => _db.TechnicianAppointmentEvents.Add(appointmentEvent);

    public void AddNotification(SchedulingNotification notification) => _db.SchedulingNotifications.Add(notification);

    public async Task<IReadOnlyList<TechnicianAppointmentEvent>> ListAppointmentEventsAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => (await _db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.SurveyRequestId == surveyRequestId).ToListAsync(cancellationToken))
            .OrderBy(e => e.CreatedAt).ToList();

    // ---- Technician configuration ----

    public async Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingInstallationAssignmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
        => !_includeInstallationAssignments
            ? []
            : (await CurrentInstallationAssignments.AsNoTracking()
                    .Where(a => a.TechnicianUserId == technicianUserId && a.EndsAt > fromUtc)
                    .Join(_db.InstallationProjects, a => a.ProjectId, p => p.Id, (a, p) => new { a.Id, a.StartsAt, a.EndsAt, p.PropertySiteId })
                    .ToListAsync(cancellationToken))
                .OrderBy(a => a.StartsAt)
                .Select(a => new UpcomingAppointment(a.Id, null, a.StartsAt, a.EndsAt, InstallationCommitments.Status, a.PropertySiteId))
                .ToList();

    public async Task<IReadOnlyList<UpcomingAppointment>> ListStartedInstallationAssignmentsAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => !_includeInstallationAssignments
            ? []
            : (await CurrentInstallationAssignments.AsNoTracking()
                    .Where(a => a.TechnicianUserId == technicianUserId)
                    .Join(_db.InstallationProjects, a => a.ProjectId, p => p.Id, (a, p) => new { a.Id, a.StartsAt, a.EndsAt, p.PropertySiteId, p.Status })
                    .Where(x => x.Status == SmartSolar.Modules.Installations.InstallationStatuses.InProgress
                                || x.Status == SmartSolar.Modules.Installations.InstallationStatuses.ReworkRequired
                                || x.Status == SmartSolar.Modules.Installations.InstallationStatuses.PendingCustomerAcceptance)
                    .ToListAsync(cancellationToken))
                .OrderBy(a => a.StartsAt)
                .Select(a => new UpcomingAppointment(a.Id, null, a.StartsAt, a.EndsAt, InstallationCommitments.Status, a.PropertySiteId))
                .ToList();

    public async Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingAppointmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
        => (await _db.TechnicianAppointments.AsNoTracking()
                .Where(a => a.EndsAt > fromUtc
                            && ((a.ReservedTechnicianUserId == technicianUserId && a.StartsAt > fromUtc
                                 && (a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated))
                                || (a.AssignedTechnicianUserId == technicianUserId
                                    && (a.Status == AppointmentStatuses.Accepted || a.Status == AppointmentStatuses.Assigned))))
                .Select(a => new { a.Id, a.SurveyRequestId, a.StartsAt, a.EndsAt, a.Status, a.PropertySiteId })
                .ToListAsync(cancellationToken))
            .OrderBy(a => a.StartsAt)
            .Select(a => new UpcomingAppointment(a.Id, a.SurveyRequestId, a.StartsAt, a.EndsAt, a.Status, a.PropertySiteId))
            .ToList();

    public async Task<IReadOnlyList<TechnicianWorkingPeriod>> GetWorkingPeriodsAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await _db.TechnicianWorkingPeriods.AsNoTracking().Where(w => w.TechnicianUserId == technicianUserId).ToListAsync(cancellationToken))
            .OrderBy(w => w.DayOfWeek).ThenBy(w => w.StartTime).ToList();

    public async Task ReplaceWorkingPeriodsAsync(Guid technicianUserId, IReadOnlyList<TechnicianWorkingPeriod> periods, CancellationToken cancellationToken)
    {
        _db.TechnicianWorkingPeriods.RemoveRange(await _db.TechnicianWorkingPeriods.Where(w => w.TechnicianUserId == technicianUserId).ToListAsync(cancellationToken));
        _db.TechnicianWorkingPeriods.AddRange(periods);
    }

    public async Task<IReadOnlyList<TechnicianUnavailablePeriod>> ListUnavailablePeriodsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
        => (await _db.TechnicianUnavailablePeriods.AsNoTracking().Where(p => p.TechnicianUserId == technicianUserId && p.EndsAt > fromUtc).ToListAsync(cancellationToken))
            .OrderBy(p => p.StartsAt).ToList();

    public Task<TechnicianUnavailablePeriod?> FindUnavailablePeriodAsync(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken)
        => _db.TechnicianUnavailablePeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId && p.TechnicianUserId == technicianUserId, cancellationToken);

    public Task<TechnicianProfile?> FindProfileForUpdateAsync(Guid userId, CancellationToken cancellationToken)
        => _db.TechnicianProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

    public void AddProfile(TechnicianProfile profile) => _db.TechnicianProfiles.Add(profile);

    public void AddUnavailablePeriod(TechnicianUnavailablePeriod period) => _db.TechnicianUnavailablePeriods.Add(period);

    public async Task<bool> RemoveUnavailablePeriodAsync(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken)
        => await _db.TechnicianUnavailablePeriods.Where(p => p.Id == periodId && p.TechnicianUserId == technicianUserId)
            .ExecuteDeleteAsync(cancellationToken) == 1;

    // ---- Operational zones ----

    public async Task<IReadOnlyList<OperationalZone>> ListZonesAsync(CancellationToken cancellationToken)
        => (await _db.OperationalZones.AsNoTracking().ToListAsync(cancellationToken)).OrderBy(z => z.Code, StringComparer.Ordinal).ToList();

    public Task<OperationalZone?> GetZoneAsync(Guid zoneId, CancellationToken cancellationToken)
        => _db.OperationalZones.AsNoTracking().FirstOrDefaultAsync(z => z.Id == zoneId, cancellationToken);

    public Task<OperationalZone?> FindZoneByCodeAsync(string code, CancellationToken cancellationToken)
        => _db.OperationalZones.AsNoTracking().FirstOrDefaultAsync(z => z.Code == code, cancellationToken);

    public Task<OperationalZone?> FindZoneForUpdateAsync(Guid zoneId, CancellationToken cancellationToken)
        => _db.OperationalZones.FirstOrDefaultAsync(z => z.Id == zoneId, cancellationToken);

    public async Task<IReadOnlyList<OperationalZone>> GetZonesAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken)
    {
        var ids = zoneIds.ToList();
        return await _db.OperationalZones.AsNoTracking().Where(z => ids.Contains(z.Id)).ToListAsync(cancellationToken);
    }

    public void AddZone(OperationalZone zone) => _db.OperationalZones.Add(zone);

    public async Task<IReadOnlyList<ZoneStats>> ListZoneStatsAsync(CancellationToken cancellationToken)
    {
        var memberships = await _db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.RemovedAt == null)
            .Select(m => new { m.ZoneId, m.TechnicianUserId }).ToListAsync(cancellationToken);
        var communes = await _db.OperationalZoneCommunes.AsNoTracking().Where(m => m.RemovedAt == null)
            .GroupBy(m => m.ZoneId).Select(g => new { ZoneId = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var roleUsers = TechnicianRoleUserIds;
        var schedulable = (await _db.TechnicianProfiles.AsNoTracking()
                .Where(p => p.IsSchedulingActive && _db.UserAccounts.Any(u => u.Id == p.UserId && u.DeletedAt == null && u.Status == UserStatus.Active)
                            && roleUsers.Contains(p.UserId))
                .Select(p => p.UserId).ToListAsync(cancellationToken))
            .ToHashSet();
        return memberships.Select(m => m.ZoneId).Concat(communes.Select(c => c.ZoneId)).Distinct()
            .Select(zoneId =>
            {
                var members = memberships.Where(m => m.ZoneId == zoneId).Select(m => m.TechnicianUserId).Distinct().ToList();
                return new ZoneStats(zoneId, communes.FirstOrDefault(c => c.ZoneId == zoneId)?.Count ?? 0, members.Count, members.Count(schedulable.Contains));
            })
            .ToList();
    }

    /// <summary>Unfinished survey appointments: not cancelled/completed, not ended, and open ones only before their start (started open ones are auto-cancelled).</summary>
    private IQueryable<TechnicianAppointment> UnfinishedAppointments(DateTimeOffset nowUtc)
        => _db.TechnicianAppointments.Where(a => a.EndsAt > nowUtc
                                                 && a.Status != AppointmentStatuses.Cancelled && a.Status != AppointmentStatuses.Completed
                                                 && !((a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated)
                                                      && a.StartsAt <= nowUtc));

    private IQueryable<SmartSolar.Modules.Installations.InstallationProject> UnfinishedProjects
        => _db.InstallationProjects.Where(p => p.Status != SmartSolar.Modules.Installations.InstallationStatuses.Completed
                                               && p.Status != SmartSolar.Modules.Installations.InstallationStatuses.Cancelled);

    public async Task<ZoneUsage> GetZoneUsageAsync(Guid zoneId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var appointments = await UnfinishedAppointments(nowUtc).AsNoTracking().CountAsync(a => a.OperationalZoneId == zoneId, cancellationToken);
        var projects = !_includeInstallationAssignments
            ? 0
            : await UnfinishedProjects.AsNoTracking().CountAsync(p => p.OperationalZoneId == zoneId, cancellationToken);
        return new ZoneUsage(appointments, projects);
    }

    public async Task<IReadOnlyList<CommuneUsage>> GetCommuneUsageAsync(IReadOnlyCollection<string> communeCodes, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (communeCodes.Count == 0) return [];
        var codes = communeCodes.Distinct().ToList();
        var appointments = await UnfinishedAppointments(nowUtc).AsNoTracking()
            .Join(_db.PropertySites.Where(p => p.CommuneCode != null && codes.Contains(p.CommuneCode)), a => a.PropertySiteId, p => p.Id, (a, p) => p.CommuneCode!)
            .GroupBy(c => c).Select(g => new { Code = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var projects = !_includeInstallationAssignments
            ? []
            : await UnfinishedProjects.AsNoTracking()
                .Join(_db.PropertySites.Where(p => p.CommuneCode != null && codes.Contains(p.CommuneCode)), x => x.PropertySiteId, p => p.Id, (x, p) => p.CommuneCode!)
                .GroupBy(c => c).Select(g => new { Code = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return appointments.Select(a => a.Code).Concat(projects.Select(p => p.Code)).Distinct().OrderBy(c => c, StringComparer.Ordinal)
            .Select(code => new CommuneUsage(code, appointments.FirstOrDefault(a => a.Code == code)?.Count ?? 0, projects.FirstOrDefault(p => p.Code == code)?.Count ?? 0))
            .ToList();
    }

    public async Task<IReadOnlyList<ZoneCommuneRow>> ListZoneCommunesAsync(Guid zoneId, CancellationToken cancellationToken)
        => (await _db.OperationalZoneCommunes.AsNoTracking().Where(m => m.ZoneId == zoneId && m.RemovedAt == null)
                .Join(_db.AdministrativeCommunes, m => m.CommuneCode, c => c.Code, (m, c) => new { m, c })
                .ToListAsync(cancellationToken))
            .OrderBy(x => x.c.SearchName, StringComparer.Ordinal).ThenBy(x => x.c.Code, StringComparer.Ordinal)
            .Select(x => new ZoneCommuneRow(x.c.Code, x.c.Name, x.c.UnitType, x.c.ProvinceCode, x.m.AssignedAt, x.m.AssignedByUserId))
            .ToList();

    public async Task<IReadOnlyList<CommuneMappingRow>> FindCommuneMappingsAsync(IReadOnlyCollection<string> communeCodes, CancellationToken cancellationToken)
    {
        if (communeCodes.Count == 0) return [];
        var codes = communeCodes.Distinct().ToList();
        return await _db.OperationalZoneCommunes.AsNoTracking().Where(m => m.RemovedAt == null && codes.Contains(m.CommuneCode))
            .Join(_db.OperationalZones, m => m.ZoneId, z => z.Id, (m, z) => new CommuneMappingRow(m.CommuneCode, z.Id, z.Code, z.Name, z.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OperationalZoneCommune>> FindActiveMappingsForUpdateAsync(IReadOnlyCollection<Guid> zoneIds, CancellationToken cancellationToken)
    {
        var ids = zoneIds.Distinct().ToList();
        return await _db.OperationalZoneCommunes.Where(m => m.RemovedAt == null && ids.Contains(m.ZoneId)).ToListAsync(cancellationToken);
    }

    public void AddZoneCommune(OperationalZoneCommune mapping) => _db.OperationalZoneCommunes.Add(mapping);

    public async Task<IReadOnlyList<AdministrativeCommune>> ListUnmappedCommunesAsync(string? provinceCode, DateOnly asOf, CancellationToken cancellationToken)
    {
        var mapped = ActiveMappings.Select(m => m.CommuneCode);
        return (await _db.AdministrativeCommunes.AsNoTracking()
                .Where(c => c.ValidFrom <= asOf && (c.ValidTo == null || c.ValidTo > asOf) && (provinceCode == null || c.ProvinceCode == provinceCode)
                            && !mapped.Contains(c.Code))
                .ToListAsync(cancellationToken))
            .OrderBy(c => c.SearchName, StringComparer.Ordinal).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<ZoneMemberRow>> ListZoneMembersAsync(Guid zoneId, CancellationToken cancellationToken)
    {
        var roleUsers = TechnicianRoleUserIds;
        var rows = await _db.TechnicianZoneMemberships.AsNoTracking().Where(m => m.ZoneId == zoneId && m.RemovedAt == null)
            .Join(_db.UserAccounts.Where(u => u.DeletedAt == null), m => m.TechnicianUserId, u => u.Id, (m, u) => new
            {
                m.TechnicianUserId, u.FullName, IsActive = u.Status == UserStatus.Active, HasRole = roleUsers.Contains(u.Id),
                Scheduling = _db.TechnicianProfiles.Any(p => p.UserId == u.Id && p.IsSchedulingActive), m.AssignedAt,
            })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.FullName, StringComparer.Ordinal).ThenBy(r => r.TechnicianUserId)
            .Select(r => new ZoneMemberRow(r.TechnicianUserId, r.FullName, r.IsActive, r.HasRole, r.Scheduling, r.AssignedAt)).ToList();
    }

    public Task<bool> IsActiveUserWithRoleAsync(Guid userId, IReadOnlyCollection<string> roleCodes, CancellationToken cancellationToken)
    {
        var codes = roleCodes.ToList();
        return _db.UserAccounts.AsNoTracking().AnyAsync(u => u.Id == userId && u.DeletedAt == null && u.Status == UserStatus.Active
                                                             && u.UserRoles.Any(r => codes.Contains(r.Role.Code)), cancellationToken);
    }

    public async Task<IReadOnlyList<TechnicianZoneMembership>> FindActiveMembershipsForUpdateAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => await _db.TechnicianZoneMemberships.Where(m => m.TechnicianUserId == technicianUserId && m.RemovedAt == null).ToListAsync(cancellationToken);

    public void AddMembership(TechnicianZoneMembership membership) => _db.TechnicianZoneMemberships.Add(membership);

    // ---- Sites ----

    public Task<bool> SiteHasActiveAppointmentAsync(Guid propertySiteId, CancellationToken cancellationToken)
        => _db.TechnicianAppointments.AsNoTracking().AnyAsync(a => a.PropertySiteId == propertySiteId
            && (a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated
                || a.Status == AppointmentStatuses.Accepted || a.Status == AppointmentStatuses.Assigned), cancellationToken);

    public async Task<IReadOnlyList<TechnicianAppointment>> FindActiveSiteAppointmentsForUpdateAsync(Guid propertySiteId, CancellationToken cancellationToken)
        => await _db.TechnicianAppointments.Where(a => a.PropertySiteId == propertySiteId
            && (a.Status == AppointmentStatuses.PendingAcceptance || a.Status == AppointmentStatuses.Escalated
                || a.Status == AppointmentStatuses.Accepted || a.Status == AppointmentStatuses.Assigned)).ToListAsync(cancellationToken);

    public async Task<bool> SiteHasActiveInstallationProjectAsync(Guid propertySiteId, CancellationToken cancellationToken)
        => _includeInstallationAssignments
           && await _db.InstallationProjects.AsNoTracking().AnyAsync(p => p.PropertySiteId == propertySiteId
               && p.Status != SmartSolar.Modules.Installations.InstallationStatuses.Completed
               && p.Status != SmartSolar.Modules.Installations.InstallationStatuses.Cancelled, cancellationToken);

    public Task<PropertySite?> FindPropertySiteForUpdateAsync(Guid propertySiteId, CancellationToken cancellationToken)
        => _db.PropertySites.FirstOrDefaultAsync(p => p.Id == propertySiteId, cancellationToken);

    // ---- Commercial ownership ----

    public async Task<SalesOwnerUpdate> SetSalesOwnerAsync(Guid surveyRequestId, Guid saleUserId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        // PostgreSQL: the row lock serialises this with claims and other owner changes, so the owner read below is the one replaced.
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM survey_request WHERE id = {surveyRequestId} FOR UPDATE", cancellationToken);
        var row = await _db.SurveyRequests.AsNoTracking().Where(r => r.Id == surveyRequestId)
            .Select(r => new { r.AssignedSaleId }).FirstOrDefaultAsync(cancellationToken);
        if (row is null) return new SalesOwnerUpdate(false, false, null);
        var previous = row.AssignedSaleId;
        if (previous == saleUserId) return new SalesOwnerUpdate(true, true, previous);
        // Conditional on the owner just read: without the row lock (SQLite) a concurrent change makes this a no-op instead of
        // overwriting an owner the audit event would not name.
        var updated = await _db.SurveyRequests
            .Where(r => r.Id == surveyRequestId && r.AssignedSaleId == previous)
            .ExecuteUpdateAsync(x => x
                .SetProperty(r => r.AssignedSaleId, saleUserId)
                .SetProperty(r => r.AssignedAt, nowUtc), cancellationToken) == 1;
        return new SalesOwnerUpdate(true, updated, previous);
    }

    public void AddSurveyRequestEvent(SurveyRequestEvent surveyRequestEvent) => _db.SurveyRequestEvents.Add(surveyRequestEvent);
}
