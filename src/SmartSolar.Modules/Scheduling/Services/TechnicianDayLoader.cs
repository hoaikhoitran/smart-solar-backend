using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// Loads technician-days for one local date and resolves each technician's effective working
/// intervals. Every scheduling decision goes through here, so the per-technician schedule is
/// honoured the same way everywhere.
/// </summary>
public sealed class TechnicianDayLoader(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar)
{
    public async Task<IReadOnlyList<TechnicianDayState>> LoadAsync(DateOnly localDate, TechnicianScope scope, CancellationToken cancellationToken)
    {
        var (start, end) = calendar.DayBoundsUtc(localDate);
        var days = await unitOfWork.GetTechnicianDayStatesAsync(start, end, scope, cancellationToken);
        return days.Select(d => calendar.Resolve(ForDay(WithOvertimeHold(d), localDate), localDate)).ToList();
    }

    /// <summary>
    /// The same per-date states as <see cref="LoadAsync"/> for every date in [<paramref name="firstDate"/>, <paramref name="lastDate"/>],
    /// from ONE window query: commitments and leave are split by each local day's bounds (as the per-date query selects them).
    /// Used by list views so their query count does not grow with the number of rows or dates.
    /// </summary>
    public async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<TechnicianDayState>>> LoadRangeAsync(DateOnly firstDate, DateOnly lastDate, TechnicianScope scope,
        CancellationToken cancellationToken)
    {
        var (start, _) = calendar.DayBoundsUtc(firstDate);
        var (_, end) = calendar.DayBoundsUtc(lastDate);
        var window = (await unitOfWork.GetTechnicianDayStatesAsync(start, end, scope, cancellationToken)).Select(WithOvertimeHold).ToList();
        var result = new Dictionary<DateOnly, IReadOnlyList<TechnicianDayState>>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var (dayStart, dayEnd) = calendar.DayBoundsUtc(date);
            result[date] = window.Select(d => calendar.Resolve(ForDay(d with
            {
                // A stop belongs to a day when its effective interval (or point: an early completion) or its reserved interval touches it.
                Appointments = d.Appointments.Where(a => Touches(a.StartsAtUtc, a.EndsAtUtc, dayStart, dayEnd) || Touches(a.ReservedStart, a.ReservedEnd, dayStart, dayEnd)).ToList(),
                Blocked = d.Blocked.Where(b => b.StartsAtUtc < dayEnd && b.EndsAtUtc > dayStart).ToList(),
            }, date), date)).ToList();
        }
        return result;
    }

    /// <summary>
    /// Work still in execution past its reserved end occupies the technician until the end of the local day in which it is observed running
    /// (<see cref="EffectiveBusy.OvertimeHoldUntil"/>): no new commitment is accepted for the rest of that day while the overrun lasts.
    /// </summary>
    private TechnicianDayState WithOvertimeHold(TechnicianDayState day)
        => day.Appointments.Any(a => a.InExecution)
            ? day with
            {
                Appointments = day.Appointments.Select(a => a.InExecution ? a with { EndsAtUtc = EffectiveBusy.OvertimeHoldUntil(a.EndsAtUtc, calendar) } : a).ToList(),
            }
            : day;

    private static bool Touches(DateTimeOffset start, DateTimeOffset end, DateTimeOffset dayStart, DateTimeOffset dayEnd)
        => start < dayEnd && (end > dayStart || (start == end && start >= dayStart));

    /// <summary>
    /// The approved daily survey limit counts surveys on their SCHEDULED day: a survey stop counts toward <paramref name="date"/> only when its reserved
    /// start is on that local date (an early completion on another day keeps its buffer there but is not charged to that day).
    /// </summary>
    private TechnicianDayState ForDay(TechnicianDayState day, DateOnly date)
        => day.Appointments.Any(a => a.CountsTowardDailyLimit && calendar.LocalDateOf(a.ReservedStart) != date)
            ? day with
            {
                Appointments = day.Appointments.Select(a => a.CountsTowardDailyLimit && calendar.LocalDateOf(a.ReservedStart) != date
                    ? a with { CountsTowardDailyLimit = false } : a).ToList(),
            }
            : day;
}
