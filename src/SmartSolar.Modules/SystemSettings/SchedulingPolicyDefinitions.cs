using SmartSolar.Modules.Installations;
using SmartSolar.Modules.OnSiteSurveys;
using SmartSolar.Modules.Scheduling.Options;

namespace SmartSolar.Modules.SystemSettings;

public static class SystemSettingUnits
{
    public const string Minutes = "MINUTES";
    public const string Hours = "HOURS";
    public const string Days = "DAYS";

    public static readonly IReadOnlyList<string> All = [Minutes, Hours, Days];
}

public static class SystemSettingTypes
{
    public const string Int = "INT";
}

/// <summary>Stable keys of the admin-editable scheduling policy. Keys are part of the API contract: never rename one.</summary>
public static class SchedulingPolicyKeys
{
    public const string DispatchReviewSlaMinutes = "dispatch.reviewSlaMinutes";
    public const string DispatchEscalationDelayMinutes = "dispatch.escalationDelayMinutes";
    public const string DispatchReminderIntervalMinutes = "dispatch.reminderIntervalMinutes";
    public const string DispatchProposalResponseWindowMinutes = "dispatch.proposalResponseWindowMinutes";
    public const string AcceptanceWindowMinutes = "scheduling.acceptanceWindowMinutes";
    public const string AcceptanceMinimumLeadMinutes = "scheduling.acceptanceMinimumLeadMinutes";
    public const string AcceptanceReminderLeadMinutes = "scheduling.acceptanceReminderLeadMinutes";
    public const string CustomerChangeLeadMinutes = "scheduling.customerChangeLeadMinutes";
    public const string MinimumBookingLeadMinutes = "scheduling.minimumBookingLeadMinutes";
    public const string BookingHorizonDays = "scheduling.bookingHorizonDays";
    public const string BufferBetweenAppointmentsMinutes = "scheduling.bufferBetweenAppointmentsMinutes";
    public const string OnSiteSurveyEarlyStartMinutes = "onsiteSurvey.earlyStartMinutes";
    public const string InstallationMaxAssignmentDays = "installation.maxAssignmentDays";
}

/// <summary>One typed setting: stable key, integer type, unit, inclusive range, bootstrap default and description.</summary>
public sealed record SystemSettingDefinition(string Key, string Type, string Unit, int Min, int Max, int Default, string Description, bool AdminEditable = true);

/// <summary>
/// Registry of the scheduling policy settings with their validation. Defaults are the current "Scheduling", "OnSiteSurvey" and
/// "Installation" option values (code defaults overridden by appsettings); the dispatch defaults are fixed in code. Ranges match
/// the startup validation of those options. Infrastructure knobs (poll intervals, timeouts, retry counts, working calendars) are
/// deliberately not part of the registry.
/// </summary>
public sealed class SchedulingPolicyDefinitions
{
    /// <summary>Smallest non-zero reminder interval (0 disables reminders).</summary>
    public const int MinimumReminderIntervalMinutes = 15;

    public const int MinutesPerDay = 1440;

    /// <summary>Human-readable cross-field rules, returned by the API next to the values.</summary>
    public static readonly IReadOnlyList<string> RuleDescriptions =
    [
        $"{SchedulingPolicyKeys.MinimumBookingLeadMinutes} < {SchedulingPolicyKeys.BookingHorizonDays} * 1440",
        $"{SchedulingPolicyKeys.AcceptanceMinimumLeadMinutes} < {SchedulingPolicyKeys.BookingHorizonDays} * 1440",
        $"{SchedulingPolicyKeys.CustomerChangeLeadMinutes} < {SchedulingPolicyKeys.BookingHorizonDays} * 1440",
        $"{SchedulingPolicyKeys.AcceptanceReminderLeadMinutes} = 0 (disabled) or < {SchedulingPolicyKeys.AcceptanceWindowMinutes}",
        $"{SchedulingPolicyKeys.DispatchReminderIntervalMinutes} = 0 (disabled) or >= {MinimumReminderIntervalMinutes}",
    ];

