using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Identity.Enums;
using SmartSolar.Modules.PreSurvey.Enums;
using SmartSolar.Modules.PreSurvey.GetSurveyRequestDetail;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Infrastructure.Persistence;

/// <summary>
/// Scheduling persistence. Concurrency is enforced in the database:
/// <list type="bullet">
/// <item>An exclusion constraint (migration SQL) forbids overlapping active appointments for the same technician.</item>
/// <item>A partial unique index allows one active appointment per survey request.</item>
/// <item>Every booking, assignment and reschedule row-locks the affected technicians' profiles, so
/// daily limits and travel feasibility are re-checked against a schedule nobody else can change.</item>
/// <item>Appointments carry a revision token, so a stale read can never overwrite a newer change.</item>
/// </list>
/// </summary>
public sealed class SchedulingUnitOfWork : ISchedulingUnitOfWork
{
    private const string ExclusionViolation = "23P01";
    private const string UniqueViolation = "23505";
    private const string ActiveSurveyRequestIndex = "ux_technician_appointment_active_survey_request";

    private readonly AppDbContext _db;
    private readonly bool _includeInstallationShifts;

    /// <param name="includeInstallationShifts">
    /// True once the Installation Flow tables exist (Features:InstallationWorkflowEnabled): installation shift assignments are
    /// then part of every technician day state, so survey bookings and installation planning share
    /// one conflict and travel model. False keeps Flow 1 independent of tables that may not exist yet.
    /// </param>
    public SchedulingUnitOfWork(AppDbContext db, bool includeInstallationShifts = false)
    {
        _db = db;
        _includeInstallationShifts = includeInstallationShifts;
    }

    private IQueryable<TechnicianAppointment> Active => _db.TechnicianAppointments.Where(a => a.Status != AppointmentStatuses.Cancelled);

    private static AppointmentSnapshot Snapshot(TechnicianAppointment a) => new(
        a.Id, a.AppointmentType, a.SurveyRequestId, a.PropertySiteId, a.StartsAt, a.EndsAt, a.ReservedTechnicianUserId,
        a.AssignedTechnicianUserId, a.AssignedBySaleUserId, a.Status, a.CustomerNote, a.BookedAt, a.AssignedAt, a.AcknowledgedAt, a.Revision);

    public async Task<PropertySiteLocation?> GetPropertySiteAsync(Guid propertySiteId, CancellationToken cancellationToken)
    {
        var row = await _db.PropertySites.AsNoTracking()
            .Where(p => p.Id == propertySiteId)
            .Select(p => new { p.Id, p.Customer.UserId, p.Latitude, p.Longitude })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : new PropertySiteLocation(row.Id, row.UserId, GeoPoint.From(row.Latitude, row.Longitude));
    }

