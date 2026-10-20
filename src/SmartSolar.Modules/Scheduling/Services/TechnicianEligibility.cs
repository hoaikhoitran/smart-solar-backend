using SmartSolar.Modules.Scheduling.Contracts.Persistence;
using SmartSolar.Modules.Scheduling.Domain;
using SmartSolar.Modules.Scheduling.Options;
using SmartSolar.Modules.SystemSettings;

namespace SmartSolar.Modules.Scheduling.Services;

/// <summary>A technician who may take the interval, with the number of survey commitments already on that day.</summary>
public sealed record EligibleTechnician(TechnicianDayState Day, int CommitmentsThatDay);

/// <summary>
/// The single eligibility service shared by survey scheduling (holder choice, acceptance, staff assignment,
/// availability) and installation planning. It loads technician-days through <see cref="TechnicianDayLoader"/>
/// and applies <see cref="EligibilityRules"/> with the configured buffer and daily limit. Busy intervals are
/// every non-cancelled survey appointment the technician holds or is confirmed on, plus current (PENDING/ACCEPTED)
/// installation assignments when Installation Flow is enabled (the unit of work decides that source).
/// <para>The buffer is the admin-editable <c>scheduling.bufferBetweenAppointmentsMinutes</c>: the instance (scoped: one per request
/// or worker tick) reads the <see cref="SchedulingPolicySnapshot"/> once, on its first load, and uses it for every decision of the
/// scope, so one operation never mixes two settings versions.</para>
/// </summary>
public sealed class TechnicianEligibility(ISchedulingUnitOfWork unitOfWork, WorkingCalendar calendar, SchedulingOptions options, ISchedulingPolicyProvider policies)
{
    private SchedulingPolicySnapshot? _policy;

    /// <summary>The scope's settings snapshot (read once, then reused).</summary>
    public async Task<SchedulingPolicySnapshot> PolicyAsync(CancellationToken cancellationToken)
        => _policy ??= await policies.GetAsync(cancellationToken);

    /// <summary>The snapshot read by the first load of this scope; every rule evaluation follows a load.</summary>
    public SchedulingPolicySnapshot Policy
        => _policy ?? throw new InvalidOperationException("The scheduling policy is read by the first technician load; call PolicyAsync or a Load method first.");

    public TimeSpan Buffer => TimeSpan.FromMinutes(Policy.BufferBetweenAppointmentsMinutes);

    public int DefaultMaxPerDay => options.MaxAppointmentsPerTechnicianPerDay;

    public WorkingCalendar Calendar => calendar;

    public async Task<IReadOnlyList<TechnicianDayState>> LoadDaysAsync(DateOnly localDate, TechnicianScope scope, CancellationToken cancellationToken)
    {
        await PolicyAsync(cancellationToken);
        return await new TechnicianDayLoader(unitOfWork, calendar).LoadAsync(localDate, scope, cancellationToken);
    }

    /// <summary>Per-date states for a date range from one window query (see <see cref="TechnicianDayLoader.LoadRangeAsync"/>).</summary>
    public async Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<TechnicianDayState>>> LoadDaysRangeAsync(DateOnly firstDate, DateOnly lastDate, TechnicianScope scope,
        CancellationToken cancellationToken)
    {
        await PolicyAsync(cancellationToken);
        return await new TechnicianDayLoader(unitOfWork, calendar).LoadRangeAsync(firstDate, lastDate, scope, cancellationToken);
    }

    public string? Evaluate(TechnicianDayState day, EligibilityRequest request)
        => EligibilityRules.Check(day, request, DefaultMaxPerDay, Buffer);

    /// <summary>Eligible technicians ordered for holder choice: fewest survey commitments that day, then id.</summary>
    public IReadOnlyList<EligibleTechnician> Rank(IEnumerable<TechnicianDayState> days, EligibilityRequest request)
        => days.Where(d => Evaluate(d, request) is null)
            .Select(d => new EligibleTechnician(d, d.CountedCommitments(request.IgnoreCommitmentIds)))
            .OrderBy(e => e.CommitmentsThatDay).ThenBy(e => e.Day.TechnicianUserId)
            .ToList();

    /// <summary>
    /// Multi-day installation interval for each technician (<see cref="EligibilityRules.CheckSpan"/>): loads every local
    /// date the interval touches. Returns the display name and the reason (null = eligible); unknown ids map to TECHNICIAN_INACTIVE.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, (string Name, string? Reason)>> CheckSpanAsync(IReadOnlyCollection<Guid> technicianUserIds,
        EligibilityRequest request, CancellationToken cancellationToken)
    {
        await PolicyAsync(cancellationToken);
        var ids = technicianUserIds.Distinct().ToList();
        var perTechnician = ids.ToDictionary(id => id, _ => new List<TechnicianDayState>());
        if (ids.Count > 0)
        {
            var last = calendar.LocalDateOf(request.EndsAtUtc.AddTicks(-1));
            for (var date = calendar.LocalDateOf(request.StartsAtUtc); date <= last; date = date.AddDays(1))
                foreach (var day in await LoadDaysAsync(date, new TechnicianScope(ids, SchedulableOnly: false), cancellationToken))
                    perTechnician[day.TechnicianUserId].Add(day);
        }
        return perTechnician.ToDictionary(p => p.Key, p => p.Value.Count == 0
            ? ("", EligibilityReasons.TechnicianInactive)
            : (p.Value[0].DisplayName, EligibilityRules.CheckSpan(p.Value, request, Buffer)));
    }

    /// <summary>One technician's state for the date (whatever their account state) and the reason they cannot take the interval, if any.</summary>
    public async Task<(TechnicianDayState? Day, string? Reason)> CheckAsync(Guid technicianUserId, EligibilityRequest request, CancellationToken cancellationToken)
    {
        var day = (await LoadDaysAsync(calendar.LocalDateOf(request.StartsAtUtc), new TechnicianScope([technicianUserId], SchedulableOnly: false), cancellationToken))
            .SingleOrDefault();
        return day is null ? (null, EligibilityReasons.TechnicianInactive) : (day, Evaluate(day, request));
    }
}