    private readonly Dictionary<string, SystemSettingDefinition> _byKey;

    public SchedulingPolicyDefinitions(IEnumerable<SystemSettingDefinition> definitions)
    {
        All = definitions.ToList();
        _byKey = All.ToDictionary(d => d.Key, StringComparer.Ordinal);
        Defaults = All.ToDictionary(d => d.Key, d => d.Default, StringComparer.Ordinal);
    }

    public IReadOnlyList<SystemSettingDefinition> All { get; }

    public IReadOnlyDictionary<string, int> Defaults { get; }

    public static SchedulingPolicyDefinitions Default { get; } = Create(new SchedulingOptions(), new OnSiteSurveyOptions(), new InstallationOptions());

    public static SchedulingPolicyDefinitions Create(SchedulingOptions scheduling, OnSiteSurveyOptions onSite, InstallationOptions installation)
    {
        const string M = SystemSettingUnits.Minutes;
        const string D = SystemSettingUnits.Days;
        const string I = SystemSettingTypes.Int;
        return new SchedulingPolicyDefinitions(
        [
            new(SchedulingPolicyKeys.DispatchReviewSlaMinutes, I, M, 5, 10080, 240,
                "Time staff have to review a new uncovered-zone dispatch request (review_due_at = created_at + value, snapshotted)."),
            new(SchedulingPolicyKeys.DispatchEscalationDelayMinutes, I, M, 0, 10080, 0,
                "Delay after the review deadline before an overdue dispatch request is escalated to Admin (0 = at the deadline)."),
            new(SchedulingPolicyKeys.DispatchReminderIntervalMinutes, I, M, 0, 10080, 0,
                "Interval of staff reminders while a dispatch request is overdue (0 = reminders disabled; otherwise at least 15)."),
            new(SchedulingPolicyKeys.DispatchProposalResponseWindowMinutes, I, M, 15, 10080, 1440,
                "Time a customer has to answer a dispatch proposal (expires_at = LEAST(created_at + value, starts_at), snapshotted)."),
            new(SchedulingPolicyKeys.AcceptanceWindowMinutes, I, M, 5, 10080, scheduling.AcceptanceWindowMinutes,
                "Time a technician has after booking to self-accept an appointment (deadline = LEAST(booked_at + value, starts_at - minimum lead))."),
            new(SchedulingPolicyKeys.AcceptanceMinimumLeadMinutes, I, M, 0, 10080, scheduling.AcceptanceMinimumLeadMinutes,
                "Acceptance must happen at least this long before the start, otherwise the appointment escalates to staff."),
            new(SchedulingPolicyKeys.AcceptanceReminderLeadMinutes, I, M, 0, 10080, 0,
                "Reminder to the technician this long before the acceptance deadline (0 = disabled; otherwise below the acceptance window)."),
            new(SchedulingPolicyKeys.CustomerChangeLeadMinutes, I, M, 0, 10080, scheduling.CustomerChangeLeadMinutes,
                "Customers may reschedule or cancel until this long before the start."),
            new(SchedulingPolicyKeys.MinimumBookingLeadMinutes, I, M, 0, 10080, scheduling.MinimumBookingLeadMinutes,
                "Minimum time between now and a bookable appointment start (0 = any future start)."),
            new(SchedulingPolicyKeys.BookingHorizonDays, I, D, 1, 365, scheduling.BookingHorizonDays,
                "How many days ahead (including today) customers may book."),
            new(SchedulingPolicyKeys.BufferBetweenAppointmentsMinutes, I, M, 0, 240, scheduling.BufferBetweenAppointmentsMinutes,
                "Fixed minimum gap between two commitments of the same technician (not a travel-time estimate)."),
            new(SchedulingPolicyKeys.OnSiteSurveyEarlyStartMinutes, I, M, 0, 1440, Math.Max(0, onSite.EarlyStartMinutes),
                "How long before the appointment start the technician may start the on-site survey (also the release cut-off)."),
            new(SchedulingPolicyKeys.InstallationMaxAssignmentDays, I, D, 1, 365, installation.MaxAssignmentDays,
                "Longest installation assignment, in local calendar days touched by the interval."),
        ]);
    }

