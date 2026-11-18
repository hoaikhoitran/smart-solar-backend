using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.AfterSales;

/// <summary>A count (and, for money, an amount in VND) per status or category key.</summary>
public sealed record OverviewBucket(string Key, int Count, decimal? Amount = null);

/// <summary>Accounts of one role: total (not deleted), active, pending verification, suspended or disabled.</summary>
public sealed record OverviewRoleAccounts(string Role, int Total, int Active, int PendingVerification, int Inactive);

/// <summary>
/// Admin operational overview (2026-11-18). "Snapshot" blocks are the state now (status distributions); "period" blocks count what happened in
/// [from, to) (local dates of the scheduling time zone). Money: VND whole amounts; quotation values are offers (never revenue); collected money is
/// verified money only (PayOS by provider time, CASH by the Manager/Admin confirmation time); a pending cash choice is never collected.
/// Definitions: guides/remediation-20261118/ADMIN_DASHBOARD_METRICS.md.
/// </summary>
public sealed record AdminOverviewView(
    DateOnly From, DateOnly To, DateTimeOffset GeneratedAt,
    IReadOnlyList<OverviewRoleAccounts> Accounts,
    // ---- pre-sales / installation (snapshot)
    IReadOnlyList<OverviewBucket> SurveyRequestsByStatus,
    int OutstandingSurveyAppointments,
    IReadOnlyList<OverviewBucket> InstallationQuotationsByStatus,
    IReadOnlyList<OverviewBucket> ContractsByStatus,
    int ContractsWithOfficialPdf, int ContractsWithSignedScan, int SignedScansUploadedByStaff,
    IReadOnlyList<OverviewBucket> InstallationProjectsByStatus,
    IReadOnlyList<OverviewBucket> InstallationSchedulesByStatus,
    int OpenInstallationSlotOffers,
    // ---- after-sales (snapshot)
    IReadOnlyList<OverviewBucket> ServiceRequestsByKindAndStatus,
    IReadOnlyList<OverviewBucket> ServiceQuotationsByStatus,
    IReadOnlyList<OverviewBucket> ServiceQuotationsByIssuer,
    IReadOnlyList<OverviewBucket> DisasterClaimsByStatus,
    IReadOnlyList<OverviewBucket> SubscriptionsByKindAndStatus,
    IReadOnlyList<OverviewBucket> OpenSalesTasksByType,
    IReadOnlyList<OverviewBucket> OpenServiceVisitsByStatus,
    // ---- money (snapshot)
    OverviewBucket PendingCashChoices,
    IReadOnlyList<OverviewBucket> RefundRecordsByStatus,
    // ---- period
    IReadOnlyList<OverviewBucket> PaymentAttemptsCreatedByMethodAndStatus,
    IReadOnlyList<OverviewBucket> CollectedByMethod,
    OverviewBucket CombinedCheckoutsPaid,
    IReadOnlyList<OverviewBucket> PackageRevenueBySaleChannel,
    OverviewBucket InstallationQuotationValueIssued,
    OverviewBucket ServiceQuotationValueIssued);

public interface IAdminOverviewQueries
{
    Task<AdminOverviewView> GetAsync(DateOnly from, DateOnly to, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, CancellationToken cancellationToken);
}

/// <summary>Validates the period (default: this month; at most 366 days) and converts local dates to a UTC range.</summary>
public sealed class AdminOverview(IAdminOverviewQueries queries, WorkingCalendar calendar, TimeProvider time)
{
    public async Task<OperationResult<AdminOverviewView>> GetAsync(DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = calendar.LocalDateOf(now);
        var first = from ?? new DateOnly(today.Year, today.Month, 1);
        var last = to ?? first.AddMonths(1).AddDays(-1);
        if (last < first || last.DayNumber - first.DayNumber > 366)
            return OperationResult<AdminOverviewView>.Fail(AfterSalesErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["to"] = ["'to' must be on or after 'from' and the period at most 366 days."] });
        return OperationResult<AdminOverviewView>.Ok(await queries.GetAsync(first, last, calendar.DayBoundsUtc(first).StartUtc, calendar.DayBoundsUtc(last).EndUtc, now,
            cancellationToken));
    }
}
