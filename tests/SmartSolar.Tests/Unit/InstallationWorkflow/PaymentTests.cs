using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSolar.Modules.CustomerContracts;
using SmartSolar.Modules.Payments;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.InstallationWorkflow;

public sealed class PaymentTests
{
    private static async Task<(InstallationWorkflowTestContext Ctx, Guid Customer, ContractView Contract)> SignedAsync()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        var (ready, contract) = await ContractTests.SignedAsync(ctx);
        return (ctx, ready.Survey.Request.Customer.Id, contract);
    }

    [Fact]
    public void Create_link_signature_follows_the_documented_payos_format()
    {
        var server = new FakePayOsServer();
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(FakePayOsServer.ChecksumKey),
            Encoding.UTF8.GetBytes("amount=1000&cancelUrl=https://c&description=SS1234567&orderCode=42&returnUrl=https://r"))).ToLowerInvariant();

        Assert.Equal(expected, server.Gateway().Sign("amount=1000&cancelUrl=https://c&description=SS1234567&orderCode=42&returnUrl=https://r"));
    }

    [Fact]
    public async Task Gateway_sends_signed_requests_with_credentials_in_headers_only()
    {
        var server = new FakePayOsServer();
        var link = await server.Gateway().CreatePaymentLinkAsync(new PaymentLinkRequest(42, 1000, "SS0000042", "https://r", "https://c",
            DateTimeOffset.UtcNow.AddMinutes(15), null, null, null), CancellationToken.None);

        Assert.Equal(GatewayCallStatus.Succeeded, link.Status);
        Assert.Equal("https://pay.payos.test/42", link.CheckoutUrl);
        var request = Assert.Single(server.Requests);
        Assert.Equal("client", request.ClientId);
        Assert.DoesNotContain("api-key", request.Path);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(server.Gateway().Sign("amount=1000&cancelUrl=https://c&description=SS0000042&orderCode=42&returnUrl=https://r"),
            body.RootElement.GetProperty("signature").GetString());
    }

    [Fact]
    public void Webhook_signature_is_verified_and_tampering_is_detected()
    {
        var server = new FakePayOsServer();
        var gateway = server.Gateway();
        var genuine = server.Webhook(42, 1000, "REF-1");

        var valid = gateway.VerifyWebhook(genuine);
        Assert.True(valid.SignatureValid);
        Assert.Equal(42, valid.OrderCode);
        Assert.Equal(1000, valid.Amount);
        Assert.Equal("REF-1", valid.Reference);

        Assert.False(gateway.VerifyWebhook(genuine.Replace("\"amount\":1000", "\"amount\":1")).SignatureValid);
        Assert.False(gateway.VerifyWebhook(server.Webhook(42, 1000, "REF-1", signatureOverride: new string('a', 64))).SignatureValid);
        Assert.False(gateway.VerifyWebhook("not json").SignatureValid);
        Assert.False(gateway.VerifyWebhook("{\"data\":{}}").SignatureValid);
    }

    [Fact]
    public async Task Payment_link_is_created_once_and_reused_while_payable()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;

        var first = await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None);
        var second = await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None);

        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(PaymentStatuses.Pending, first.Value!.Status);
        Assert.Equal(contract.TotalAmount, first.Value.Amount);
        Assert.NotNull(first.Value.CheckoutUrl);
        Assert.Equal(first.Value.PaymentAttemptId, second.Value!.PaymentAttemptId);
        Assert.Equal(1, ctx.PayOs.CreateCalls);
    }

    [Fact]
    public async Task Only_signed_contracts_of_the_customer_within_the_provider_limit_are_payable()
    {
        var ctx = InstallationWorkflowTestContext.Create();
        await using var _ = ctx;
        var ready = await ContractTests.AcceptedQuotationAsync(ctx);
        var published = await ContractTests.PublishedAsync(ctx, ready);
        var customer = ready.Survey.Request.Customer.Id;

        Assert.Equal(PaymentErrorCodes.ContractNotSigned, (await ctx.Payments().CreateOrGetAttemptAsync(customer, published.ContractId, CancellationToken.None)).ErrorCode);
        Assert.Equal(PaymentErrorCodes.ContractNotFound, (await ctx.Payments().CreateOrGetAttemptAsync(Guid.NewGuid(), published.ContractId, CancellationToken.None)).ErrorCode);

        var under = await ContractTests.SubmitSignedAsync(ctx, ready, published);
        await ctx.Contracts().ReviewAsync(ready.Survey.Sales.Id, false, under.ContractId, under.Revision, ReviewDecision.Approve, null, CancellationToken.None);
        ctx.PaymentOptions.MaxAmountVnd = 1_000;
        Assert.Equal(PaymentErrorCodes.AmountNotSupported, (await ctx.Payments().CreateOrGetAttemptAsync(customer, published.ContractId, CancellationToken.None)).ErrorCode);
        Assert.Equal(0, ctx.PayOs.CreateCalls);
    }

    [Fact]
    public async Task Verified_webhook_marks_attempt_and_contract_paid_atomically_and_duplicates_change_nothing()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
        var body = ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, "FT123");

        var applied = await ctx.Payments().HandleWebhookAsync(body, CancellationToken.None);
        var duplicate = await ctx.Payments().HandleWebhookAsync(body, CancellationToken.None);

        Assert.Equal(WebhookOutcomes.Applied, applied.Outcome);
        Assert.Equal(WebhookOutcomes.Duplicate, duplicate.Outcome);
        var view = (await ctx.Payments().GetForStaffAsync(contract.ContractId, CancellationToken.None)).Value!;
        Assert.Equal(ContractStatuses.Paid, view.ContractStatus);
        Assert.Equal(PaymentStatuses.Paid, view.Current!.Status);
        Assert.Equal("FT123", view.Current.ProviderReference);
        Assert.Single(view.History!, e => e.EventType == "PAID");
        Assert.Single(await ctx.Db.PaymentWebhookEvents.AsNoTracking().ToListAsync());
        Assert.Equal(PaymentErrorCodes.AlreadyPaid, (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Invalid_signatures_and_wrong_amounts_never_pay_the_contract()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;

        var forged = await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, "X", signatureOverride: new string('b', 64)), CancellationToken.None);
        Assert.False(forged.Accepted);
        Assert.Equal(PaymentStatuses.Pending, (await ctx.Payments().GetForStaffAsync(contract.ContractId, CancellationToken.None)).Value!.Current!.Status);

        var partial = await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, 1_000, "PART"), CancellationToken.None);
        Assert.Equal(WebhookOutcomes.Applied, partial.Outcome);
        var view = (await ctx.Payments().GetForStaffAsync(contract.ContractId, CancellationToken.None)).Value!;
        Assert.Equal(ContractStatuses.Signed, view.ContractStatus);
        Assert.Equal(PaymentStatuses.ReviewRequired, view.Attempts.Single().Status);
    }

    [Fact]
    public async Task Return_url_is_not_trusted_only_the_provider_status_query_confirms()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;
        var attempt = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;

        // The customer comes back from checkout but PayOS still says PENDING.
        var pending = (await ctx.Payments().GetForCustomerAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
        Assert.Equal(PaymentStatuses.Pending, pending.Current!.Status);

        ctx.PayOs.Links[attempt.OrderCode] = ("PAID", (long)attempt.Amount, "FT-RECON");
        ctx.Clock.Now = ctx.Clock.Now.AddMinutes(1);
        var paid = (await ctx.Payments().GetForCustomerAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
        Assert.Equal(PaymentStatuses.Paid, paid.Current!.Status);
        Assert.Equal(ContractStatuses.Paid, paid.ContractStatus);

        // The webhook arriving after reconciliation is harmless.
        Assert.Equal(WebhookOutcomes.Applied, (await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(attempt.OrderCode, (long)attempt.Amount, "FT-RECON"), CancellationToken.None)).Outcome);
        Assert.Single((await ctx.Payments().GetForStaffAsync(contract.ContractId, CancellationToken.None)).Value!.History!, e => e.EventType == "PAID");
    }

    [Fact]
    public async Task Expired_links_are_retried_with_a_new_attempt_and_a_late_second_payment_requires_refund()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;
        var first = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
        ctx.PayOs.Links[first.OrderCode] = ("EXPIRED", 0, null);
        ctx.Clock.Now = first.ExpiresAt.AddMinutes(1);

        var second = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
        Assert.NotEqual(first.PaymentAttemptId, second.PaymentAttemptId);
        Assert.Equal(2, ctx.PayOs.CreateCalls);

        Assert.Equal(WebhookOutcomes.Applied, (await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(second.OrderCode, (long)second.Amount, "FT-2"), CancellationToken.None)).Outcome);
        // The customer somehow also paid the expired link: the contract is never paid twice.
        await ctx.Payments().HandleWebhookAsync(ctx.PayOs.Webhook(first.OrderCode, (long)first.Amount, "FT-1"), CancellationToken.None);

        var view = (await ctx.Payments().GetForStaffAsync(contract.ContractId, CancellationToken.None)).Value!;
        Assert.Single(view.Attempts, a => a.Status == PaymentStatuses.Paid);
        Assert.Equal(PaymentStatuses.RefundRequired, view.Attempts.Single(a => a.PaymentAttemptId == first.PaymentAttemptId).Status);
        Assert.Equal(ContractStatuses.Paid, view.ContractStatus);
    }

    [Fact]
    public async Task Provider_outages_and_missing_configuration_free_the_slot_for_a_retry()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;
        ctx.PayOs.Down = true;
        Assert.Equal(PaymentErrorCodes.ProviderError, (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).ErrorCode);

        ctx.PayOs.Options.ApiKey = null;
        Assert.Equal(PaymentErrorCodes.ProviderNotConfigured, (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).ErrorCode);

        ctx.PayOs.Options.ApiKey = "api-key";
        ctx.PayOs.Down = false;
        Assert.True((await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Succeeded);
        var attempts = (await ctx.Payments().GetForStaffAsync(contract.ContractId, CancellationToken.None)).Value!.Attempts;
        Assert.Equal(2, attempts.Count(a => a.Status == PaymentStatuses.Failed));
    }

    [Fact]
    public async Task Attempt_creation_is_capped_per_contract_across_requests()
    {
        var (ctx, customer, contract) = await SignedAsync();
        await using var _ = ctx;
        ctx.PaymentOptions.MaxAttemptsPerTenMinutes = 2;
        for (var i = 0; i < 2; i++)
        {
            var created = (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).Value!;
            await ctx.Payments().CancelCurrentAsync(customer, contract.ContractId, CancellationToken.None);
            Assert.Equal("CANCELLED", ctx.PayOs.Links[created.OrderCode].Status);
        }

        Assert.Equal(PaymentErrorCodes.TooManyAttempts, (await ctx.Payments().CreateOrGetAttemptAsync(customer, contract.ContractId, CancellationToken.None)).ErrorCode);
    }
}
