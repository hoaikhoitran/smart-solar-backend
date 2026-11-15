using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Modules.Identity.PasswordRecovery;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Identity;

/// <summary>
/// The password-reset outbox: a request accepted while the broker is down is published later (before, during or after the outage),
/// never after the token expired, never twice by competing callers, and a redelivered message does not send a second email.
/// </summary>
public sealed class PasswordResetOutboxTests
{
    private const string Password = "correct horse battery";
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<IdentityTestContext> WithUserAsync()
    {
        var ctx = new IdentityTestContext();
        await ctx.SeedActiveUserAsync("person@example.com", Password);
        return ctx;
    }

    private static Task RequestAsync(IdentityTestContext ctx)
        => ctx.CreateForgotPasswordHandler().HandleAsync(new ForgotPasswordCommand("person@example.com"), None);

    private static Task<PasswordResetDispatch> RowAsync(IdentityTestContext ctx)
    {
        ctx.Db.ChangeTracker.Clear();
        return ctx.Db.PasswordResetDispatches.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task A_healthy_broker_gets_the_event_at_once_and_the_raw_url_leaves_the_database()
    {
        await using var ctx = await WithUserAsync();

        await RequestAsync(ctx);

        var row = await RowAsync(ctx);
        var published = Assert.Single(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());
        Assert.Equal(row.Id, published.DispatchId);
        Assert.StartsWith(ctx.FrontendOptions.PasswordResetUrl + "?token=", published.ResetUrl);
        Assert.Equal((PasswordResetDispatchStatuses.Published, 1, (string?)null), (row.Status, row.PublishAttempts, row.ResetUrl));
        Assert.NotNull(row.PublishedAt);
    }

    [Fact]
    public async Task A_request_accepted_during_an_outage_is_published_by_the_sweep_once_the_broker_is_back()
    {
        await using var ctx = await WithUserAsync();
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");

        await RequestAsync(ctx);
        var dispatcher = ctx.CreatePasswordResetDispatcher();
        var row = await RowAsync(ctx);
        Assert.Equal(PasswordResetDispatchStatuses.Pending, row.Status);
        Assert.Equal(ctx.Clock.GetUtcNow() + PasswordResetDispatcher.Backoff(1), row.NextAttemptAt);

        // Still down at the next due time: one more failed attempt, longer backoff.
        ctx.Clock.Now = row.NextAttemptAt;
        Assert.Equal((0, 0), await dispatcher.SweepAsync(10, None));
        row = await RowAsync(ctx);
        Assert.Equal((2, PasswordResetDispatchStatuses.Pending), (row.PublishAttempts, row.Status));
        Assert.Equal(ctx.Clock.GetUtcNow() + PasswordResetDispatcher.Backoff(2), row.NextAttemptAt);

        // Not due yet: nothing happens.
        Assert.Equal((0, 0), await dispatcher.SweepAsync(10, None));
        Assert.Equal(2, (await RowAsync(ctx)).PublishAttempts);

        // Broker back: published exactly once, with the outbox id.
        ctx.Publisher.ThrowOnPublish = null;
        ctx.Clock.Now = row.NextAttemptAt;
        Assert.Equal((1, 0), await dispatcher.SweepAsync(10, None));
        Assert.Equal(row.Id, Assert.Single(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>()).DispatchId);
        Assert.Equal((0, 0), await dispatcher.SweepAsync(10, None));
        Assert.Single(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());
        Assert.Equal(PasswordResetDispatchStatuses.Published, (await RowAsync(ctx)).Status);
    }

    [Fact]
    public async Task Nothing_is_published_after_the_token_expired_and_the_url_is_erased()
    {
        await using var ctx = await WithUserAsync();
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");
        await RequestAsync(ctx);
        ctx.Publisher.ThrowOnPublish = null;

        ctx.Clock.Now = (await RowAsync(ctx)).ExpiresAt;
        Assert.Equal((0, 1), await ctx.CreatePasswordResetDispatcher().SweepAsync(10, None));

        var row = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Expired, (string?)null), (row.Status, row.ResetUrl));
        Assert.Empty(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());
    }

    [Fact]
    public async Task Without_email_delivery_nothing_is_published_and_a_newer_request_replaces_the_pending_row()
    {
        await using var ctx = await WithUserAsync();
        ctx.EmailCanDeliver = false;

        await RequestAsync(ctx);
        Assert.Empty(ctx.Publisher.Published);
        var first = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Pending, 0), (first.Status, first.PublishAttempts));
        Assert.Equal((0, 0), await ctx.CreatePasswordResetDispatcher().SweepAsync(10, None));

        // A second request replaces the unused token; its pending email is dropped with it (the old link is dead).
        await RequestAsync(ctx);
        var second = await RowAsync(ctx);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(1, await ctx.Db.AuthActionTokens.CountAsync());
    }

    [Fact]
    public async Task Only_one_of_two_competing_callers_claims_a_row()
    {
        await using var ctx = await WithUserAsync();
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");
        await RequestAsync(ctx);
        var row = await RowAsync(ctx);
        ctx.Clock.Now = row.NextAttemptAt;

        var store = ctx.DispatchStore;
        var first = await store.TryClaimAsync(row.Id, ctx.Clock.GetUtcNow(), PasswordResetDispatcher.Lease, None);
        var second = await store.TryClaimAsync(row.Id, ctx.Clock.GetUtcNow(), PasswordResetDispatcher.Lease, None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task The_consumer_records_sent_skips_redeliveries_and_superseded_rows_and_keeps_failures_retryable()
    {
        await using var ctx = await WithUserAsync();
        await RequestAsync(ctx);
        var message = Assert.Single(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());
        var sender = new FakeEmailSender();
        var consumer = new PasswordResetRequestedConsumer(
            new PasswordResetEmailService(sender, new PasswordResetOptions { LifetimeMinutes = 60 }, NullLogger<PasswordResetEmailService>.Instance),
            ctx.DispatchStore, ctx.Clock, NullLogger<PasswordResetRequestedConsumer>.Instance);

        // SMTP failure: the exception escapes (MassTransit retries) and the failure is recorded, the row is not SENT.
        sender.ThrowOnSend = new TimeoutException("smtp timeout");
        await Assert.ThrowsAsync<TimeoutException>(() => consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None)));
        Assert.Equal((PasswordResetDispatchStatuses.Published, nameof(TimeoutException)), ((await RowAsync(ctx)).Status, (await RowAsync(ctx)).LastError));

        // Retry succeeds: one email, row SENT.
        sender.ThrowOnSend = null;
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None));
        Assert.Single(sender.Sent);
        var row = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Sent, (string?)null), (row.Status, row.LastError));
        Assert.NotNull(row.SentAt);

        // Redelivery of the same message: no second email.
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None));
        Assert.Single(sender.Sent);

        // A message whose token was replaced by a newer request is dropped.
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message with { DispatchId = Guid.NewGuid() }, None));
        Assert.Single(sender.Sent);
    }
}
