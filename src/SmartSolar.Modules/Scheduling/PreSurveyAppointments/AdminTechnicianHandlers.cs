using FluentValidation;
using SmartSolar.Modules.Identity.Constants;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Zones;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed record UpsertTechnicianProfileCommand(Guid TechnicianUserId, bool IsSchedulingActive, int? MaxAppointmentsPerDay, Guid? ActorUserId = null);

public sealed record AddUnavailablePeriodCommand(Guid ActorUserId, Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record WorkingPeriodInput(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary><see cref="UseDefault"/> = true removes the custom week; otherwise <see cref="Periods"/> is the full week (missing days are days off).</summary>
public sealed record SetWorkingScheduleCommand(Guid TechnicianUserId, bool UseDefault, IReadOnlyList<WorkingPeriodInput> Periods, Guid? ActorUserId = null);

/// <summary>Replaces the technician's zone set (removed memberships are soft-removed and kept as history).</summary>
public sealed record SetTechnicianZonesCommand(Guid ActorUserId, Guid TechnicianUserId, IReadOnlyList<Guid> ZoneIds);

public sealed record TechnicianProfileView(Guid TechnicianUserId, string FullName, string? Phone, bool IsActiveUser, bool IsSchedulingActive,
    int? MaxAppointmentsPerDay, bool ReadyForScheduling, bool HasCustomWorkingSchedule, IReadOnlyList<ZoneView> Zones);

public sealed record UnavailablePeriodView(Guid Id, Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record WorkingPeriodView(string Day, string Start, string End);

public sealed record WorkingScheduleView(Guid TechnicianUserId, bool UsesDefault, string TimeZone, IReadOnlyList<WorkingPeriodView> Periods);

/// <summary>An existing confirmed appointment that a configuration change would make impossible.</summary>
public sealed record ConflictingAppointmentView(Guid AppointmentId, Guid? SurveyRequestId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, string Problem);

public sealed record TechnicianZonesView(Guid TechnicianUserId, IReadOnlyList<ZoneView> Zones, IReadOnlyList<RehomeResult> RehomedHolds);

public sealed class UpsertTechnicianProfileCommandValidator : AbstractValidator<UpsertTechnicianProfileCommand>
{
    public UpsertTechnicianProfileCommandValidator()
    {
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.MaxAppointmentsPerDay).InclusiveBetween(1, 20).When(x => x.MaxAppointmentsPerDay.HasValue);
    }
}

public sealed class AddUnavailablePeriodCommandValidator : AbstractValidator<AddUnavailablePeriodCommand>
{
    public AddUnavailablePeriodCommandValidator()
    {
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.EndsAt).GreaterThan(x => x.StartsAt);
        RuleFor(x => x).Must(x => x.EndsAt - x.StartsAt <= TimeSpan.FromDays(366)).WithMessage("A period may be at most one year.").OverridePropertyName("EndsAt");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class SetWorkingScheduleCommandValidator : AbstractValidator<SetWorkingScheduleCommand>
{
    public SetWorkingScheduleCommandValidator()
    {
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.Periods).NotNull();
        RuleFor(x => x.Periods).Must(p => p.Count <= 42).WithMessage("At most 42 periods per week.");
        RuleForEach(x => x.Periods).Must(p => p.End > p.Start).WithMessage("Each period must end after it starts.");
        RuleFor(x => x.Periods).Must(NoOverlap).When(x => x.Periods is not null).WithMessage("Working periods on the same day must not overlap.");
        RuleFor(x => x).Must(x => x.UseDefault || x.Periods.Count > 0)
            .WithMessage("Provide at least one working period, or set useDefault to true.").OverridePropertyName("Periods");
    }

    private static bool NoOverlap(IReadOnlyList<WorkingPeriodInput> periods)
        => periods.GroupBy(p => p.Day).All(day =>
        {
            var ordered = day.OrderBy(p => p.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].Start < ordered[i - 1].End) return false;
            return true;
        });
}

public sealed class SetTechnicianZonesCommandValidator : AbstractValidator<SetTechnicianZonesCommand>
{
    public SetTechnicianZonesCommandValidator()
    {
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.ZoneIds).NotNull();
        RuleFor(x => x.ZoneIds).Must(ids => ids.Count <= 200).WithMessage("At most 200 zones.");
        RuleForEach(x => x.ZoneIds).NotEmpty();
    }
}

