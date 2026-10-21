using SmartSolar.Modules.Payments;

namespace SmartSolar.Modules.AfterSales;

/// <summary>
/// Applies the after-sales lines of a verified PayOS payment, inside the payment confirmation transaction (so every line is applied once).
/// A line is valid only when its package / quotation still waits for exactly this checkout and the provider-confirmed transaction time is
/// within the checkout deadline (a late webhook is not a late payment). Money that cannot be applied is never ignored: the line is marked
/// REVIEW_REQUIRED and a refund request is created for a Manager.
/// </summary>
public sealed class AfterSalesPaymentSettlement(IAfterSalesUnitOfWork data, SubscriptionTerms terms) : IPaymentItemSettlement
{
    public async Task SettleAsync(PaymentAttempt attempt, IReadOnlyList<PaymentAttemptItem> items, DateTimeOffset providerPaidAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var problem = item.SubscriptionId is { } subscriptionId
                ? await SettleSubscriptionAsync(attempt, subscriptionId, providerPaidAt, now, cancellationToken)
                : item.QuotationId is { } quotationId
                    ? await SettleQuotationAsync(attempt, quotationId, providerPaidAt, now, cancellationToken)
                    : "Unknown payment line.";
            if (problem is null)
            {
                item.Status = PaymentItemStatuses.Settled;
                continue;
            }
            item.Status = PaymentItemStatuses.ReviewRequired;
            item.Note = problem.Length <= 300 ? problem : problem[..300];
            var refund = new RefundRecord
            {
                Id = Guid.NewGuid(), CustomerUserId = attempt.CustomerUserId, PaymentAttemptId = attempt.Id, SubscriptionId = item.SubscriptionId,
                QuotationId = item.QuotationId, OriginalAmount = item.Amount, Reason = $"Payment received but not applied: {problem}",
                Status = RefundStatuses.PendingApproval, CreatedAt = now, UpdatedAt = now,
            };
            data.Add(refund);
            AfterSalesHistory.Record(data, EventSubjects.Refund, refund.Id, "REFUND_REQUESTED", null, refund.Reason, now);
        }
    }

    private async Task<string?> SettleSubscriptionAsync(PaymentAttempt attempt, Guid subscriptionId, DateTimeOffset providerPaidAt, DateTimeOffset now, CancellationToken token)
    {
        var subscription = await data.FindSubscriptionAsync(subscriptionId, true, token);
        if (subscription is null) return "The package order no longer exists.";
        if (subscription.CurrentPaymentAttemptId != attempt.Id) return "The package order belongs to another checkout.";
        if (subscription.Status is not (SubscriptionStatuses.AwaitingPayment or SubscriptionStatuses.CheckoutExpired))
            return $"The package order is {subscription.Status}.";
        if (providerPaidAt > (subscription.CheckoutExpiresAt ?? attempt.ExpiresAt)) return "The payment was made after the checkout expired.";
        subscription.Status = SubscriptionStatuses.Paid;
        subscription.PaidAt = providerPaidAt;
        subscription.UpdatedAt = now;
        subscription.Revision++;
        AfterSalesHistory.Record(data, EventSubjects.Subscription, subscription.Id, "PAID", null, $"{subscription.Price:0} VND verified", now);
        AfterSalesHistory.Notify(data, subscription.CustomerUserId, "PACKAGE_PAID", $"Payment received for {subscription.PackageName}.", now, subscriptionId: subscription.Id);
        // Starts and activates now when the installation is already handed over; otherwise the worker does it at handover.
        await terms.StartTermIfPossibleAsync(subscription, now, token);
        return null;
    }

    private async Task<string?> SettleQuotationAsync(PaymentAttempt attempt, Guid quotationId, DateTimeOffset providerPaidAt, DateTimeOffset now, CancellationToken token)
    {
        var quotation = await data.FindQuotationAsync(quotationId, true, token);
        if (quotation is null) return "The quotation no longer exists.";
        if (quotation.CurrentPaymentAttemptId != attempt.Id) return "The quotation belongs to another checkout.";
        if (quotation.Status != QuotationStatuses.Accepted) return $"The quotation is {quotation.Status}.";
        if (providerPaidAt > quotation.ExpiresAt) return "The payment was made after the quotation expired.";
        var request = (await data.FindRequestAsync(quotation.RequestId, true, token))!;
        await AfterSalesWorkflow.MarkQuotationPaidAsync(data, quotation, request, null, providerPaidAt, now, token);
        AfterSalesHistory.Notify(data, request.CustomerUserId, "QUOTATION_PAID", $"Payment received for {request.RequestNumber}.", now, request.Id);
        return null;
    }
}
