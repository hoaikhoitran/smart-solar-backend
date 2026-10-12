using SmartSolar.Modules.Scheduling.Options;

namespace SmartSolar.Modules.Scheduling.Domain;

/// <summary>A candidate appointment interval, in UTC, with its local date.</summary>
public sealed record AppointmentWindow(DateOnly LocalDate, DateTimeOffset StartsAtUtc, DateTimeOffset EndsAtUtc, TimeOnly LocalStart, TimeOnly LocalEnd);

/// <summary>
/// Working periods and slot rules in the configured time zone. Every appointment must fit entirely
/// inside one working period of the technician who takes it (so it never crosses a break or the end
/// of the day). Start times are generated every <see cref="SchedulingOptions.SlotIntervalMinutes"/>
/// from each period start. The default schedule applies to technicians without a custom weekly
/// schedule; a custom schedule replaces it completely (a day without periods is a day off).
/// </summary>
public sealed class WorkingCalendar
{
    private readonly IReadOnlyList<(TimeOnly Start, TimeOnly End)> _periods;
    private readonly IReadOnlySet<DayOfWeek> _workingDays;

    public WorkingCalendar(SchedulingOptions options)
    {
        Options = options;
        TimeZone = TimeZoneInfo.FindSystemTimeZoneById(options.TimeZoneId);
        _periods = ParsePeriods(options.WorkingHours);
        _workingDays = ParseDays(options.WorkingDays);
        DefaultEarliestBaseDeparture = ParseTime(options.EarliestBaseDeparture, "Scheduling:EarliestBaseDeparture");
        Duration = TimeSpan.FromMinutes(options.AppointmentDurationMinutes);
        Interval = TimeSpan.FromMinutes(options.SlotIntervalMinutes);
    }

    public SchedulingOptions Options { get; }
    public TimeZoneInfo TimeZone { get; }
    public TimeSpan Duration { get; }
    public TimeSpan Interval { get; }
    public TimeOnly DefaultEarliestBaseDeparture { get; }
    public IReadOnlyList<(TimeOnly Start, TimeOnly End)> Periods => _periods;
    public IReadOnlySet<DayOfWeek> WorkingDays => _workingDays;

    public static IReadOnlyList<(TimeOnly Start, TimeOnly End)> ParsePeriods(string workingHours)
    {
        var periods = new List<(TimeOnly, TimeOnly)>();
        foreach (var part in workingHours.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length != 2
                || !TimeOnly.TryParseExact(bounds[0], "HH:mm", out var start)
                || !TimeOnly.TryParseExact(bounds[1], "HH:mm", out var end)
                || end <= start)
            {
                throw new FormatException($"Invalid working period '{part}'. Expected 'HH:mm-HH:mm'.");
            }
            periods.Add((start, end));
        }