    public async Task<SurveyRequestSchedulingContext?> GetSurveyRequestContextAsync(Guid surveyRequestId, CancellationToken cancellationToken)
    {
        var row = await _db.SurveyRequests.AsNoTracking()
            .Where(x => x.Id == surveyRequestId)
            .Select(x => new
            {
                x.Id,
                x.PreSurveyId,
                x.PreSurvey.PropertyId,
                x.PreSurvey.Property.Customer.UserId,
                x.AssignedSaleId,
                x.Status,
                x.PreSurvey.Property.Latitude,
                x.PreSurvey.Property.Longitude,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return null;

        var active = await Active.AsNoTracking().Where(a => a.SurveyRequestId == surveyRequestId).FirstOrDefaultAsync(cancellationToken);
        return new SurveyRequestSchedulingContext(row.Id, row.PreSurveyId, row.PropertyId, row.UserId, row.AssignedSaleId, row.Status,
            GeoPoint.From(row.Latitude, row.Longitude), active is null ? null : Snapshot(active));
    }

    public async Task<IReadOnlyList<TechnicianDayState>> GetTechnicianDayStatesAsync(
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc, Guid? onlyTechnicianId, CancellationToken cancellationToken)
    {
        var technicianRoleUserIds = _db.UserRoles.Where(ur => ur.Role.Code == RoleCodes.Technician).Select(ur => ur.UserId);

        var technicians = await _db.TechnicianProfiles.AsNoTracking()
            .Where(p => p.IsSchedulingActive && p.BaseLatitude != null && p.BaseLongitude != null
                        && (onlyTechnicianId == null || p.UserId == onlyTechnicianId))
            .Join(_db.UserAccounts.Where(u => u.Status == UserStatus.Active && u.DeletedAt == null && technicianRoleUserIds.Contains(u.Id)),
                p => p.UserId, u => u.Id, (p, u) => new { Profile = p, u.FullName })
            .OrderBy(x => x.Profile.UserId)
            .ToListAsync(cancellationToken);
        if (technicians.Count == 0) return [];

        var ids = technicians.Select(t => t.Profile.UserId).ToList();

        var appointments = await Active.AsNoTracking()
            .Where(a => ids.Contains(a.ReservedTechnicianUserId) && a.StartsAt < windowEndUtc && a.EndsAt > windowStartUtc)
            .Join(_db.PropertySites, a => a.PropertySiteId, p => p.Id,
                (a, p) => new { a.Id, a.ReservedTechnicianUserId, a.StartsAt, a.EndsAt, p.Latitude, p.Longitude })
            .ToListAsync(cancellationToken);

        var blocked = await _db.TechnicianUnavailablePeriods.AsNoTracking()
            .Where(b => ids.Contains(b.TechnicianUserId) && b.StartsAt < windowEndUtc && b.EndsAt > windowStartUtc)
            .Select(b => new { b.TechnicianUserId, b.StartsAt, b.EndsAt })
            .ToListAsync(cancellationToken);

        var installation = !_includeInstallationShifts
            ? []
            : await _db.InstallationShiftAssignments.AsNoTracking()
                .Where(s => ids.Contains(s.TechnicianUserId) && s.Status == "ACTIVE" && s.StartsAt < windowEndUtc && s.EndsAt > windowStartUtc)
                .Join(_db.InstallationProjects, s => s.ProjectId, p => p.Id, (s, p) => new { s.Id, s.TechnicianUserId, s.StartsAt, s.EndsAt, p.PropertySiteId })
                .Join(_db.PropertySites, x => x.PropertySiteId, site => site.Id, (x, site) => new { x.Id, x.TechnicianUserId, x.StartsAt, x.EndsAt, site.Latitude, site.Longitude })
                .ToListAsync(cancellationToken);

        var customIds = technicians.Where(t => t.Profile.HasCustomWorkingSchedule).Select(t => t.Profile.UserId).ToList();
        var weekly = customIds.Count == 0
            ? []
            : await _db.TechnicianWorkingPeriods.AsNoTracking().Where(w => customIds.Contains(w.TechnicianUserId)).ToListAsync(cancellationToken);

        return technicians.Select(t => new TechnicianDayState(
            t.Profile.UserId,
            t.FullName,
            GeoPoint.From(t.Profile.BaseLatitude, t.Profile.BaseLongitude),
            appointments.Where(a => a.ReservedTechnicianUserId == t.Profile.UserId)
                .Select(a => new ScheduledStop(a.Id, a.StartsAt, a.EndsAt, GeoPoint.From(a.Latitude, a.Longitude)))
                .Concat(installation.Where(s => s.TechnicianUserId == t.Profile.UserId)
                    .Select(s => new ScheduledStop(s.Id, s.StartsAt, s.EndsAt, GeoPoint.From(s.Latitude, s.Longitude), CountsTowardDailyLimit: false)))
                .OrderBy(a => a.StartsAtUtc)
                .ToList(),
            blocked.Where(b => b.TechnicianUserId == t.Profile.UserId).Select(b => new BlockedPeriod(b.StartsAt, b.EndsAt)).ToList(),
            t.Profile.MaxAppointmentsPerDay,
            t.Profile.HasCustomWorkingSchedule
                ? weekly.Where(w => w.TechnicianUserId == t.Profile.UserId).Select(w => new WeeklyPeriod((DayOfWeek)w.DayOfWeek, w.StartTime, w.EndTime)).ToList()
                : null,
            t.Profile.EarliestBaseDeparture)).ToList();
    }

    public async Task<TechnicianIdentity?> GetTechnicianAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await _db.UserAccounts.AsNoTracking()
            .Where(u => u.Id == userId && u.DeletedAt == null)
            .Select(u => new
            {
                u.Id,
                u.FullName,
                IsActive = u.Status == UserStatus.Active,
                HasRole = u.UserRoles.Any(r => r.Role.Code == RoleCodes.Technician),
            })
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
            .OrderBy(a => a.BookedAt).Select(Snapshot).ToList();

    public async Task<IReadOnlyList<(AppointmentSnapshot Appointment, string PropertyName, string Province, string? District)>> ListTechnicianAppointmentsAsync(
        Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
    {
        var rows = await _db.TechnicianAppointments.AsNoTracking()
            .Where(a => a.AssignedTechnicianUserId == technicianUserId
                        && (a.Status == AppointmentStatuses.Assigned || a.Status == AppointmentStatuses.Acknowledged)
                        && a.EndsAt >= fromUtc)
            .Join(_db.PropertySites, a => a.PropertySiteId, p => p.Id, (a, p) => new { a, p.Name, p.Province, p.District })
            .ToListAsync(cancellationToken);
        return rows.OrderBy(r => r.a.StartsAt).Select(r => (Snapshot(r.a), r.Name, r.Province, r.District)).ToList();
    }

    public async Task<TechnicianJobDetail?> GetTechnicianJobAsync(Guid technicianUserId, Guid appointmentId, CancellationToken cancellationToken)
    {
        var appointment = await _db.TechnicianAppointments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == appointmentId
                                      && a.AssignedTechnicianUserId == technicianUserId
                                      && (a.Status == AppointmentStatuses.Assigned || a.Status == AppointmentStatuses.Acknowledged)
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
            simulation = await _db.SolarSimulations.AsNoTracking()
                .Where(s => s.Id == selectedId && s.PreSurveyId == job.PreSurveyId)
                .Select(s => new SelectedSimulationSummary(s.Id, s.Status, s.EnergyStatus, s.PreSurveyGeometryVersion != job.GeometryVersion,
                    s.MountingType, s.ProductSku, s.ProductName, s.PanelCount, s.InstalledCapacityKwp, s.AnnualEnergyKwh, s.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
        }

        return new TechnicianJobDetail(Snapshot(appointment), job.Id, job.Status, job.FullName, job.Phone, job.Email,
            job.PropertyId, job.PropertyName, job.Province, job.District, job.Ward, job.StreetLine, job.Latitude, job.Longitude,
            job.InstallationSurfaceType, job.SurfaceMaterial, job.PreSurveyId, job.TotalAreaM2, job.UsableAreaM2, job.SurfaceLengthM,
            job.SurfaceWidthM, job.TiltDegree, job.AzimuthDegree, job.HasObstruction, job.Obstacles, simulation);
    }

    public async Task<TechnicianTravelEstimate?> GetLatestInboundEstimateAsync(Guid appointmentId, CancellationToken cancellationToken)
        => (await _db.TechnicianTravelEstimates.AsNoTracking().Where(e => e.AppointmentId == appointmentId && e.SupersededAt == null).ToListAsync(cancellationToken))
            .OrderByDescending(e => e.EstimatedAt).FirstOrDefault();

    public async Task<IReadOnlyList<ScheduleEntry>> GetTechnicianScheduleAsync(
        Guid technicianUserId, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc, CancellationToken cancellationToken)
    {
        var rows = await Active.AsNoTracking()
            .Where(a => a.ReservedTechnicianUserId == technicianUserId && a.StartsAt < dayEndUtc && a.EndsAt > dayStartUtc)
            .Join(_db.PropertySites, a => a.PropertySiteId, p => p.Id, (a, p) => new { a, p.Name, p.Latitude, p.Longitude })
            .ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.a.Id).ToList();
        // Superseded estimates are history: they never describe the current route.
        var estimates = await _db.TechnicianTravelEstimates.AsNoTracking().Where(e => ids.Contains(e.AppointmentId) && e.SupersededAt == null).ToListAsync(cancellationToken);

        return rows.OrderBy(r => r.a.StartsAt)
            .Select(r => new ScheduleEntry(Snapshot(r.a), r.Name, GeoPoint.From(r.Latitude, r.Longitude),
                estimates.Where(e => e.AppointmentId == r.a.Id).OrderByDescending(e => e.EstimatedAt).FirstOrDefault()))
            .ToList();
    }

    public async Task<IReadOnlyList<TechnicianListItem>> ListTechniciansAsync(CancellationToken cancellationToken)
    {
        var users = await _db.UserAccounts.AsNoTracking()
            .Where(u => u.DeletedAt == null && u.UserRoles.Any(r => r.Role.Code == RoleCodes.Technician))
            .Select(u => new { u.Id, u.FullName, u.Phone, IsActive = u.Status == UserStatus.Active })
            .ToListAsync(cancellationToken);
        var ids = users.Select(u => u.Id).ToList();
        var profiles = await _db.TechnicianProfiles.AsNoTracking().Where(p => ids.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, cancellationToken);
        return users.OrderBy(u => u.FullName)
            .Select(u => new TechnicianListItem(u.Id, u.FullName, u.Phone, u.IsActive, profiles.GetValueOrDefault(u.Id)))
            .ToList();
    }

    public Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<(T Result, bool Commit)>> work, CancellationToken cancellationToken)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(cancellationToken, async token =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(token);
            var (result, commit) = await work(token);
            if (commit)
            {
                await transaction.CommitAsync(token);
            }
            else
            {
                await transaction.RollbackAsync(token);
            }
            _db.ChangeTracker.Clear();
            return result;
        });
    }

