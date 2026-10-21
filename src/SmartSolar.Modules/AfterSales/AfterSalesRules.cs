namespace SmartSolar.Modules.AfterSales;

/// <summary>
/// Pure business calculations of Flow 3 (no database). Kept in one place so they are easy to read and to unit test.
/// Calendar math is done in the business time zone (Vietnam) and returned in UTC; every period end is exclusive.
/// </summary>
public static class AfterSalesRules
{
    /// <summary>"6,12" → [6, 12]. Returns an empty list for empty input; throws for anything that is not a positive month list.</summary>
    public static IReadOnlyList<int> ParseMonths(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [];
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
    }

    public static IReadOnlyList<string> ParseList(string? csv)
        => string.IsNullOrWhiteSpace(csv) ? [] : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// End (exclusive) of a term of <paramref name="months"/> calendar months. Uses month anniversaries, not 30-day months:
    /// 20 Oct 2026 + 12 months = 20 Oct 2027; 31 Jan + 1 month = 28/29 Feb.
    /// </summary>
    public static DateTimeOffset AddMonths(DateTimeOffset start, int months, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(start, zone);
        return local.AddMonths(months).ToUniversalTime();
    }

    /// <summary>
    /// The window of plan month <paramref name="planMonth"/> (1-based) of a term that starts at <paramref name="termStart"/>:
    /// [start + (month − 1) months, start + month months). Example: a term from 20 Oct 2026 has month 6 = 20 Mar 2027 – 20 Apr 2027.
    /// </summary>
    public static (DateTimeOffset StartsAt, DateTimeOffset EndsAt) MaintenancePeriod(DateTimeOffset termStart, int planMonth, TimeZoneInfo zone)
        => (AddMonths(termStart, planMonth - 1, zone), AddMonths(termStart, planMonth, zone));

    /// <summary>Whole VND, rounded DOWN, so support is never more than the computed share.</summary>
    public static decimal RoundDownVnd(decimal amount) => Math.Floor(amount);

    /// <summary>
    /// Disaster support for an eligible repair cost: the package percentage of the cost, never above the remaining per-incident
    /// limit nor the remaining term limit (both already net of reserved and consumed support). Example: 10,000,000 eligible at 70 %
    /// = 7,000,000 before caps.
    /// </summary>
    public static decimal DisasterSupport(decimal eligibleAmount, decimal coveragePercent, decimal incidentRemaining, decimal termRemaining)
    {
        var share = RoundDownVnd(eligibleAmount * coveragePercent / 100m);
        var support = Math.Min(share, Math.Min(incidentRemaining, termRemaining));
        return Math.Max(0, support);
    }

    /// <summary>
    /// Whether a disaster-protection term covered an incident: the incident happened after the waiting period and before the term
    /// end, the disaster type is one the package covers, and the claim was submitted while the term was still running.
    /// </summary>
    public static string? DisasterIneligibility(ServiceSubscription term, string disasterType, DateTimeOffset incidentAt, DateTimeOffset submittedAt)
    {
        if (term.CoverageStartsAt is not { } coverageStart || term.EndsAt is not { } end) return "The protection term has not started.";
        if (!ParseList(term.CoveredDisasterTypes).Contains(disasterType)) return $"{disasterType} is not covered by the package {term.PackageName}.";
        if (incidentAt < coverageStart) return "The incident happened before coverage started (waiting period or before the term).";
        if (incidentAt >= end) return "The incident happened after the protection term ended.";
        if (submittedAt >= end) return "The claim was submitted after the protection term ended.";
        return null;
    }

    /// <summary>A booking start must be at least the lead time ahead (default 7 days).</summary>
    public static bool RespectsLead(DateTimeOffset startsAt, DateTimeOffset now, int leadDays) => startsAt >= now.AddDays(leadDays);
}
