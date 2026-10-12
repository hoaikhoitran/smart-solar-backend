namespace SmartSolar.Modules.Scheduling.Options;

/// <summary>
/// Configuration section "Scheduling". All values are configurable V1 defaults, not permanent
/// business restrictions. Times are local to <see cref="TimeZoneId"/>; storage is UTC.
/// </summary>
public sealed class SchedulingOptions
{
    public const string SectionName = "Scheduling";

    /// <summary>IANA time zone used for working hours and slot generation.</summary>
    public string TimeZoneId { get; set; } = "Asia/Ho_Chi_Minh";

    /// <summary>Comma-separated local working periods, each "HH:mm-HH:mm". Appointments must fit inside one period.</summary>
    public string WorkingHours { get; set; } = "08:00-12:00,13:00-17:00";

    /// <summary>
    /// Days on which the default <see cref="WorkingHours"/> apply (comma-separated MON…SUN). Default:
    /// every day, as in V1. Technicians with a custom weekly schedule ignore this and
    /// <see cref="WorkingHours"/>.
    /// </summary>
    public string WorkingDays { get; set; } = "MON,TUE,WED,THU,FRI,SAT,SUN";

    /// <summary>
    /// Earliest local time a technician may leave the base for the first appointment of the day
    /// ("HH:mm"). Independent of on-site working hours. A technician-specific value overrides it.
    /// </summary>
    public string EarliestBaseDeparture { get; set; } = "07:00";

    public int AppointmentDurationMinutes { get; set; } = 90;

    /// <summary>Spacing between bookable start times.</summary>
    public int SlotIntervalMinutes { get; set; } = 30;

    /// <summary>Upper limit of active appointments per technician per local day (not a target).</summary>
    public int MaxAppointmentsPerTechnicianPerDay { get; set; } = 3;

    /// <summary>Added on top of every provider travel estimate.</summary>
    public int SafetyBufferMinutes { get; set; } = 30;

    /// <summary>How many days ahead (including today) customers may book.</summary>
    public int BookingHorizonDays { get; set; } = 30;

    /// <summary>Minimum time between now and an appointment start. 0 = any future start.</summary>
    public int MinimumBookingLeadMinutes { get; set; } = 0;

    /// <summary>
    /// The slot search stops after this many feasible technicians; the reported count is then marked
    /// AT_LEAST rather than EXACT.
    /// </summary>
    public int MaxReportedSlotCapacity { get; set; } = 3;

    /// <summary>Upper bound of travel-estimate requests for one operation (slot listing, booking, availability).</summary>
    public int MaxRouteRequestsPerOperation { get; set; } = 60;

    /// <summary>At most this many technicians are route-checked per candidate time (nearest first).</summary>
    public int MaxTechniciansEvaluatedPerSlot { get; set; } = 6;

    /// <summary>How many times a reservation is re-estimated when the schedule changed concurrently.</summary>
    public int MaxReservationAttempts { get; set; } = 3;

    /// <summary>Runs the background worker that retries pending route recalculations (disable only in tests).</summary>
    public bool RecalculationWorkerEnabled { get; set; } = true;

    /// <summary>How often the worker looks for due recalculations.</summary>
    public int RecalculationPollSeconds { get; set; } = 30;

    /// <summary>Lease held while one instance processes a technician-day.</summary>
    public int RecalculationLeaseSeconds { get; set; } = 120;

    /// <summary>Upper bound of the exponential retry delay after a failed recalculation.</summary>
    public int RecalculationMaxBackoffMinutes { get; set; } = 60;
}
