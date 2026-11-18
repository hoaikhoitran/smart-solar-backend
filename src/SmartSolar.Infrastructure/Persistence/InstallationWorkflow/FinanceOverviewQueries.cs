using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Installations;
using SmartSolar.Modules.Payments;

namespace SmartSolar.Infrastructure.Persistence.InstallationWorkflow;

/// <summary>Finance overview of installation contracts (definitions on <see cref="FinanceOverviewView"/>). Reads only; projections in SQL, sums in memory.</summary>
public sealed class FinanceOverviewQueries(AppDbContext db) : IFinanceOverviewQueries
{
    public async Task<FinanceOverviewView> GetAsync(DateOnly from, DateOnly to, Guid? zoneId, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var contracts = db.CustomerContracts.AsNoTracking()
            .Where(c => c.Status != ContractStatuses.Superseded && c.Status != ContractStatuses.Discarded);
        if (zoneId is { } zone) contracts = contracts.Where(c => db.InstallationProjects.Any(p => p.ContractId == c.Id && p.OperationalZoneId == zone));
        var rows = await contracts.Select(c => new
        {
            c.Id, c.Status, c.PaymentPlan, c.TotalAmount,
            ProjectStatus = db.InstallationProjects.Where(p => p.ContractId == c.Id).Select(p => p.Status).FirstOrDefault(),
        }).ToListAsync(ct);
        var planIds = rows.Where(r => r.PaymentPlan != null).Select(r => r.Id).ToList();
        var installments = await db.ContractInstallments.AsNoTracking().Where(i => planIds.Contains(i.ContractId)).ToListAsync(ct);
        var byContract = installments.ToLookup(i => i.ContractId);

        static MoneyBucket Bucket(string key, IEnumerable<decimal> amounts)
        {
            var list = amounts.ToList();
            return new MoneyBucket(key, list.Count, list.Sum());
        }

        var plan = rows.Where(r => r.PaymentPlan != null).ToList();
        var legacy = rows.Where(r => r.PaymentPlan == null).ToList();
        var deposits = installments.Where(i => i.Kind == InstallmentKinds.Deposit).ToList();
        var balances = installments.Where(i => i.Kind == InstallmentKinds.Balance).ToList();
        bool Overdue(ContractInstallment i) => PaymentPlanCalculator.IsOverdue(i, nowUtc);
        var notPaid = installments.Where(i => i.Status is InstallmentStatuses.Pending or InstallmentStatuses.Due).ToList();
        var awaitingAcceptance = plan.Where(r => r.ProjectStatus == InstallationStatuses.PendingCustomerAcceptance).ToList();
        var stages = plan.Select(r =>
        {
            var probe = new CustomerContract { Status = r.Status, PaymentPlan = r.PaymentPlan, TotalAmount = r.TotalAmount };
            return (Stage: PaymentPlanViews.Build(probe, byContract[r.Id].ToList(), [], [], null, nowUtc)!.Stage, r.TotalAmount);
        }).GroupBy(x => x.Stage).Select(g => new MoneyBucket(g.Key, g.Count(), g.Sum(x => x.TotalAmount))).OrderBy(b => b.Key).ToList();

        // ---- collection (open cases of the contracts in scope)
        var cases = await db.ContractCollectionCases.AsNoTracking().Where(c => c.Status != CollectionCaseStatuses.Resolved && planIds.Contains(c.ContractId))
            .Select(c => new { c.Status, c.AmountDue }).ToListAsync(ct);
        var caseBuckets = cases.GroupBy(c => c.Status).Select(g => new MoneyBucket(g.Key, g.Count(), g.Sum(c => c.AmountDue))).OrderBy(b => b.Key).ToList();

        // ---- period: verified money (PayOS provider time, CASH confirmation time)
        var received = await db.PaymentAttempts.AsNoTracking()
            .Where(a => a.AmountPaid != null && (a.Status == PaymentStatuses.Paid || a.Status == PaymentStatuses.RefundRequired || a.Status == PaymentStatuses.ReviewRequired))
            .Where(a => (a.ProviderTransactionAt ?? a.PaidAt ?? a.UpdatedAt) >= fromUtc && (a.ProviderTransactionAt ?? a.PaidAt ?? a.UpdatedAt) < toUtc)
            .Select(a => new { a.Id, a.Status, a.Provider, a.AmountPaid, a.ContractId }).ToListAsync(ct);
        var receivedIds = received.Select(a => a.Id).ToList();
        var lines = await db.PaymentAttemptItems.AsNoTracking().Where(l => receivedIds.Contains(l.PaymentAttemptId))
            .Select(l => new { l.PaymentAttemptId, l.ItemType, l.Amount, l.Status, l.ContractInstallmentId, l.ContractId }).ToListAsync(ct);
        var linesByAttempt = lines.ToLookup(l => l.PaymentAttemptId);
        var inScope = installments.ToDictionary(i => i.Id);
        var legacyIds = legacy.Select(r => r.Id).ToHashSet();

        var depositsCollected = new List<decimal>();
        var balancesCollected = new List<decimal>();
        var byMethod = new Dictionary<string, List<decimal>> { ["ONLINE"] = [], ["CASH"] = [] };
        var legacyCollected = new List<decimal>();
        var unapplied = new List<decimal>();
        foreach (var a in received)
        {
            var mine = linesByAttempt[a.Id].ToList();
            var installmentLines = mine.Where(l => l.ContractInstallmentId is { } id && inScope.ContainsKey(id)).ToList();
            var gross = (decimal)a.AmountPaid!.Value;
            if (installmentLines.Count > 0)
            {
                var settled = a.Status == PaymentStatuses.Paid ? installmentLines.Where(l => l.Status == PaymentItemStatuses.Settled).ToList() : [];
                foreach (var l in settled)
                {
                    (inScope[l.ContractInstallmentId!.Value].Kind == InstallmentKinds.Deposit ? depositsCollected : balancesCollected).Add(l.Amount);
                    byMethod[a.Provider == PaymentProviders.Cash ? "CASH" : "ONLINE"].Add(l.Amount);
                }
                var unappliedAmount = installmentLines.Sum(l => l.Amount) - settled.Sum(l => l.Amount);
                if (a.Status != PaymentStatuses.Paid) unappliedAmount = gross;
                if (unappliedAmount > 0) unapplied.Add(unappliedAmount);
                continue;
            }
            // Legacy full-payment contracts (contract attempts / contract lines / quotation paid before its contract).
            var legacyContract = a.ContractId is { } cid && legacyIds.Contains(cid);
            if (!legacyContract) continue;
            if (a.Status != PaymentStatuses.Paid) { unapplied.Add(gross); continue; }
            legacyCollected.Add(mine.Count == 0 ? gross
                : mine.Where(l => l.Status == PaymentItemStatuses.Settled && l.ItemType is PaymentItemTypes.Contract or PaymentItemTypes.InstallationQuotation).Sum(l => l.Amount));
        }

        // Refunds actually returned in the period for attempts of the contracts in scope (manual; there is no refund button).
        var refunds = await db.RefundRecords.AsNoTracking()
            .Where(r => r.Status == RefundStatuses.Refunded && r.RefundedAt >= fromUtc && r.RefundedAt < toUtc && r.PaymentAttemptId != null)
            .Select(r => new { r.PaymentAttemptId, Amount = r.ApprovedAmount ?? r.OriginalAmount }).ToListAsync(ct);
        var scopedAttemptIds = await db.PaymentAttemptItems.AsNoTracking()
            .Where(l => l.ContractInstallmentId != null && db.ContractInstallments.Any(i => i.Id == l.ContractInstallmentId && planIds.Contains(i.ContractId)))
            .Select(l => l.PaymentAttemptId).Distinct().ToListAsync(ct);
        var scoped = scopedAttemptIds.ToHashSet();
        var refunded = refunds.Where(r => scoped.Contains(r.PaymentAttemptId!.Value)).Select(r => r.Amount);

        return new FinanceOverviewView(from, to, zoneId, nowUtc,
            Bucket("PAYMENT_PLAN", plan.Select(r => r.TotalAmount)), Bucket("LEGACY_FULL_PAYMENT", legacy.Select(r => r.TotalAmount)),
            Bucket("DEPOSIT_PAID", deposits.Where(i => i.Status == InstallmentStatuses.Paid).Select(i => i.Amount)),
            Bucket("DEPOSIT_DUE", deposits.Where(i => i.Status == InstallmentStatuses.Due).Select(i => i.Amount)),
            Bucket("BALANCE_PENDING", balances.Where(i => i.Status == InstallmentStatuses.Pending).Select(i => i.Amount)),
            Bucket("BALANCE_DUE", balances.Where(i => i.Status == InstallmentStatuses.Due && !Overdue(i)).Select(i => i.Amount)),
            Bucket("BALANCE_OVERDUE", balances.Where(Overdue).Select(i => i.Amount)),
            Bucket("BALANCE_PAID", balances.Where(i => i.Status == InstallmentStatuses.Paid).Select(i => i.Amount)),
            balances.Count(i => i.Status == InstallmentStatuses.Paid && i.PaidLate),
            Bucket("OUTSTANDING", notPaid.Select(i => i.Amount)),
            Bucket("OUTSTANDING_DUE_NOW", notPaid.Where(i => i.Status == InstallmentStatuses.Due).Select(i => i.Amount)),
            Bucket("AWAITING_ACCEPTANCE", awaitingAcceptance.Select(r => byContract[r.Id].Where(i => i.Kind == InstallmentKinds.Balance).Sum(i => i.Amount))),
            Bucket("ACCEPTED_UNPAID", balances.Where(i => i.Status == InstallmentStatuses.Due).Select(i => i.Amount)),
            Bucket("FULLY_PAID", plan.Where(r => r.Status == ContractStatuses.Paid).Select(r => r.TotalAmount)),
            stages, caseBuckets,
            Bucket("NEGOTIATIONS", cases.Where(c => c.Status is CollectionCaseStatuses.NegotiationInProgress or CollectionCaseStatuses.AgreementRecorded).Select(c => c.AmountDue)),
            Bucket("ESCALATIONS", cases.Where(c => c.Status is CollectionCaseStatuses.EscalatedToManager or CollectionCaseStatuses.LegalReviewRequired).Select(c => c.AmountDue)),
            Bucket("DEPOSITS_COLLECTED", depositsCollected), Bucket("BALANCES_COLLECTED", balancesCollected),
            byMethod.Select(kv => Bucket(kv.Key, kv.Value)).ToList(),
            Bucket("LEGACY_CONTRACT_PAYMENTS_COLLECTED", legacyCollected), Bucket("UNAPPLIED", unapplied), Bucket("REFUNDED", refunded));
    }
}
