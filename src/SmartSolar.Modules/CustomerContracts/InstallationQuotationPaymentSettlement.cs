using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.CustomerContracts;

/// <summary>
/// The payment settlement used by <see cref="PaymentService"/> (2026-11-18): INSTALLATION_QUOTATION lines — an installation quotation agreed on site
/// and paid before its contract exists — create the contract from the paid snapshot (<see cref="ContractHandlers.CreateForPaidQuotationAsync"/>);
/// every other line (packages, repair / maintenance quotations) goes to the after-sales settlement unchanged. Runs inside the payment confirmation
/// transaction, so a line is applied at most once. Money that cannot be applied is never ignored: the line becomes REVIEW_REQUIRED and an
/// unapplied-money (refund) record is opened for a Manager — no refund is executed automatically.
/// </summary>
public sealed class InstallationQuotationPaymentSettlement(ContractHandlers contracts, IPaymentItemSettlement? afterSales = null) : IPaymentItemSettlement
{
    public async Task SettleAsync(PaymentAttempt attempt, IReadOnlyList<PaymentAttemptItem> items, DateTimeOffset providerPaidAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(i => i.ItemType == PaymentItemTypes.InstallationQuotation))
        {
            var problem = item.InstallationQuotationId is { } quotationId
                ? await contracts.CreateForPaidQuotationAsync(quotationId, attempt, item.Amount, providerPaidAt, now, cancellationToken)
                : "The payment line names no installation quotation.";
            if (problem is null)
            {
                item.Status = PaymentItemStatuses.Settled;
                continue;
            }
            item.Status = PaymentItemStatuses.ReviewRequired;
            item.Note = problem.Length <= 300 ? problem : problem[..300];
            if (afterSales is not null) await afterSales.RecordUnappliedPaymentAsync(attempt, item.Amount, problem, now, cancellationToken);
        }

        var others = items.Where(i => i.ItemType != PaymentItemTypes.InstallationQuotation).ToList();
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
        foreach (var item in items.Where(i => i.ItemType == PaymentItemTypes.InstallationQuotation))
        {
            if (item.InstallationQuotationId is not { } quotationId) return "The payment line names no installation quotation.";
            if (await contracts.CreateForPaidQuotationAsync(quotationId, attempt, item.Amount, now, now, cancellationToken, checkOnly: true) is { } problem)
                return problem;
        }
        var others = items.Where(i => i.ItemType != PaymentItemTypes.InstallationQuotation).ToList();
        return others.Count == 0 || afterSales is null ? null : await afterSales.CashSettlementProblemAsync(attempt, others, now, cancellationToken);
    }
}
