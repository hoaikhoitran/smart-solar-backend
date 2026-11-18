using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>
/// The payment settlement used by <see cref="PaymentService"/>:
/// <list type="bullet">
/// <item>CONTRACT_INSTALLMENT lines (30/70 plan, 2026-11-19) settle exactly their installment through <see cref="PaymentPlanLedger.SettleAsync"/>;</item>
/// <item>INSTALLATION_QUOTATION lines (2026-11-18 flow, kept for payments opened before the 30/70 release) create the contract from the paid snapshot
/// (<see cref="ContractHandlers.CreateForPaidQuotationAsync"/>);</item>
/// <item>every other line (packages, repair / maintenance quotations) goes to the after-sales settlement unchanged (after-sales stays 100% upfront).</item>
/// </list>
/// Runs inside the payment confirmation transaction, so a line is applied at most once. Money that cannot be applied is never ignored: the line becomes
/// REVIEW_REQUIRED and an unapplied-money (refund) record is opened for a Manager — no refund is executed automatically.
/// </summary>
public sealed class InstallationQuotationPaymentSettlement(ContractHandlers contracts, IPaymentItemSettlement? afterSales = null, PaymentPlanLedger? ledger = null)
    : IPaymentItemSettlement
{
    private static bool IsContractLine(PaymentAttemptItem i) => i.ItemType is PaymentItemTypes.InstallationQuotation or PaymentItemTypes.ContractInstallment;

    public async Task SettleAsync(PaymentAttempt attempt, IReadOnlyList<PaymentAttemptItem> items, DateTimeOffset providerPaidAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(IsContractLine))
        {
            var problem = await ProblemAsync(attempt, item, providerPaidAt, now, cancellationToken, checkOnly: false);
            if (problem is null)
            {
                item.Status = PaymentItemStatuses.Settled;
                continue;
            }
            item.Status = PaymentItemStatuses.ReviewRequired;
            item.Note = problem.Length <= 300 ? problem : problem[..300];
            if (afterSales is not null) await afterSales.RecordUnappliedPaymentAsync(attempt, item.Amount, problem, now, cancellationToken);
        }

        var others = items.Where(i => !IsContractLine(i)).ToList();
        if (others.Count == 0) return;
        if (afterSales is null)
        {
            foreach (var line in others)
            {
                line.Status = PaymentItemStatuses.ReviewRequired;
                line.Note = "After-sales settlement is not available.";
            }
            return;
        }
        await afterSales.SettleAsync(attempt, others, providerPaidAt, now, cancellationToken);
    }

    public Task RecordUnappliedPaymentAsync(PaymentAttempt attempt, decimal amount, string reason, DateTimeOffset now, CancellationToken cancellationToken)
        => afterSales?.RecordUnappliedPaymentAsync(attempt, amount, reason, now, cancellationToken) ?? Task.CompletedTask;

    public async Task<string?> CashSettlementProblemAsync(PaymentAttempt attempt, IReadOnlyList<PaymentAttemptItem> items, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(IsContractLine))
            if (await ProblemAsync(attempt, item, now, now, cancellationToken, checkOnly: true) is { } problem)
                return problem;
        var others = items.Where(i => !IsContractLine(i)).ToList();
        return others.Count == 0 || afterSales is null ? null : await afterSales.CashSettlementProblemAsync(attempt, others, now, cancellationToken);
    }

    private async Task<string?> ProblemAsync(PaymentAttempt attempt, PaymentAttemptItem item, DateTimeOffset providerPaidAt, DateTimeOffset now,
        CancellationToken cancellationToken, bool checkOnly)
    {
        if (item.ItemType == PaymentItemTypes.ContractInstallment)
            return ledger is null
                ? "Installment settlement is not available."
                : await ledger.SettleAsync(attempt, item, providerPaidAt, now, cancellationToken, checkOnly);
        return item.InstallationQuotationId is { } quotationId
            ? await contracts.CreateForPaidQuotationAsync(quotationId, attempt, item.Amount, providerPaidAt, now, cancellationToken, checkOnly)
            : "The payment line names no installation quotation.";
    }
}