    public SystemSettingDefinition? Find(string? key) => key is not null && _byKey.TryGetValue(key, out var d) ? d : null;

    /// <summary>Range errors of one value against its definition (empty when valid).</summary>
    public static string? RangeError(SystemSettingDefinition definition, int value)
        => value < definition.Min || value > definition.Max
            ? $"Must be between {definition.Min} and {definition.Max} {definition.Unit.ToLowerInvariant()}."
            : null;

    /// <summary>Cross-field rules over a complete set of effective values, keyed by the offending setting.</summary>
    public static Dictionary<string, string[]> RuleErrors(IReadOnlyDictionary<string, int> values)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        int V(string key) => values.TryGetValue(key, out var v) ? v : 0;
        var horizonMinutes = (long)V(SchedulingPolicyKeys.BookingHorizonDays) * MinutesPerDay;

        if (V(SchedulingPolicyKeys.MinimumBookingLeadMinutes) >= horizonMinutes)
            errors[SchedulingPolicyKeys.MinimumBookingLeadMinutes] = [$"Must be below the booking horizon ({horizonMinutes} minutes)."];
        if (V(SchedulingPolicyKeys.AcceptanceMinimumLeadMinutes) >= horizonMinutes)
            errors[SchedulingPolicyKeys.AcceptanceMinimumLeadMinutes] = [$"Must be below the booking horizon ({horizonMinutes} minutes)."];
        if (V(SchedulingPolicyKeys.CustomerChangeLeadMinutes) >= horizonMinutes)
            errors[SchedulingPolicyKeys.CustomerChangeLeadMinutes] = [$"Must be below the booking horizon ({horizonMinutes} minutes)."];
        var reminderLead = V(SchedulingPolicyKeys.AcceptanceReminderLeadMinutes);
        if (reminderLead != 0 && reminderLead >= V(SchedulingPolicyKeys.AcceptanceWindowMinutes))
            errors[SchedulingPolicyKeys.AcceptanceReminderLeadMinutes] = [$"Must be 0 (disabled) or below {SchedulingPolicyKeys.AcceptanceWindowMinutes}."];
        var reminder = V(SchedulingPolicyKeys.DispatchReminderIntervalMinutes);
        if (reminder is > 0 and < MinimumReminderIntervalMinutes)
            errors[SchedulingPolicyKeys.DispatchReminderIntervalMinutes] = [$"Must be 0 (disabled) or at least {MinimumReminderIntervalMinutes} minutes."];
        return errors;
    }

    /// <summary>Startup check of the bootstrap defaults (ranges and cross-field rules); empty when valid.</summary>
    public IReadOnlyList<string> ValidateDefaults()
    {
        var errors = All.Select(d => RangeError(d, d.Default) is { } e ? $"Default of '{d.Key}' ({d.Default}): {e}" : null).OfType<string>().ToList();
        errors.AddRange(RuleErrors(Defaults).Select(e => $"Default of '{e.Key}': {string.Join(" ", e.Value)}"));
        return errors;
    }

    /// <summary>
    /// Effective values: stored rows overlaid on the defaults. A stored value outside the current range (ranges narrowed by a
    /// later release) or with another unit falls back to the default; rows of unknown keys are ignored.
    /// </summary>
    public Dictionary<string, int> Effective(IEnumerable<SystemSetting> rows)
    {
        var values = new Dictionary<string, int>(Defaults, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (Find(row.Key) is { } d && IsUsable(d, row)) values[d.Key] = row.ValueInt;
        }
        return values;
    }

    public static bool IsUsable(SystemSettingDefinition definition, SystemSetting row)
        => row.Unit == definition.Unit && RangeError(definition, row.ValueInt) is null;
}
