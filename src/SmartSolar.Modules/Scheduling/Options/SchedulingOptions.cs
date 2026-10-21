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
    /// Days on which the default <see cref="WorkingHours"/> apply (comma-separated MON…SUN). Technicians with
    /// a custom weekly schedule ignore this and <see cref="WorkingHours"/>.
    /// </summary>
    public string WorkingDays { get; set; } = "MON,TUE,WED,THU,FRI,SAT,SUN";

    public int AppointmentDurationMinutes { get; set; } = 90;

    /// <summary>Spacing between bookable start times.</summary>
    public int SlotIntervalMinutes { get; set; } = 30;

    /// <summary>Upper limit of survey appointments (pending holds included) per technician per local day.</summary>
    public int MaxAppointmentsPerTechnicianPerDay { get; set; } = 3;

    /// <summary>
    /// Minimum gap between two commitments of the same technician (survey appointments and installation
    /// assignments): previous end + buffer &lt;= start and end + buffer &lt;= next start. A conservative fixed
    /// buffer, NOT a travel-time estimate.
    /// </summary>
    public int BufferBetweenAppointmentsMinutes { get; set; } = 30;

    /// <summary>How many days ahead (including today) customers may book.</summary>
    public int BookingHorizonDays { get; set; } = 30;

    /// <summary>Minimum time between now and an appointment start. 0 = any future start.</summary>
    public int MinimumBookingLeadMinutes { get; set; } = 0;

    /// <summary>The slot listing reports at most this many eligible holders per slot (CapacityStatus AT_LEAST beyond it).</summary>
    public int MaxReportedSlotCapacity { get; set; } = 3;

    /// <summary>How many times a reservation or acceptance is retried when the schedule changed concurrently.</summary>
    public int MaxReservationAttempts { get; set; } = 3;

    // ---- Bootstrap defaults of admin-editable settings ----
    // AcceptanceWindowMinutes, AcceptanceMinimumLeadMinutes, CustomerChangeLeadMinutes, MinimumBookingLeadMinutes, BookingHorizonDays and
    // BufferBetweenAppointmentsMinutes are only the DEFAULTS of the system settings (scheduling.*): handlers and workers read the effective
    // values from ISchedulingPolicyProvider, never from these properties.

    /// <summary>
    /// Default of <c>scheduling.acceptanceWindowMinutes</c>: technicians have this long after booking to self-accept, deadline =
    /// LEAST(booked_at + window, starts_at − minimum lead).
    /// </summary>
    public int AcceptanceWindowMinutes { get; set; } = 240;

    /// <summary>Acceptance must happen at least this long before the start, otherwise the appointment escalates to staff.</summary>
    public int AcceptanceMinimumLeadMinutes { get; set; } = 120;

    /// <summary>Customers may reschedule or cancel until this long before the start.</summary>
    public int CustomerChangeLeadMinutes { get; set; } = 120;

    /// <summary>Runs the background worker that escalates pending appointments past their deadline.</summary>
    public bool EscalationWorkerEnabled { get; set; } = true;

    /// <summary>How often the escalation worker runs.</summary>
    public int EscalationPollSeconds { get; set; } = 60;
}