        var ordered = periods.OrderBy(p => p.Item1).ToList();
        EnsureNoOverlap(ordered);
        return ordered.Count > 0 ? ordered : throw new FormatException("At least one working period is required.");
    }

    /// <summary>Throws when sorted periods overlap.</summary>
    public static void EnsureNoOverlap(IReadOnlyList<(TimeOnly Start, TimeOnly End)> ordered)
    {
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
            {
                throw new FormatException("Working periods must not overlap.");
            }
        }
    }

    public static IReadOnlySet<DayOfWeek> ParseDays(string days)
    {
        var result = new HashSet<DayOfWeek>();
        foreach (var part in days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(part.ToUpperInvariant() switch
            {
                "MON" => DayOfWeek.Monday,
                "TUE" => DayOfWeek.Tuesday,
                "WED" => DayOfWeek.Wednesday,
                "THU" => DayOfWeek.Thursday,
                "FRI" => DayOfWeek.Friday,
                "SAT" => DayOfWeek.Saturday,
                "SUN" => DayOfWeek.Sunday,
                _ => throw new FormatException($"Invalid working day '{part}'. Use MON, TUE, WED, THU, FRI, SAT, SUN."),
            });
        }
        return result.Count > 0 ? result : throw new FormatException("At least one working day is required.");
    }

    public static TimeOnly ParseTime(string value, string settingName)
        => TimeOnly.TryParseExact(value?.Trim(), "HH:mm", out var time)
            ? time
            : throw new FormatException($"'{settingName}' must be a local time in 'HH:mm' format.");

    public DateOnly LocalDateOf(DateTimeOffset instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    /// <summary>UTC instant of a local wall-clock time on a local date.</summary>
    public DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        var offset = TimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>[start, end) of the local calendar day in UTC.</summary>
    public (DateTimeOffset StartUtc, DateTimeOffset EndUtc) DayBoundsUtc(DateOnly date)
        => (ToUtc(date, TimeOnly.MinValue), ToUtc(date.AddDays(1), TimeOnly.MinValue));

    /// <summary>The default working periods on a date (empty on a non-working day).</summary>
    public IReadOnlyList<(TimeOnly Start, TimeOnly End)> DefaultPeriodsFor(DateOnly date)
        => _workingDays.Contains(date.DayOfWeek) ? _periods : [];

    /// <summary>The technician's effective local periods on a date: the custom week when configured, otherwise the default.</summary>
    public IReadOnlyList<(TimeOnly Start, TimeOnly End)> PeriodsFor(TechnicianDayState day, DateOnly date)
        => day.CustomWeeklySchedule is { } week
            ? week.Where(p => p.Day == date.DayOfWeek).OrderBy(p => p.Start).Select(p => (p.Start, p.End)).ToList()
            : DefaultPeriodsFor(date);

    /// <summary>Fills the resolved working intervals and the earliest base departure for <paramref name="date"/>.</summary>
    public TechnicianDayState Resolve(TechnicianDayState day, DateOnly date)
        => day with
        {
            WorkingIntervals = PeriodsFor(day, date).Select(p => new WorkingInterval(ToUtc(date, p.Start), ToUtc(date, p.End))).ToList(),
            EarliestBaseDepartureUtc = ToUtc(date, day.EarliestBaseDepartureOverride ?? DefaultEarliestBaseDeparture),
        };

    /// <summary>Every start time of the default schedule on that date (past ones included).</summary>
    public IReadOnlyList<AppointmentWindow> CandidateWindows(DateOnly date) => CandidateWindows(date, DefaultPeriodsFor(date));

    /// <summary>Every start time that fits fully inside one of <paramref name="periods"/> on that date.</summary>
    public IReadOnlyList<AppointmentWindow> CandidateWindows(DateOnly date, IEnumerable<(TimeOnly Start, TimeOnly End)> periods)
    {
        var windows = new List<AppointmentWindow>();
        foreach (var (start, end) in periods)
        {
            for (var localStart = start.ToTimeSpan(); localStart + Duration <= end.ToTimeSpan(); localStart += Interval)
            {
                var localStartTime = TimeOnly.FromTimeSpan(localStart);
                var localEndTime = TimeOnly.FromTimeSpan(localStart + Duration);
                var startUtc = ToUtc(date, localStartTime);
                windows.Add(new AppointmentWindow(date, startUtc, startUtc + Duration, localStartTime, localEndTime));
            }
        }
        return windows.DistinctBy(w => w.StartsAtUtc).OrderBy(w => w.StartsAtUtc).ToList();
    }

    /// <summary>Union of the start-time grids of every technician's effective schedule on that date.</summary>
    public IReadOnlyList<AppointmentWindow> CandidateWindows(DateOnly date, IEnumerable<TechnicianDayState> technicians)
        => CandidateWindows(date, technicians.SelectMany(t => PeriodsFor(t, date)).Distinct());

    /// <summary>First and last local date customers may book, relative to now.</summary>
    public (DateOnly First, DateOnly Last) BookingRange(DateTimeOffset nowUtc)
    {
        var today = LocalDateOf(nowUtc);
        return (today, today.AddDays(Math.Max(1, Options.BookingHorizonDays) - 1));
    }

    /// <summary>Default-schedule bookable windows on a date (kept for callers without technician data).</summary>
    public IReadOnlyList<AppointmentWindow> BookableWindows(DateOnly date, DateTimeOffset nowUtc) => Bookable(CandidateWindows(date), date, nowUtc);

    /// <summary>
    /// The windows inside the booking range that start no earlier than now plus the minimum lead
    /// time. Never returns past start times.
    /// </summary>
    public IReadOnlyList<AppointmentWindow> Bookable(IReadOnlyList<AppointmentWindow> windows, DateOnly date, DateTimeOffset nowUtc)
    {
        var (first, last) = BookingRange(nowUtc);
        if (date < first || date > last) return [];
        var earliest = nowUtc + TimeSpan.FromMinutes(Math.Max(0, Options.MinimumBookingLeadMinutes));
        return windows.Where(w => w.StartsAtUtc > earliest).ToList();
    }

    /// <summary>Maps a client-supplied start (any offset) to a bookable default-schedule window, or explains why not.</summary>
    public bool TryResolveBookable(DateTimeOffset requestedStart, DateTimeOffset nowUtc, out AppointmentWindow? window, out string? reason)
        => TryResolveBookable(requestedStart, nowUtc, CandidateWindows(LocalDateOf(requestedStart)), out window, out reason);

    /// <summary>Maps a client-supplied start to one of <paramref name="candidates"/> (the technicians' union grid).</summary>
    public bool TryResolveBookable(DateTimeOffset requestedStart, DateTimeOffset nowUtc, IReadOnlyList<AppointmentWindow> candidates,
        out AppointmentWindow? window, out string? reason)
    {
        var date = LocalDateOf(requestedStart);
        var match = candidates.FirstOrDefault(w => w.StartsAtUtc == requestedStart.ToUniversalTime());
        window = null;

        if (match is null)
        {
            reason = "The start time is not an allowed slot (it must start on the slot grid and fit entirely inside a technician's working hours).";
            return false;
        }

        if (!Bookable([match], date, nowUtc).Contains(match))
        {
            reason = "The start time is in the past or outside the booking horizon.";
            return false;
        }

        window = match;
        reason = null;
        return true;
    }
}
