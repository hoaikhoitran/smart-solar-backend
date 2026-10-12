using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>
/// Loads technician-days for one local date and resolves each technician's effective working
/// intervals and earliest base departure. Every scheduling decision goes through here, so the
/// per-technician schedule is honoured the same way everywhere.
/// </summary>
public sealed class TechnicianDayLoader(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar)
{
    public async Task<IReadOnlyList<TechnicianDayState>> LoadAsync(DateOnly localDate, Guid? onlyTechnicianId, CancellationToken cancellationToken)
    {
        var (start, end) = calendar.DayBoundsUtc(localDate);
        var days = await unitOfWork.GetTechnicianDayStatesAsync(start, end, onlyTechnicianId, cancellationToken);
        return days.Select(d => calendar.Resolve(d, localDate)).ToList();
    }
}
