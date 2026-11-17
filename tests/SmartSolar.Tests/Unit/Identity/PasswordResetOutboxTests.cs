using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.EmailVerification;
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

    private static (PasswordResetRequestedConsumer Consumer, FakeEmailSender Sender) Consumer(IdentityTestContext ctx)
    {
        var sender = new FakeEmailSender();
        return (new PasswordResetRequestedConsumer(
            new PasswordResetEmailService(sender, new PasswordResetOptions { LifetimeMinutes = 60 }, NullLogger<PasswordResetEmailService>.Instance),
            new AuthLinkEmailDelivery(ctx.DispatchStore, ctx.Clock, NullLogger<AuthLinkEmailDelivery>.Instance)), sender);
    }

    [Fact]
    public async Task An_smtp_outage_longer_than_the_broker_retry_hands_the_row_back_and_ends_in_exactly_one_email()
    {
        await using var ctx = await WithUserAsync();
        await RequestAsync(ctx);
        var (consumer, sender) = Consumer(ctx);
        var dispatcher = ctx.CreatePasswordResetDispatcher();
        sender.ThrowOnSend = new TimeoutException("smtp down");

        // Ten minutes of SMTP outage: every delivery is acknowledged (no exception, nothing left in an error queue) and the row goes
        // back to PENDING with its URL; the worker re-publishes it after the backoff.
        var deliveries = 0;
        for (var round = 0; round < 6; round++)
        {
            var message = ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Last();
            await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None));   // does not throw
            deliveries++;
            var row = await RowAsync(ctx);
            Assert.Equal((PasswordResetDispatchStatuses.Pending, nameof(TimeoutException)), (row.Status, row.LastError));
            Assert.Equal(message.ResetUrl, row.ResetUrl);
            Assert.Equal(ctx.Clock.GetUtcNow() + PasswordResetDispatcher.Backoff(row.PublishAttempts), row.NextAttemptAt);
            ctx.Clock.Now = row.NextAttemptAt;
            Assert.Equal((1, 0), await dispatcher.SweepAsync(10, None));
        }
        Assert.Equal(7, ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Count());
        Assert.Empty(sender.Sent);

        // SMTP back: exactly one email, row SENT, URL erased.
        sender.ThrowOnSend = null;
        var last = ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Last();
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(last, None));
        var sent = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Sent, (string?)null, (string?)null), (sent.Status, sent.ResetUrl, sent.LastError));
        Assert.Single(sender.Sent);

        // Redelivery of any earlier copy: skipped, still one email.
        foreach (var copy in ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>())
            await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(copy, None));
        Assert.Single(sender.Sent);
        Assert.Equal(6, deliveries);
    }

    [Fact]
    public async Task An_outage_longer_than_the_link_lifetime_expires_the_row_and_erases_the_url()
    {
        await using var ctx = await WithUserAsync();
        await RequestAsync(ctx);
        var (consumer, sender) = Consumer(ctx);
        sender.ThrowOnSend = new TimeoutException("smtp down");
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Single(), None));
        Assert.Equal(PasswordResetDispatchStatuses.Pending, (await RowAsync(ctx)).Status);

        ctx.Clock.Now = (await RowAsync(ctx)).ExpiresAt;
        Assert.Equal((0, 1), await ctx.CreatePasswordResetDispatcher().SweepAsync(10, None));
        var row = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Expired, (string?)null), (row.Status, row.ResetUrl));

        // A copy still in the broker is dropped: the link is dead.
        sender.ThrowOnSend = null;
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>().Single(), None));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task The_consumer_records_sent_skips_redeliveries_and_superseded_rows_and_never_retries_an_invalid_recipient()
    {
        await using var ctx = await WithUserAsync();
        await RequestAsync(ctx);
        var message = Assert.Single(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());
        var (consumer, sender) = Consumer(ctx);

        // A malformed recipient is permanent: recorded, not handed back, no exception.
        sender.ThrowOnSend = new MimeKit.ParseException("bad address", 0, 0);
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None));
        var failed = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Published, nameof(MimeKit.ParseException), (string?)null), (failed.Status, failed.LastError, failed.ResetUrl));

        sender.ThrowOnSend = null;
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None));
        Assert.Single(sender.Sent);
        var row = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Sent, (string?)null), (row.Status, row.LastError));
        Assert.NotNull(row.SentAt);

        // Redelivery: no second email. A message whose token was replaced by a newer request is dropped.
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message, None));
        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(message with { DispatchId = Guid.NewGuid() }, None));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Verification_emails_of_register_and_resend_survive_a_broker_outage_and_an_smtp_outage()
    {
        await using var ctx = new IdentityTestContext();
        await ctx.SeedCustomerRoleAsync();
        ctx.Publisher.ThrowOnPublish = new InvalidOperationException("broker down");

        var registered = await ctx.CreateRegisterHandler().HandleAsync(
            new SmartSolar.Modules.Identity.Register.RegisterCommand("new@example.com", Password, "New Person", null), None);
        Assert.Equal(SmartSolar.Modules.Identity.Register.RegisterOutcome.Created, registered.Outcome);
        Assert.Empty(ctx.Publisher.Published);
        var row = await RowAsync(ctx);
        Assert.Equal((PasswordResetDispatchStatuses.Pending, 1), (row.Status, row.PublishAttempts));
        Assert.StartsWith(ctx.FrontendOptions.EmailVerificationUrl + "?token=", row.ResetUrl);

        // Broker back: the sweep publishes the VERIFICATION event (the kind comes from the token type), with the outbox id.
        ctx.Publisher.ThrowOnPublish = null;
        ctx.Clock.Now = row.NextAttemptAt;
        Assert.Equal((1, 0), await ctx.CreatePasswordResetDispatcher().SweepAsync(10, None));
        var published = Assert.Single(ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>());
        Assert.Equal((row.Id, row.ResetUrl), (published.DispatchId, published.VerificationUrl));
        Assert.Empty(ctx.Publisher.PublishedOf<PasswordResetRequestedEvent>());

        // SMTP down for the verification consumer: handed back, then delivered once.
        var sender = new FakeEmailSender { ThrowOnSend = new TimeoutException("smtp down") };
        var consumer = new EmailVerificationRequestedConsumer(
            new VerificationEmailService(sender, new EmailVerificationOptions { LifetimeMinutes = 1440 }, NullLogger<VerificationEmailService>.Instance),
            new AuthLinkEmailDelivery(ctx.DispatchStore, ctx.Clock, NullLogger<AuthLinkEmailDelivery>.Instance));
        await consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(published, None));
        Assert.Equal(PasswordResetDispatchStatuses.Pending, (await RowAsync(ctx)).Status);
        sender.ThrowOnSend = null;
        ctx.Clock.Now = (await RowAsync(ctx)).NextAttemptAt;
        await ctx.CreatePasswordResetDispatcher().SweepAsync(10, None);
        await consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>().Last(), None));
        Assert.Single(sender.Sent);
        Assert.Equal(PasswordResetDispatchStatuses.Sent, (await RowAsync(ctx)).Status);

        // Resend replaces the unused token: the old outbox row goes with it (cascade) and the new link is published with its own id.
        await ctx.CreateResendVerificationHandler().HandleAsync(new ResendVerificationCommand("new@example.com"), None);
        var current = await RowAsync(ctx);
        Assert.NotEqual(row.Id, current.Id);
        Assert.Equal(PasswordResetDispatchStatuses.Published, current.Status);
        Assert.Equal(current.Id, ctx.Publisher.PublishedOf<EmailVerificationRequestedEvent>().Last().DispatchId);
    }
}
