using Microsoft.Extensions.Logging;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>A schedule conflict as Manager/Admin dispatch sees it.</summary>
public sealed record ScheduleConflictView(Guid ConflictId, Guid TechnicianUserId, string OverrunKind, Guid OverrunId, DateTimeOffset OverrunReservedEndsAt,
    string AffectedKind, Guid AffectedId, DateTimeOffset AffectedStartsAt, DateTimeOffset AffectedEndsAt, string Status, DateTimeOffset DetectedAt,
    DateTimeOffset? ResolvedAt, string? Resolution);

/// <summary>
/// Overtime protection (2026-11-25). Work in execution past its reserved end keeps occupying its technician (see <see cref="EffectiveBusy"/>), so no new
/// commitment can be taken in that time. Commitments that ALREADY exist in that time are never cancelled, moved or reassigned here: each collision is
/// recorded once as an OPEN <see cref="TechnicianScheduleConflict"/> (the Manager/Admin dispatch queue), the affected survey appointment gets an
/// OVERTIME_CONFLICT history event, and Managers/Admins are notified. Managers resolve with the existing override / reassign / reschedule actions; the
/// conflict closes by itself when the overrun ends or the commitment no longer collides.
/// <para>Runs per technician under the technician row lock that booking, acceptance, assignment, completion and planning also take, so it is safe
/// against concurrent workers and web requests; the unique (overrun, affected) index makes duplicate runs harmless.</para>
/// </summary>
public sealed class OvertimeConflictService(ISchedulingUnitOfWork unitOfWork, TechnicianEligibility eligibility, WorkingCalendar calendar, TimeProvider time,
    ILogger<OvertimeConflictService>? logger = null)
{
    private static readonly string[] DispatchRoles = [RoleCodes.Manager, RoleCodes.Admin];

    public async Task<int> SweepAsync(int limit, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var technicianUserId in await unitOfWork.ListTechniciansToCheckForOvertimeAsync(time.GetUtcNow(), limit, cancellationToken))
        {
            try
            {
                changed += await CheckTechnicianAsync(technicianUserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Ids only. A concurrent run that inserted the same pair first ends here (unique index): the next run sees its row.
                logger?.LogWarning("Overtime check of technician {TechnicianUserId} failed ({ErrorType}); retrying on the next run.", technicianUserId, ex.GetType().Name);
            }
        }
        return changed;
    }

    /// <summary>Re-evaluates one technician's conflicts now (in its own transaction). Returns the number of conflicts opened, re-opened or resolved.</summary>
    public async Task<int> CheckTechnicianAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => await unitOfWork.ExecuteInTransactionAsync<int>(async token =>
        {
            await unitOfWork.LockTechniciansAsync([technicianUserId], token);
            var now = time.GetUtcNow();
            var day = (await eligibility.LoadDaysAsync(calendar.LocalDateOf(now), new TechnicianScope([technicianUserId], SchedulableOnly: false), token)).SingleOrDefault();
            var stops = day?.Appointments ?? [];
            var overruns = stops.Where(s => s.InExecution).ToList();
            var pairs = overruns
                .SelectMany(o => stops.Where(c => !c.InExecution && c.AppointmentId != o.AppointmentId && c.EndsAtUtc > now
                                                  && EligibilityRules.Conflicts(o, c.StartsAtUtc, c.EndsAtUtc, eligibility.Buffer))
                    .Select(c => (Overrun: o, Affected: c)))
                .ToList();

            var existing = await unitOfWork.ListScheduleConflictsAsync(technicianUserId, null, true, token);
            var changes = 0;
            IReadOnlyList<Guid>? dispatchers = null;
            foreach (var (overrun, affected) in pairs)
            {
                var row = existing.FirstOrDefault(r => r.OverrunId == overrun.AppointmentId && r.AffectedId == affected.AppointmentId);
                if (row is { Status: ScheduleConflictStatuses.Open }) continue;
                if (row is null)
                {
                    row = new TechnicianScheduleConflict
                    {
                        Id = Guid.NewGuid(), TechnicianUserId = technicianUserId, OverrunKind = Kind(overrun), OverrunId = overrun.AppointmentId,
                        OverrunReservedEndsAt = await ReservedEndAsync(overrun, token), AffectedKind = Kind(affected), AffectedId = affected.AppointmentId,
                        AffectedStartsAt = affected.StartsAtUtc, AffectedEndsAt = affected.EndsAtUtc, DetectedAt = now, UpdatedAt = now,
                    };
                    unitOfWork.AddScheduleConflict(row);
                }
                else
                {
                    row.Status = ScheduleConflictStatuses.Open;
                    row.ResolvedAt = null;
                    row.Resolution = null;
                    row.DetectedAt = now;
                    row.UpdatedAt = now;
                    row.Revision++;
                }
                dispatchers ??= await unitOfWork.ListActiveUserIdsWithRolesAsync(DispatchRoles, token);
                var message = $"Kỹ thuật viên đang làm quá giờ ({Kind(overrun)}), trùng với lịch {Kind(affected)} lúc "
                              + $"{AppointmentHistory.When(affected.StartsAtUtc, calendar.TimeZone)}. Cần điều phối lại.";
                foreach (var recipient in dispatchers) unitOfWork.AddNotification(recipient, AppointmentEventTypes.OvertimeConflict, message, now);
                if (!affected.IsInstallation && await unitOfWork.FindAppointmentForUpdateAsync(affected.AppointmentId, token) is { } appointment)
                    AppointmentHistory.Record(unitOfWork, appointment, AppointmentEventTypes.OvertimeConflict, null, now,
                        reason: $"OVERTIME_CONFLICT: {Kind(overrun)} {overrun.AppointmentId} still in execution; sent to Manager/Admin dispatch",
                        notify: [technicianUserId]);
                changes++;
            }
            foreach (var open in existing.Where(r => r.Status == ScheduleConflictStatuses.Open
                                                     && !pairs.Any(p => p.Overrun.AppointmentId == r.OverrunId && p.Affected.AppointmentId == r.AffectedId)))
            {
                open.Status = ScheduleConflictStatuses.Resolved;
                open.ResolvedAt = now;
                open.Resolution = overruns.Any(o => o.AppointmentId == open.OverrunId) ? ScheduleConflictResolutions.CommitmentCleared : ScheduleConflictResolutions.OvertimeEnded;
                open.UpdatedAt = now;
                open.Revision++;
                changes++;
            }
            if (changes == 0) return (0, false);
            await unitOfWork.SaveChangesAsync(token);
            return (changes, true);
        }, cancellationToken);

    public async Task<IReadOnlyList<ScheduleConflictView>> ListAsync(Guid? technicianUserId, string? status, CancellationToken cancellationToken)
        => (await unitOfWork.ListScheduleConflictsAsync(technicianUserId, string.IsNullOrWhiteSpace(status) ? null : status.Trim().ToUpperInvariant(), false, cancellationToken))
            .Select(c => new ScheduleConflictView(c.Id, c.TechnicianUserId, c.OverrunKind, c.OverrunId, c.OverrunReservedEndsAt, c.AffectedKind, c.AffectedId,
                c.AffectedStartsAt, c.AffectedEndsAt, c.Status, c.DetectedAt, c.ResolvedAt, c.Resolution)).ToList();

    private static string Kind(ScheduledStop stop) => stop.IsInstallation ? ScheduleConflictKinds.InstallationAssignment : ScheduleConflictKinds.SurveyAppointment;

    /// <summary>The overrunning work's reserved end (the stop itself carries the overtime hold, not the reservation).</summary>
    private async Task<DateTimeOffset> ReservedEndAsync(ScheduledStop overrun, CancellationToken token)
        => !overrun.IsInstallation && await unitOfWork.GetAppointmentAsync(overrun.AppointmentId, token) is { } appointment
            ? appointment.EndsAt
            : await unitOfWork.GetInstallationAssignmentEndAsync(overrun.AppointmentId, token) ?? overrun.StartsAtUtc;
}
