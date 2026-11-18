using SmartSolar.Modules.Common.Results;
using SmartSolar.Modules.Scheduling.Domain;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>A count and an amount (VND).</summary>
public sealed record MoneyBucket(string Key, int Count, decimal Amount);

/// <summary>
/// Admin finance overview of installation contracts (2026-11-19, 30/70 plan). Snapshot figures are the state now; period figures count money verified in
/// [from, to] (local dates of the business time zone, converted to a UTC range). Rules (no double counting):
/// <list type="bullet">
/// <item>Applied money = SETTLED payment lines of PAID attempts only (deposit / balance installment lines, legacy contract lines). PENDING attempts,
/// pending cash choices, REVIEW_REQUIRED / REFUND_REQUIRED money are never counted as collected; they appear as unapplied.</item>
/// <item>Period time of a payment: PayOS provider transaction time, CASH confirmation time.</item>
/// <item>Outstanding = Σ installments not PAID of live 30/70 contracts (PENDING balance included); due now = DUE installments; overdue = DUE past due_at.</item>
/// <item>Zone filter: the installation project's operational zone (contracts without a project are excluded when a zone is given).</item>
/// </list>
/// </summary>
public sealed record FinanceOverviewView(
    DateOnly From, DateOnly To, Guid? ZoneId, DateTimeOffset GeneratedAt,
    // ---- snapshot
    MoneyBucket PaymentPlanContracts, MoneyBucket LegacyContracts,
    MoneyBucket DepositsPaid, MoneyBucket DepositsDue,
    MoneyBucket BalancesPending, MoneyBucket BalancesDue, MoneyBucket BalancesOverdue, MoneyBucket BalancesPaid, int BalancesPaidLate,
    MoneyBucket Outstanding, MoneyBucket OutstandingDueNow,
    MoneyBucket AwaitingAcceptance, MoneyBucket AcceptedUnpaid, MoneyBucket FullyPaid,
    IReadOnlyList<MoneyBucket> ContractsByStage,
    IReadOnlyList<MoneyBucket> OpenCollectionCasesByStatus, MoneyBucket Negotiations, MoneyBucket Escalations,
    // ---- period
    MoneyBucket DepositsCollected, MoneyBucket BalancesCollected, IReadOnlyList<MoneyBucket> InstallmentsCollectedByMethod,
    MoneyBucket LegacyContractPaymentsCollected, MoneyBucket Unapplied, MoneyBucket Refunded);

public interface IFinanceOverviewQueries
{
    Task<FinanceOverviewView> GetAsync(DateOnly from, DateOnly to, Guid? zoneId, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}

/// <summary>Validates the period (default this month; at most 366 days) and converts local dates to a UTC range.</summary>
public sealed class FinanceOverview(IFinanceOverviewQueries queries, WorkingCalendar calendar, TimeProvider time)
{
    public async Task<OperationResult<FinanceOverviewView>> GetAsync(DateOnly? from, DateOnly? to, Guid? zoneId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var today = calendar.LocalDateOf(now);
        var first = from ?? new DateOnly(today.Year, today.Month, 1);
        var last = to ?? first.AddMonths(1).AddDays(-1);
        if (last < first || last.DayNumber - first.DayNumber > 366)
            return OperationResult<FinanceOverviewView>.Fail(CommonErrorCodes.ValidationFailed, "One or more fields are invalid.",
                new Dictionary<string, string[]> { ["to"] = ["'to' must be on or after 'from' and the period at most 366 days."] });
        return OperationResult<FinanceOverviewView>.Ok(await queries.GetAsync(first, last, zoneId, calendar.DayBoundsUtc(first).StartUtc, calendar.DayBoundsUtc(last).EndUtc,
            now, cancellationToken));
    }
}