    public async Task LockTechniciansAsync(IEnumerable<Guid> technicianUserIds, CancellationToken cancellationToken)
    {
        if (!_db.Database.IsNpgsql()) return;  // SQLite: BEGIN IMMEDIATE already serialises writers.

        foreach (var id in technicianUserIds.Distinct().OrderBy(id => id))
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM technician_profile WHERE user_id = {id} FOR UPDATE", cancellationToken);
        }
    }

    public Task<TechnicianAppointment?> FindAppointmentForUpdateAsync(Guid appointmentId, CancellationToken cancellationToken)
        => _db.TechnicianAppointments.FirstOrDefaultAsync(a => a.Id == appointmentId, cancellationToken);

    public void AddAppointment(TechnicianAppointment appointment) => _db.TechnicianAppointments.Add(appointment);

    public void AddTravelEstimates(IEnumerable<TechnicianTravelEstimate> estimates) => _db.TechnicianTravelEstimates.AddRange(estimates);

    public async Task<bool> TryMarkSurveyRequestScheduledAsync(Guid surveyRequestId, Guid saleUserId, DateTimeOffset scheduledAt, CancellationToken cancellationToken)
        => await _db.SurveyRequests
            .Where(x => x.Id == surveyRequestId && x.AssignedSaleId == saleUserId
                        && (x.Status == SurveyRequestStatus.Assigned || x.Status == SurveyRequestStatus.Scheduled))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Status, SurveyRequestStatus.Scheduled)
                .SetProperty(x => x.ScheduledAt, scheduledAt), cancellationToken) == 1;

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
            throw;
        }
    }

    // ---- Route recalculation ----

    public async Task RequestRecalculationAsync(Guid technicianUserId, DateOnly localDate, string reason, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var row = await _db.TechnicianScheduleRecalculations.FirstOrDefaultAsync(r => r.TechnicianUserId == technicianUserId && r.LocalDate == localDate, cancellationToken);
        if (row is null)
        {
            _db.TechnicianScheduleRecalculations.Add(new TechnicianScheduleRecalculation
            {
                TechnicianUserId = technicianUserId, LocalDate = localDate, Status = RecalculationStatuses.Pending, Reason = reason,
                RequestedAt = nowUtc, Attempts = 0, NextAttemptAt = nowUtc, UpdatedAt = nowUtc,
            });
            return;
        }

        // A newer change: a fresh request version that is due now. A running lease keeps going but
        // will not delete this version when it completes.
        row.Status = RecalculationStatuses.Pending;
        row.Reason = reason;
        row.RequestedAt = nowUtc > row.RequestedAt ? nowUtc : row.RequestedAt.AddTicks(1);
        row.Attempts = 0;
        row.NextAttemptAt = nowUtc;
        row.LastError = null;
        row.UpdatedAt = nowUtc;
    }

    public async Task<RecalculationLease?> TryLeaseRecalculationAsync(Guid technicianUserId, DateOnly localDate, DateTimeOffset nowUtc, DateTimeOffset leaseUntilUtc, CancellationToken cancellationToken)
    {
        // Conditional UPDATE: of two concurrent workers only one sees a free, due row.
        var leased = await _db.TechnicianScheduleRecalculations
            .Where(r => r.TechnicianUserId == technicianUserId && r.LocalDate == localDate && r.NextAttemptAt <= nowUtc
                        && (r.LockedUntil == null || r.LockedUntil < nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.LockedUntil, leaseUntilUtc).SetProperty(r => r.UpdatedAt, nowUtc), cancellationToken);
        if (leased != 1) return null;

        var row = await _db.TechnicianScheduleRecalculations.AsNoTracking()
            .FirstAsync(r => r.TechnicianUserId == technicianUserId && r.LocalDate == localDate, cancellationToken);
        return new RecalculationLease(row.TechnicianUserId, row.LocalDate, row.RequestedAt, row.Attempts);
    }

    public async Task CompleteRecalculationAsync(RecalculationLease lease, CancellationToken cancellationToken)
    {
        var deleted = await _db.TechnicianScheduleRecalculations
            .Where(r => r.TechnicianUserId == lease.TechnicianUserId && r.LocalDate == lease.LocalDate && r.RequestedAt == lease.RequestedAt)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            await _db.TechnicianScheduleRecalculations
                .Where(r => r.TechnicianUserId == lease.TechnicianUserId && r.LocalDate == lease.LocalDate)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LockedUntil, (DateTimeOffset?)null), cancellationToken);
        }
    }

    public async Task FailRecalculationAsync(RecalculationLease lease, string error, DateTimeOffset nextAttemptAtUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var message = error.Length <= 200 ? error : error[..200];
        var updated = await _db.TechnicianScheduleRecalculations
            .Where(r => r.TechnicianUserId == lease.TechnicianUserId && r.LocalDate == lease.LocalDate && r.RequestedAt == lease.RequestedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RecalculationStatuses.Failed)
                .SetProperty(r => r.Attempts, r => r.Attempts + 1)
                .SetProperty(r => r.NextAttemptAt, nextAttemptAtUtc)
                .SetProperty(r => r.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(r => r.LastError, message)
                .SetProperty(r => r.UpdatedAt, nowUtc), cancellationToken);
        if (updated == 0)
        {
            await _db.TechnicianScheduleRecalculations
                .Where(r => r.TechnicianUserId == lease.TechnicianUserId && r.LocalDate == lease.LocalDate)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LockedUntil, (DateTimeOffset?)null), cancellationToken);
        }
    }

    public async Task<IReadOnlyList<(Guid TechnicianUserId, DateOnly LocalDate)>> ListDueRecalculationsAsync(DateTimeOffset nowUtc, int limit, CancellationToken cancellationToken)
        => (await _db.TechnicianScheduleRecalculations.AsNoTracking()
                .Where(r => r.NextAttemptAt <= nowUtc && (r.LockedUntil == null || r.LockedUntil < nowUtc))
                .OrderBy(r => r.NextAttemptAt)
                .Take(limit)
                .Select(r => new { r.TechnicianUserId, r.LocalDate })
                .ToListAsync(cancellationToken))
            .Select(r => (r.TechnicianUserId, r.LocalDate)).ToList();

    public Task<TechnicianScheduleRecalculation?> GetRecalculationAsync(Guid technicianUserId, DateOnly localDate, CancellationToken cancellationToken)
        => _db.TechnicianScheduleRecalculations.AsNoTracking().FirstOrDefaultAsync(r => r.TechnicianUserId == technicianUserId && r.LocalDate == localDate, cancellationToken);

    public async Task SupersedeEstimatesAsync(IReadOnlyCollection<Guid> appointmentIds, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (appointmentIds.Count == 0) return;
        var ids = appointmentIds.Distinct().ToList();
        await _db.TechnicianTravelEstimates
            .Where(e => ids.Contains(e.AppointmentId) && e.SupersededAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.SupersededAt, nowUtc), cancellationToken);
    }

    // ---- History / notifications ----

    public void AddAppointmentEvent(TechnicianAppointmentEvent appointmentEvent) => _db.TechnicianAppointmentEvents.Add(appointmentEvent);

    public void AddNotification(SchedulingNotification notification) => _db.SchedulingNotifications.Add(notification);

    public async Task<IReadOnlyList<TechnicianAppointmentEvent>> ListAppointmentEventsAsync(Guid surveyRequestId, CancellationToken cancellationToken)
        => (await _db.TechnicianAppointmentEvents.AsNoTracking().Where(e => e.SurveyRequestId == surveyRequestId).ToListAsync(cancellationToken))
            .OrderBy(e => e.CreatedAt).ToList();

    // ---- Technician configuration ----

    public async Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingInstallationAssignmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
        => !_includeInstallationShifts
            ? []
            : (await _db.InstallationShiftAssignments.AsNoTracking()
                    .Where(a => a.TechnicianUserId == technicianUserId && a.Status == "ACTIVE" && a.EndsAt > fromUtc)
                    .Select(a => new { a.Id, a.StartsAt, a.EndsAt })
                    .ToListAsync(cancellationToken))
                .OrderBy(a => a.StartsAt)
                .Select(a => new UpcomingAppointment(a.Id, null, a.StartsAt, a.EndsAt, "INSTALLATION_SHIFT"))
                .ToList();

    public async Task<IReadOnlyList<UpcomingAppointment>> ListUpcomingAppointmentsAsync(Guid technicianUserId, DateTimeOffset fromUtc, CancellationToken cancellationToken)
        => (await Active.AsNoTracking()
                .Where(a => a.ReservedTechnicianUserId == technicianUserId && a.EndsAt > fromUtc)
                .Select(a => new { a.Id, a.SurveyRequestId, a.StartsAt, a.EndsAt, a.Status })
                .ToListAsync(cancellationToken))
            .OrderBy(a => a.StartsAt)
            .Select(a => new UpcomingAppointment(a.Id, a.SurveyRequestId, a.StartsAt, a.EndsAt, a.Status))
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
}