/// <summary>
/// Technician scheduling setup for ADMIN/MANAGER: profile (scheduling flag, daily cap), operational zones, custom
/// weekly working schedule, and leave. A change that would strand a confirmed (ACCEPTED/ASSIGNED) future
/// appointment is refused with TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS listing them; pending capacity holds
/// that the change invalidates (including holds above a lowered daily limit) are moved to another eligible technician
/// automatically, or the appointment escalates without a holder. Every change runs under the technician lock, serialised
/// with bookings.
/// </summary>
public sealed class AdminTechnicianHandlers(
    ISchedulingUnitOfWork unitOfWork,
    AppointmentAssignmentService assignments,
    WorkingCalendar calendar,
    SchedulingOptions options,
    TimeProvider time)
{
    /// <summary>
    /// Installation work that pins the technician's scheduling and zones: upcoming current assignments plus those of started,
    /// unfinished projects (IN_PROGRESS / REWORK_REQUIRED / PENDING_CUSTOMER_ACCEPTANCE) even after their interval ended.
    /// </summary>
    private async Task<IReadOnlyList<UpcomingAppointment>> InstallationWorkAsync(Guid technicianUserId, DateTimeOffset now, CancellationToken token)
        => (await unitOfWork.ListUpcomingInstallationAssignmentsAsync(technicianUserId, now, token))
            .Concat(await unitOfWork.ListStartedInstallationAssignmentsAsync(technicianUserId, token))
            .DistinctBy(a => a.AppointmentId).OrderBy(a => a.StartsAt).ToList();

    public async Task<IReadOnlyList<TechnicianProfileView>> ListAsync(Guid? zoneId, CancellationToken cancellationToken)
        => (await unitOfWork.ListTechniciansAsync(zoneId, cancellationToken))
            .Select(t => View(t.UserId, t.FullName, t.Phone, t.IsActiveUser, t.Profile, t.Zones)).ToList();

    public async Task<SchedulingResult<TechnicianProfileView>> UpsertProfileAsync(UpsertTechnicianProfileCommand command, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return SchedulingResult<TechnicianProfileView>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");

        var outcome = await WithHoldsAsync<TechnicianProfile>(command.TechnicianUserId, async (upcoming, now, token) =>
        {
            if (command.IsSchedulingActive) return await DailyLimitAsync<TechnicianProfile>(command.TechnicianUserId, command.MaxAppointmentsPerDay, upcoming, now, token);
            var confirmed = upcoming.Where(a => AppointmentStatuses.IsConfirmed(a.Status)).Select(a => Conflict(a, EligibilityReasons.SchedulingDisabled)).ToList();
            if (confirmed.Count > 0)
                return (null, Conflicts<TechnicianProfile>(confirmed, "The technician has confirmed upcoming appointments. Reassign them before disabling scheduling."));
            // Installation assignments (Installation Flow) are made under the same technician lock, so this check cannot race an assignment.
            var installation = await InstallationWorkAsync(command.TechnicianUserId, now, token);
            if (installation.Count > 0)
                return (null, Conflicts<TechnicianProfile>(installation.Select(a => Conflict(a, EligibilityReasons.SchedulingDisabled)),
                    "The technician is the responsible technician of upcoming installation work. Reassign the installations before disabling scheduling."));
            return (upcoming.Where(a => AppointmentStatuses.IsOpen(a.Status)), null);
        }, async (now, token) =>
        {
            var existing = await unitOfWork.FindProfileForUpdateAsync(command.TechnicianUserId, token);
            if (existing is null)
            {
                existing = new TechnicianProfile { UserId = command.TechnicianUserId, CreatedAt = now };
                unitOfWork.AddProfile(existing);
            }
            existing.IsSchedulingActive = command.IsSchedulingActive;
            existing.MaxAppointmentsPerDay = command.MaxAppointmentsPerDay;
            existing.UpdatedAt = now;
            return existing;
        }, command.ActorUserId, "TECHNICIAN_SCHEDULING_DISABLED", cancellationToken);

        if (!outcome.Succeeded) return SchedulingResult<TechnicianProfileView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        var zones = await unitOfWork.ListTechnicianZonesAsync(command.TechnicianUserId, cancellationToken);
        return SchedulingResult<TechnicianProfileView>.Ok(View(technician.UserId, technician.FullName, null, technician.IsActiveUser, outcome.Value, zones));
    }

    /// <summary>
    /// Replaces the zone set (a technician may belong to several zones). Refused (409 TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS, with the
    /// appointments) when a confirmed future survey appointment or installation work would no longer be in one of the technician's
    /// active zones; pending holds no longer covered are re-homed or escalated. Removed memberships are kept as history.
    /// Scope: an Admin may change any membership; a Manager only adds or removes memberships of zones whose responsible manager
    /// (<c>operational_zone.responsible_manager_user_id</c>) they are — memberships of other zones must stay exactly as they are
    /// (403 ZONE_SCOPE_FORBIDDEN). Checked against the actor's current roles inside the transaction, under the technician lock.
    /// </summary>
    public async Task<SchedulingResult<TechnicianZonesView>> SetZonesAsync(SetTechnicianZonesCommand command, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return SchedulingResult<TechnicianZonesView>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");
        if (technician.Profile is null)
            return SchedulingResult<TechnicianZonesView>.Fail(SchedulingErrorCodes.TechnicianProfileRequired, "Configure the technician profile (PUT …/profile) first.");

        var wanted = command.ZoneIds.Distinct().ToList();
        var zones = await unitOfWork.GetZonesAsync(wanted, cancellationToken);
        if (zones.Count != wanted.Count)
            return SchedulingResult<TechnicianZonesView>.Fail(SchedulingErrorCodes.ZoneNotFound, "One or more operational zones were not found.");
        var activeWanted = zones.Where(z => z.IsActive).Select(z => z.Id).ToHashSet();

        var rehomed = new List<RehomeResult>();
        var outcome = await WithHoldsAsync<bool>(command.TechnicianUserId, async (upcoming, now, token) =>
        {
            if (await ScopeErrorAsync(command.ActorUserId, command.TechnicianUserId, wanted, token) is { } scopeError) return (null, scopeError);
            var conflicts = new List<ConflictingAppointmentView>();
            var holds = new List<UpcomingAppointment>();
            foreach (var a in upcoming.Where(a => a.PropertySiteId is not null))
            {
                var coverage = await unitOfWork.GetSiteCoverageAsync(a.PropertySiteId!.Value, token);
                if (coverage.ZoneIds.Any(activeWanted.Contains)) continue;
                if (AppointmentStatuses.IsConfirmed(a.Status)) conflicts.Add(Conflict(a, EligibilityReasons.OutsideZone));
                else holds.Add(a);
            }
            // Installation assignments are never moved automatically: one outside the new zones refuses the change.
            foreach (var a in await InstallationWorkAsync(command.TechnicianUserId, now, token))
            {
                if (a.PropertySiteId is not { } site || (await unitOfWork.GetSiteCoverageAsync(site, token)).ZoneIds.Any(activeWanted.Contains)) continue;
                conflicts.Add(Conflict(a, EligibilityReasons.OutsideZone));
            }
            return conflicts.Count > 0
                ? (null, Conflicts<bool>(conflicts, "The technician has confirmed upcoming appointments in the zones being removed. Reassign them first."))
                : (holds, null);
        }, async (now, token) =>
        {
            var current = await unitOfWork.FindActiveMembershipsForUpdateAsync(command.TechnicianUserId, token);
            foreach (var membership in current.Where(m => !wanted.Contains(m.ZoneId)))
            {
                membership.RemovedAt = now;
                membership.RemovedByUserId = command.ActorUserId;
            }
            // Soft removals first: the partial unique index only counts active memberships.
            await unitOfWork.SaveChangesAsync(token);
            foreach (var zoneId in wanted.Where(id => current.All(m => m.ZoneId != id)))
                unitOfWork.AddMembership(new TechnicianZoneMembership
                {
                    Id = Guid.NewGuid(), TechnicianUserId = command.TechnicianUserId, ZoneId = zoneId,
                    AssignedByUserId = command.ActorUserId, AssignedAt = now,
                });
            return true;
        }, command.ActorUserId, "TECHNICIAN_ZONES_CHANGED", cancellationToken, rehomed);

        if (!outcome.Succeeded) return SchedulingResult<TechnicianZonesView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        var memberships = await unitOfWork.ListTechnicianZonesAsync(command.TechnicianUserId, cancellationToken);
        return SchedulingResult<TechnicianZonesView>.Ok(new TechnicianZonesView(command.TechnicianUserId,
            memberships.Select(m => ZoneView.From(m)!).ToList(), rehomed));
    }

    /// <summary>Null when the actor may replace the technician's current memberships with <paramref name="wanted"/>.</summary>
    private async Task<SchedulingResult<bool>?> ScopeErrorAsync(Guid actorUserId, Guid technicianUserId, IReadOnlyCollection<Guid> wanted, CancellationToken token)
    {
        if (await unitOfWork.IsActiveUserWithRoleAsync(actorUserId, [RoleCodes.Admin], token)) return null;
        if (!await unitOfWork.IsActiveUserWithRoleAsync(actorUserId, [RoleCodes.Manager], token))
            return SchedulingResult<bool>.Fail(SchedulingErrorCodes.ZoneScopeForbidden, "Only an Admin or a responsible Manager can change technician zones.");
        var current = (await unitOfWork.FindActiveMembershipsForUpdateAsync(technicianUserId, token)).Select(m => m.ZoneId).ToHashSet();
        var changed = wanted.Where(id => !current.Contains(id)).Concat(current.Where(id => !wanted.Contains(id))).Distinct().ToList();
        if (changed.Count == 0) return null;
        var outside = (await unitOfWork.GetZonesAsync(changed, token)).Where(z => z.ResponsibleManagerUserId != actorUserId).Select(z => z.Id).ToList();
        return outside.Count == 0
            ? null
            : SchedulingResult<bool>.Fail(SchedulingErrorCodes.ZoneScopeForbidden,
                "You can add or remove technicians only in the operational zones you are the responsible manager of.", new { zoneIds = outside });
    }

    public async Task<SchedulingResult<WorkingScheduleView>> GetWorkingScheduleAsync(Guid technicianUserId, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(technicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return SchedulingResult<WorkingScheduleView>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");
        return SchedulingResult<WorkingScheduleView>.Ok(await ScheduleViewAsync(technicianUserId, technician.Profile?.HasCustomWorkingSchedule == true, cancellationToken));
    }

    public async Task<SchedulingResult<WorkingScheduleView>> SetWorkingScheduleAsync(SetWorkingScheduleCommand command, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return SchedulingResult<WorkingScheduleView>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");
        if (technician.Profile is null)
            return SchedulingResult<WorkingScheduleView>.Fail(SchedulingErrorCodes.TechnicianProfileRequired, "Configure the technician profile (PUT …/profile) first.");

        var week = command.UseDefault ? null : command.Periods.Select(p => new WeeklyPeriod(p.Day, p.Start, p.End)).ToList();
        var probe = new TechnicianDayState(command.TechnicianUserId, technician.FullName, [], [], CustomWeeklySchedule: week);
        bool Outside(UpcomingAppointment a)
        {
            var intervals = calendar.Resolve(probe, calendar.LocalDateOf(a.StartsAt)).WorkingIntervals!;
            return !intervals.Any(w => w.StartsAtUtc <= a.StartsAt && a.EndsAt <= w.EndsAtUtc);
        }

        // A (multi-day) installation assignment must still fall on working days with start/end inside working hours.
        bool InstallationOutside(UpcomingAppointment a)
        {
            var days = new List<TechnicianDayState>();
            for (var d = calendar.LocalDateOf(a.StartsAt); d <= calendar.LocalDateOf(a.EndsAt.AddTicks(-1)); d = d.AddDays(1)) days.Add(calendar.Resolve(probe, d));
            return EligibilityRules.CheckSpan(days, new EligibilityRequest(a.StartsAt, a.EndsAt, null, CountsTowardDailyLimit: false), TimeSpan.Zero) is not null;
        }

        var outcome = await WithHoldsAsync<bool>(command.TechnicianUserId, async (upcoming, now, token) =>
        {
            var outside = upcoming.Where(Outside).ToList();
            var confirmed = outside.Where(a => AppointmentStatuses.IsConfirmed(a.Status)).Select(a => Conflict(a, EligibilityReasons.OutsideWorkingHours))
                .Concat((await unitOfWork.ListUpcomingInstallationAssignmentsAsync(command.TechnicianUserId, now, token)).Where(InstallationOutside)
                    .Select(a => Conflict(a, EligibilityReasons.OutsideWorkingHours)))
                .ToList();
            return confirmed.Count > 0
                ? (null, Conflicts<bool>(confirmed, "Confirmed appointments or installation assignments fall outside the new working schedule. Reschedule or reassign them first."))
                : (outside.Where(a => AppointmentStatuses.IsOpen(a.Status)), null);
        }, async (now, token) =>
        {
            var profile = (await unitOfWork.FindProfileForUpdateAsync(command.TechnicianUserId, token))!;
            profile.HasCustomWorkingSchedule = !command.UseDefault;
            profile.UpdatedAt = now;
            await unitOfWork.ReplaceWorkingPeriodsAsync(command.TechnicianUserId, command.UseDefault
                ? []
                : command.Periods.Select(p => new TechnicianWorkingPeriod
                {
                    Id = Guid.NewGuid(), TechnicianUserId = command.TechnicianUserId, DayOfWeek = (int)p.Day, StartTime = p.Start, EndTime = p.End,
                }).ToList(), token);
            return true;
        }, command.ActorUserId, "TECHNICIAN_WORKING_SCHEDULE_CHANGED", cancellationToken);

        if (!outcome.Succeeded) return SchedulingResult<WorkingScheduleView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        return SchedulingResult<WorkingScheduleView>.Ok(await ScheduleViewAsync(command.TechnicianUserId, !command.UseDefault, cancellationToken));
    }

    public async Task<SchedulingResult<UnavailablePeriodView>> AddUnavailablePeriodAsync(AddUnavailablePeriodCommand command, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return SchedulingResult<UnavailablePeriodView>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");

        var period = new TechnicianUnavailablePeriod
        {
            Id = Guid.NewGuid(),
            TechnicianUserId = command.TechnicianUserId,
            StartsAt = command.StartsAt.ToUniversalTime(),
            EndsAt = command.EndsAt.ToUniversalTime(),
            Reason = string.IsNullOrWhiteSpace(command.Reason) ? null : command.Reason.Trim(),
            CreatedByUserId = command.ActorUserId,
            CreatedAt = time.GetUtcNow(),
        };

        var outcome = await WithHoldsAsync<bool>(command.TechnicianUserId, async (upcoming, now, token) =>
        {
            var overlapping = upcoming.Where(a => a.StartsAt < period.EndsAt && period.StartsAt < a.EndsAt).ToList();
            var confirmed = overlapping.Where(a => AppointmentStatuses.IsConfirmed(a.Status)).Select(a => Conflict(a, EligibilityReasons.OnLeave))
                .Concat((await unitOfWork.ListUpcomingInstallationAssignmentsAsync(command.TechnicianUserId, now, token))
                    .Where(a => a.StartsAt < period.EndsAt && period.StartsAt < a.EndsAt).Select(a => Conflict(a, EligibilityReasons.OnLeave)))
                .ToList();
            return confirmed.Count > 0
                ? (null, Conflicts<bool>(confirmed,
                    "The technician has confirmed appointments or installation work in this period. Reassign or reschedule them first; nothing is cancelled automatically."))
                : (overlapping.Where(a => AppointmentStatuses.IsOpen(a.Status)), null);
        }, (_, _) =>
        {
            unitOfWork.AddUnavailablePeriod(period);
            return Task.FromResult(true);
        }, command.ActorUserId, "TECHNICIAN_ON_LEAVE", cancellationToken);

        if (!outcome.Succeeded) return SchedulingResult<UnavailablePeriodView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        return SchedulingResult<UnavailablePeriodView>.Ok(new UnavailablePeriodView(period.Id, period.TechnicianUserId,
            TimeZoneInfo.ConvertTime(period.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(period.EndsAt, calendar.TimeZone), period.Reason));
    }

    public async Task<IReadOnlyList<UnavailablePeriodView>> ListUnavailablePeriodsAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await unitOfWork.ListUnavailablePeriodsAsync(technicianUserId, time.GetUtcNow(), cancellationToken))
            .Select(p => new UnavailablePeriodView(p.Id, p.TechnicianUserId,
                TimeZoneInfo.ConvertTime(p.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(p.EndsAt, calendar.TimeZone), p.Reason))
            .ToList();

    /// <summary>Removing leave only adds capacity; new slots appear immediately.</summary>
    public Task<bool> RemoveUnavailablePeriodAsync(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken)
        => unitOfWork.RemoveUnavailablePeriodAsync(technicianUserId, periodId, cancellationToken);

    /// <summary>
    /// Shared transaction shape: lock the technician and every possible new holder of their pending holds, re-read
    /// the upcoming commitments, let <paramref name="check"/> refuse (confirmed conflicts) or pick the holds to move,
    /// apply the change, re-home those holds, save, commit. When the technician's open holds under the lock differ from
    /// the set read before (a booking or hold move committed in between), the transaction rolls back and is retried with
    /// the new holds' possible holders locked too (bounded, like acceptance), so no hold is re-homed without its candidates locked.
    /// </summary>
    private async Task<SchedulingResult<T>> WithHoldsAsync<T>(
        Guid technicianUserId,
        Func<IReadOnlyList<UpcomingAppointment>, DateTimeOffset, CancellationToken, Task<(IEnumerable<UpcomingAppointment>? Holds, SchedulingResult<T>? Error)>> check,
        Func<DateTimeOffset, CancellationToken, Task<T>> change,
        Guid? actorUserId,
        string reason,
        CancellationToken cancellationToken,
        List<RehomeResult>? rehomed = null)
    {
        var extraLocks = new HashSet<Guid>();
        for (var attempt = 0; attempt < Math.Max(1, options.MaxReservationAttempts) + 1; attempt++)
        {
            var before = (await unitOfWork.ListUpcomingAppointmentsAsync(technicianUserId, time.GetUtcNow(), cancellationToken))
                .Where(a => AppointmentStatuses.IsOpen(a.Status)).Select(a => a.AppointmentId).ToHashSet();
            var candidates = await assignments.RehomeCandidatesAsync(before, cancellationToken);
            var locked = candidates.Concat(extraLocks).Append(technicianUserId).ToHashSet();
            var moved = new List<RehomeResult>();
            (bool Retry, SchedulingResult<T>? Result) outcome;
            try
            {
                outcome = await unitOfWork.ExecuteInTransactionAsync<(bool, SchedulingResult<T>?)>(async token =>
                {
                    moved.Clear();   // the execution strategy may run this body again
                    await unitOfWork.LockTechniciansAsync(locked, token);
                    var now = time.GetUtcNow();
                    var upcoming = await unitOfWork.ListUpcomingAppointmentsAsync(technicianUserId, now, token);
                    var holdsNow = upcoming.Where(a => AppointmentStatuses.IsOpen(a.Status)).Select(a => a.AppointmentId).ToHashSet();
                    if (!holdsNow.SetEquals(before))
                    {
                        extraLocks.UnionWith(await assignments.RehomeCandidatesAsync(holdsNow, token));
                        return ((true, null), false);
                    }
                    var (holds, error) = await check(upcoming, now, token);
                    if (error is not null) return ((false, error), false);

                    var value = await change(now, token);
                    await unitOfWork.SaveChangesAsync(token);
                    moved.AddRange(await assignments.RehomeHoldsAsync(technicianUserId, holds!.Select(h => h.AppointmentId).ToList(), locked,
                        actorUserId ?? Guid.Empty, reason, token));
                    await unitOfWork.SaveChangesAsync(token);
                    return ((false, SchedulingResult<T>.Ok(value)), true);
                }, cancellationToken);
            }
            catch (SchedulingConflictException ex) when (ex.Kind is SchedulingConflictException.StaleAppointment or SchedulingConflictException.TechnicianOverlap)
            {
                outcome = (true, null);
            }
            catch (SchedulingConflictException)
            {
                return SchedulingResult<T>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The technician's schedule changed meanwhile. Reload and try again.");
            }

            if (outcome.Retry) continue;
            if (outcome.Result!.Succeeded) rehomed?.AddRange(moved);
            return outcome.Result;
        }
        return SchedulingResult<T>.Fail(SchedulingErrorCodes.ConcurrencyConflict, "The technician's schedule changed meanwhile. Reload and try again.");
    }

    private async Task<WorkingScheduleView> ScheduleViewAsync(Guid technicianUserId, bool custom, CancellationToken cancellationToken)
    {
        IEnumerable<WorkingPeriodView> periods = custom
            ? (await unitOfWork.GetWorkingPeriodsAsync(technicianUserId, cancellationToken))
                .Select(p => new WorkingPeriodView(((DayOfWeek)p.DayOfWeek).ToString().ToUpperInvariant(), p.StartTime.ToString("HH:mm"), p.EndTime.ToString("HH:mm")))
            : calendar.WorkingDays.OrderBy(d => ((int)d + 6) % 7)
                .SelectMany(d => calendar.Periods.Select(p => new WorkingPeriodView(d.ToString().ToUpperInvariant(), p.Start.ToString("HH:mm"), p.End.ToString("HH:mm"))));
        return new WorkingScheduleView(technicianUserId, !custom, calendar.TimeZone.Id, periods.ToList());
    }

    /// <summary>
    /// A lower daily limit: on every local day with upcoming work whose survey commitments exceed the new cap, the latest pending
    /// holds above the cap are re-homed (or escalate); a day whose confirmed (ACCEPTED/ASSIGNED/COMPLETED) appointments alone exceed
    /// the cap refuses the change with TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS (DAILY_LIMIT_REACHED).
    /// </summary>
    private async Task<(IEnumerable<UpcomingAppointment>? Holds, SchedulingResult<T>? Error)> DailyLimitAsync<T>(Guid technicianUserId, int? maxPerDay,
        IReadOnlyList<UpcomingAppointment> upcoming, DateTimeOffset now, CancellationToken token)
    {
        var cap = maxPerDay ?? options.MaxAppointmentsPerTechnicianPerDay;
        var conflicts = new List<ConflictingAppointmentView>();
        var holds = new List<UpcomingAppointment>();
        foreach (var date in upcoming.Select(a => calendar.LocalDateOf(a.StartsAt)).Distinct().Order())
        {
            var day = (await assignments.Eligibility.LoadDaysAsync(date, new TechnicianScope([technicianUserId], SchedulableOnly: false), token)).SingleOrDefault();
            if (day is null) continue;
            var counted = day.CountedCommitments();
            if (counted <= cap) continue;
            var confirmed = day.Appointments.Count(a => a.CountsTowardDailyLimit && !a.IsPendingHold);
            var ofDay = upcoming.Where(a => calendar.LocalDateOf(a.StartsAt) == date).ToList();
            if (confirmed > cap)
            {
                conflicts.AddRange(ofDay.Where(a => AppointmentStatuses.IsConfirmed(a.Status)).Select(a => Conflict(a, EligibilityReasons.DailyLimitReached)));
                continue;
            }
            holds.AddRange(ofDay.Where(a => AppointmentStatuses.IsOpen(a.Status)).OrderByDescending(a => a.StartsAt).Take(counted - cap));
        }
        return conflicts.Count > 0
            ? (null, Conflicts<T>(conflicts, "The technician has more confirmed appointments on a day than the new daily limit allows. Reassign some of them first."))
            : (holds, null);
    }

    private ConflictingAppointmentView Conflict(UpcomingAppointment a, string problem) => new(a.AppointmentId, a.SurveyRequestId,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone), a.Status, problem);

    private static SchedulingResult<T> Conflicts<T>(IEnumerable<ConflictingAppointmentView> conflicts, string message)
        => SchedulingResult<T>.Fail(SchedulingErrorCodes.TechnicianHasConflictingAppointments, message, new { conflictingAppointments = conflicts.ToList() });

    private static TechnicianProfileView View(Guid id, string name, string? phone, bool active, TechnicianProfile? p, IReadOnlyList<ZoneRef> zones) => new(
        id, name, phone, active, p?.IsSchedulingActive ?? false, p?.MaxAppointmentsPerDay,
        active && p is { IsSchedulingActive: true } && zones.Count > 0,
        p?.HasCustomWorkingSchedule ?? false,
        zones.Select(z => ZoneView.From(z)!).ToList());
}
