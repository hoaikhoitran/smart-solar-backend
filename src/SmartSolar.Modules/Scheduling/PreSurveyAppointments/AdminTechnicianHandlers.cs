using FluentValidation;
using SmartSolar.Modules.Scheduling.Constants;
using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Entities;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.Scheduling.Services;

namespace SmartSolar.Modules.Scheduling.PreSurveyAppointments;

public sealed record UpsertTechnicianProfileCommand(
    Guid TechnicianUserId, decimal? BaseLatitude, decimal? BaseLongitude, string? BaseLabel, bool IsSchedulingActive, int? MaxAppointmentsPerDay,
    TimeOnly? EarliestBaseDeparture = null);

public sealed record AddUnavailablePeriodCommand(Guid ActorUserId, Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record WorkingPeriodInput(DayOfWeek Day, TimeOnly Start, TimeOnly End);

/// <summary><see cref="UseDefault"/> = true removes the custom week; otherwise <see cref="Periods"/> is the full week (missing days are days off).</summary>
public sealed record SetWorkingScheduleCommand(Guid TechnicianUserId, bool UseDefault, IReadOnlyList<WorkingPeriodInput> Periods);

public sealed record TechnicianProfileView(Guid TechnicianUserId, string FullName, string? Phone, bool IsActiveUser, decimal? BaseLatitude, decimal? BaseLongitude,
    string? BaseLabel, bool IsSchedulingActive, int? MaxAppointmentsPerDay, bool ReadyForScheduling,
    bool HasCustomWorkingSchedule, string EarliestBaseDeparture, bool EarliestBaseDepartureIsDefault, string VehicleType);

public sealed record UnavailablePeriodView(Guid Id, Guid TechnicianUserId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Reason);

public sealed record WorkingPeriodView(string Day, string Start, string End);

public sealed record WorkingScheduleView(Guid TechnicianUserId, bool UsesDefault, string TimeZone, IReadOnlyList<WorkingPeriodView> Periods);

/// <summary>An existing appointment that a configuration change would make impossible.</summary>
public sealed record ConflictingAppointmentView(Guid AppointmentId, Guid? SurveyRequestId, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string Status, string Problem);

public sealed class UpsertTechnicianProfileCommandValidator : AbstractValidator<UpsertTechnicianProfileCommand>
{
    public UpsertTechnicianProfileCommandValidator()
    {
        RuleFor(x => x.TechnicianUserId).NotEmpty();
        RuleFor(x => x.BaseLatitude).InclusiveBetween(-90, 90).When(x => x.BaseLatitude.HasValue);
        RuleFor(x => x.BaseLongitude).InclusiveBetween(-180, 180).When(x => x.BaseLongitude.HasValue);
        RuleFor(x => x).Must(x => x.BaseLatitude.HasValue == x.BaseLongitude.HasValue)
            .WithMessage("Base latitude and longitude must be provided together.").OverridePropertyName("BaseLatitude");
        RuleFor(x => x).Must(x => !x.IsSchedulingActive || GeoPoint.From(x.BaseLatitude, x.BaseLongitude) is not null)
            .WithMessage("An active scheduling profile needs a real base coordinate (not 0,0).").OverridePropertyName("BaseLatitude");
        RuleFor(x => x.BaseLabel).MaximumLength(200);
        RuleFor(x => x.MaxAppointmentsPerDay).InclusiveBetween(1, 20).When(x => x.MaxAppointmentsPerDay.HasValue);
        RuleFor(x => x.EarliestBaseDeparture).Must(t => t!.Value >= new TimeOnly(4, 0) && t.Value <= new TimeOnly(12, 0))
            .When(x => x.EarliestBaseDeparture.HasValue).WithMessage("earliestBaseDeparture must be between 04:00 and 12:00 local time.");
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

/// <summary>
/// Technician scheduling setup for ADMIN/MANAGER: profile (base location, active flag, daily cap,
/// earliest base departure), custom weekly working schedule, and leave. Changes that would make an
/// existing appointment impossible are refused with TECHNICIAN_HAS_CONFLICTING_APPOINTMENTS and the
/// affected appointments; nothing is cancelled automatically. Changes that alter routes queue a
/// durable recalculation of the affected days.
/// </summary>
public sealed class AdminTechnicianHandlers(
    ISchedulingUnitOfWork unitOfWork,
    ScheduleRecalculationService recalculation,
    WorkingCalendar calendar,
    TimeProvider time)
{
    public async Task<IReadOnlyList<TechnicianProfileView>> ListAsync(CancellationToken cancellationToken)
        => (await unitOfWork.ListTechniciansAsync(cancellationToken)).Select(t => View(t.UserId, t.FullName, t.Phone, t.IsActiveUser, t.Profile)).ToList();

    public async Task<SchedulingResult<TechnicianProfileView>> UpsertProfileAsync(UpsertTechnicianProfileCommand command, CancellationToken cancellationToken)
    {
        var technician = await unitOfWork.GetTechnicianAsync(command.TechnicianUserId, cancellationToken);
        if (technician is null || !technician.HasTechnicianRole)
            return SchedulingResult<TechnicianProfileView>.Fail(SchedulingErrorCodes.TechnicianNotFound, "Technician was not found.");

        var recalcDates = new List<DateOnly>();
        var outcome = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<TechnicianProfile>>(async token =>
        {
            await unitOfWork.LockTechniciansAsync([command.TechnicianUserId], token);
            var now = time.GetUtcNow();
            var existing = await unitOfWork.FindProfileForUpdateAsync(command.TechnicianUserId, token);
            var upcoming = await unitOfWork.ListUpcomingAppointmentsAsync(command.TechnicianUserId, now, token);

            if (!command.IsSchedulingActive && upcoming.Count > 0)
                return (Conflicts<TechnicianProfile>(upcoming.Select(a => Conflict(a, "SCHEDULING_DISABLED")),
                    "The technician still has upcoming appointments. Reassign them before disabling scheduling."), false);
            // Installation crews (Installation Flow) are booked under the same technician lock, so this check cannot race a confirmation.
            var installationShifts = command.IsSchedulingActive ? [] : await unitOfWork.ListUpcomingInstallationAssignmentsAsync(command.TechnicianUserId, now, token);
            if (installationShifts.Count > 0)
                return (Conflicts<TechnicianProfile>(installationShifts.Select(a => Conflict(a, "INSTALLATION_SHIFT")),
                    "The technician is booked on upcoming installation shifts. Change the installation team before disabling scheduling."), false);

            var newEarliest = command.EarliestBaseDeparture ?? calendar.DefaultEarliestBaseDeparture;
            var oldEarliest = existing?.EarliestBaseDeparture ?? calendar.DefaultEarliestBaseDeparture;
            if (newEarliest > oldEarliest)
            {
                var late = await FirstLegsDepartingBeforeAsync(command.TechnicianUserId, upcoming, newEarliest, token);
                if (late.Count > 0)
                    return (Conflicts<TechnicianProfile>(late, "Existing first appointments require leaving the base before the new earliest departure."), false);
            }

            if (existing is null)
            {
                existing = new TechnicianProfile { UserId = command.TechnicianUserId, CreatedAt = now };
                unitOfWork.AddProfile(existing);
            }

            var baseChanged = existing.BaseLatitude != command.BaseLatitude || existing.BaseLongitude != command.BaseLongitude;
            existing.BaseLatitude = command.BaseLatitude;
            existing.BaseLongitude = command.BaseLongitude;
            existing.BaseLabel = string.IsNullOrWhiteSpace(command.BaseLabel) ? null : command.BaseLabel.Trim();
            existing.IsSchedulingActive = command.IsSchedulingActive;
            existing.MaxAppointmentsPerDay = command.MaxAppointmentsPerDay;
            existing.EarliestBaseDeparture = command.EarliestBaseDeparture;
            existing.UpdatedAt = now;

            if (baseChanged)
            {
                // First legs of every upcoming day now start elsewhere: re-estimate them durably.
                foreach (var date in upcoming.Select(a => calendar.LocalDateOf(a.StartsAt)).Distinct())
                {
                    await unitOfWork.RequestRecalculationAsync(command.TechnicianUserId, date, RecalculationReasons.BaseLocationChanged, now, token);
                    recalcDates.Add(date);
                }
            }

            await unitOfWork.SaveChangesAsync(token);
            return (SchedulingResult<TechnicianProfile>.Ok(existing), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return SchedulingResult<TechnicianProfileView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        foreach (var date in recalcDates) await recalculation.TryRunAsync(command.TechnicianUserId, date, cancellationToken);
        return SchedulingResult<TechnicianProfileView>.Ok(View(technician.UserId, technician.FullName, null, technician.IsActiveUser, outcome.Value));
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
        var dates = new List<DateOnly>();
        var outcome = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<bool>>(async token =>
        {
            await unitOfWork.LockTechniciansAsync([command.TechnicianUserId], token);
            var now = time.GetUtcNow();
            var upcoming = await unitOfWork.ListUpcomingAppointmentsAsync(command.TechnicianUserId, now, token);
            var probe = new TechnicianDayState(command.TechnicianUserId, technician.FullName, null, [], [], CustomWeeklySchedule: week);

            var outside = upcoming.Where(a =>
            {
                var date = calendar.LocalDateOf(a.StartsAt);
                var intervals = calendar.Resolve(probe, date).WorkingIntervals!;
                return !intervals.Any(w => w.StartsAtUtc <= a.StartsAt && a.EndsAt <= w.EndsAtUtc);
            }).Select(a => Conflict(a, FeasibilityReasons.OutsideWorkingHours)).ToList();
            if (outside.Count > 0)
                return (Conflicts<bool>(outside, "Existing appointments fall outside the new working schedule. Reschedule or reassign them first."), false);

            var profile = (await unitOfWork.FindProfileForUpdateAsync(command.TechnicianUserId, token))!;
            profile.HasCustomWorkingSchedule = !command.UseDefault;
            profile.UpdatedAt = now;
            await unitOfWork.ReplaceWorkingPeriodsAsync(command.TechnicianUserId, command.UseDefault
                ? []
                : command.Periods.Select(p => new TechnicianWorkingPeriod
                {
                    Id = Guid.NewGuid(), TechnicianUserId = command.TechnicianUserId, DayOfWeek = (int)p.Day, StartTime = p.Start, EndTime = p.End,
                }).ToList(), token);

            foreach (var date in upcoming.Select(a => calendar.LocalDateOf(a.StartsAt)).Distinct())
            {
                await unitOfWork.RequestRecalculationAsync(command.TechnicianUserId, date, RecalculationReasons.WorkingScheduleChanged, now, token);
                dates.Add(date);
            }
            await unitOfWork.SaveChangesAsync(token);
            return (SchedulingResult<bool>.Ok(true), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return SchedulingResult<WorkingScheduleView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        foreach (var date in dates) await recalculation.TryRunAsync(command.TechnicianUserId, date, cancellationToken);
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

        var outcome = await unitOfWork.ExecuteInTransactionAsync<SchedulingResult<bool>>(async token =>
        {
            // Serialise with bookings for this technician so a booking cannot slip in unseen.
            await unitOfWork.LockTechniciansAsync([command.TechnicianUserId], token);
            var overlapping = (await unitOfWork.ListUpcomingAppointmentsAsync(command.TechnicianUserId, period.StartsAt, token))
                .Where(a => a.StartsAt < period.EndsAt && period.StartsAt < a.EndsAt)
                .Select(a => Conflict(a, FeasibilityReasons.Unavailable))
                .ToList();
            if (overlapping.Count > 0)
                return (Conflicts<bool>(overlapping,
                    "The technician has appointments in this period. Reassign or reschedule them first; appointments are never cancelled automatically."), false);

            unitOfWork.AddUnavailablePeriod(period);
            await unitOfWork.SaveChangesAsync(token);
            return (SchedulingResult<bool>.Ok(true), true);
        }, cancellationToken);

        if (!outcome.Succeeded) return SchedulingResult<UnavailablePeriodView>.Fail(outcome.ErrorCode!, outcome.Message!, outcome.Details);
        return SchedulingResult<UnavailablePeriodView>.Ok(new UnavailablePeriodView(period.Id, period.TechnicianUserId,
            TimeZoneInfo.ConvertTime(period.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(period.EndsAt, calendar.TimeZone), period.Reason));
    }

    public async Task<IReadOnlyList<UnavailablePeriodView>> ListUnavailablePeriodsAsync(Guid technicianUserId, CancellationToken cancellationToken)
        => (await unitOfWork.ListUnavailablePeriodsAsync(technicianUserId, time.GetUtcNow(), cancellationToken))
            .Select(p => new UnavailablePeriodView(p.Id, p.TechnicianUserId,
                TimeZoneInfo.ConvertTime(p.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(p.EndsAt, calendar.TimeZone), p.Reason))
            .ToList();

    /// <summary>Removing leave only adds capacity; existing routes are unaffected, new slots appear immediately.</summary>
    public Task<bool> RemoveUnavailablePeriodAsync(Guid technicianUserId, Guid periodId, CancellationToken cancellationToken)
        => unitOfWork.RemoveUnavailablePeriodAsync(technicianUserId, periodId, cancellationToken);

    /// <summary>First appointments of a day whose current base→first-stop estimate departs before <paramref name="earliest"/>.</summary>
    private async Task<List<ConflictingAppointmentView>> FirstLegsDepartingBeforeAsync(
        Guid technicianUserId, IReadOnlyList<UpcomingAppointment> upcoming, TimeOnly earliest, CancellationToken token)
    {
        var conflicts = new List<ConflictingAppointmentView>();
        foreach (var first in upcoming.GroupBy(a => calendar.LocalDateOf(a.StartsAt)).Select(g => (Date: g.Key, First: g.OrderBy(a => a.StartsAt).First())))
        {
            var estimate = await unitOfWork.GetLatestInboundEstimateAsync(first.First.AppointmentId, token);
            if (estimate is { OriginKind: TravelOriginKinds.Base, Status: Travel.RouteCalculationStatuses.Ok }
                && estimate.DepartureAt < calendar.ToUtc(first.Date, earliest))
            {
                conflicts.Add(Conflict(first.First, FeasibilityReasons.BeforeEarliestDeparture));
            }
        }
        return conflicts;
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

    private ConflictingAppointmentView Conflict(UpcomingAppointment a, string problem) => new(a.AppointmentId, a.SurveyRequestId,
        TimeZoneInfo.ConvertTime(a.StartsAt, calendar.TimeZone), TimeZoneInfo.ConvertTime(a.EndsAt, calendar.TimeZone), a.Status, problem);

    private static SchedulingResult<T> Conflicts<T>(IEnumerable<ConflictingAppointmentView> conflicts, string message)
        => SchedulingResult<T>.Fail(SchedulingErrorCodes.TechnicianHasConflictingAppointments, message, new { conflictingAppointments = conflicts.ToList() });

    private TechnicianProfileView View(Guid id, string name, string? phone, bool active, TechnicianProfile? p) => new(
        id, name, phone, active, p?.BaseLatitude, p?.BaseLongitude, p?.BaseLabel, p?.IsSchedulingActive ?? false, p?.MaxAppointmentsPerDay,
        active && p is { IsSchedulingActive: true } && GeoPoint.From(p.BaseLatitude, p.BaseLongitude) is not null,
        p?.HasCustomWorkingSchedule ?? false,
        (p?.EarliestBaseDeparture ?? calendar.DefaultEarliestBaseDeparture).ToString("HH:mm"),
        p?.EarliestBaseDeparture is null,
        "MOTORCYCLE");
}
