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
        return days.Select(d => calendar.Resolve(d, localDate)).ToList();
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
        var window = await unitOfWork.GetTechnicianDayStatesAsync(start, end, scope, cancellationToken);
        var result = new Dictionary<DateOnly, IReadOnlyList<TechnicianDayState>>();
        for (var date = firstDate; date <= lastDate; date = date.AddDays(1))
        {
            var (dayStart, dayEnd) = calendar.DayBoundsUtc(date);
            result[date] = window.Select(d => calendar.Resolve(d with
            {
                Appointments = d.Appointments.Where(a => a.StartsAtUtc < dayEnd && a.EndsAtUtc > dayStart).ToList(),
                Blocked = d.Blocked.Where(b => b.StartsAtUtc < dayEnd && b.EndsAtUtc > dayStart).ToList(),
            }, date)).ToList();
        }
        return result;
    }
}
