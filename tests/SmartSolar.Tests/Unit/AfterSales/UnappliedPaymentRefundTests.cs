using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.AfterSales;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;
using SmartSolar.Tests.Unit.InstallationWorkflow;

namespace SmartSolar.Tests.Unit.AfterSales;

/// <summary>
/// Contract money that PayOS confirmed but that could not be applied (a second payment of an already paid contract, a wrong amount)
/// becomes a PENDING_APPROVAL refund request in the Manager queue, in the confirmation transaction, exactly once. Amounts are SYNTHETIC.
/// </summary>
public sealed class UnappliedPaymentRefundTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_late_payment_of_an_expired_link_for_a_paid_contract_opens_one_refund_request()
    {
        await using var ctx = AfterSalesTestContext.Create();
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        var customer = ready.Survey.Request.Customer.Id;
        var payments = ctx.Payments();
        var payOs = ctx.Installation.PayOs;
        var first = (await payments.CreateOrGetAttemptAsync(customer, contract.ContractId, None)).Value!;
        payOs.Links[first.OrderCode] = ("EXPIRED", 0, null);
        ctx.Installation.Clock.Now = first.ExpiresAt.AddMinutes(1);
        var second = (await payments.CreateOrGetAttemptAsync(customer, contract.ContractId, None)).Value!;
        await payments.HandleWebhookAsync(payOs.Webhook(second.OrderCode, (long)second.Amount, "FT-2"), None);

        // The customer also paid the old link; the webhook is then delivered twice.
        await payments.HandleWebhookAsync(payOs.Webhook(first.OrderCode, (long)first.Amount, "FT-1"), None);
        await payments.HandleWebhookAsync(payOs.Webhook(first.OrderCode, (long)first.Amount, "FT-1"), None);

        var refund = Assert.Single(await ctx.Refunds().ListAsync(RefundStatuses.PendingApproval, None));
        Assert.Equal((customer, first.PaymentAttemptId, first.Amount), (refund.CustomerUserId, refund.PaymentAttemptId!.Value, refund.OriginalAmount));
        Assert.Contains("already paid", refund.Reason);
        Assert.Equal(ContractStatuses.Paid, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == contract.ContractId)).Status);
    }

    [Fact]
    public async Task A_wrong_amount_leaves_the_contract_unpaid_and_opens_a_refund_request_for_the_amount_received()
    {
        await using var ctx = AfterSalesTestContext.Create();
        var (ready, contract) = await ContractTests.SignedAsync(ctx.Installation);
        var payments = ctx.Payments();
        var attempt = (await payments.CreateOrGetAttemptAsync(ready.Survey.Request.Customer.Id, contract.ContractId, None)).Value!;

        await payments.HandleWebhookAsync(ctx.Installation.PayOs.Webhook(attempt.OrderCode, 1_000_000, "FT-PART"), None);

        Assert.Equal(PaymentStatuses.ReviewRequired, (await ctx.Db.PaymentAttempts.AsNoTracking().SingleAsync(a => a.Id == attempt.PaymentAttemptId)).Status);
        Assert.Equal(ContractStatuses.Signed, (await ctx.Db.CustomerContracts.AsNoTracking().SingleAsync(c => c.Id == contract.ContractId)).Status);
        var refund = Assert.Single(await ctx.Refunds().ListAsync(RefundStatuses.PendingApproval, None));
        Assert.Equal(1_000_000m, refund.OriginalAmount);
    }
}
