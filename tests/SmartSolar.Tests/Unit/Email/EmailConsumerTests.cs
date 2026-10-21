using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Email;

/// <summary>
/// The two RabbitMQ email consumers: they send the email with the consume context's cancellation token, let SMTP failures escape
/// (a catch inside Consume would ack the message and lose the email), and listen on their own durable queues with a short,
/// bounded retry. Endpoint values are asserted, not exercised: proving they reach MassTransit needs a broker.
/// </summary>
public class EmailConsumerTests
{
    private static (EmailVerificationRequestedConsumer Consumer, FakeEmailSender Sender) Verification()
    {
        var sender = new FakeEmailSender();
        var service = new VerificationEmailService(sender, new EmailVerificationOptions { LifetimeMinutes = 1440 }, NullLogger<VerificationEmailService>.Instance);
        return (new EmailVerificationRequestedConsumer(service), sender);
    }

    private static (PasswordResetRequestedConsumer Consumer, FakeEmailSender Sender) Reset()
    {
        var sender = new FakeEmailSender();
        var service = new PasswordResetEmailService(sender, new PasswordResetOptions { LifetimeMinutes = 60 }, NullLogger<PasswordResetEmailService>.Instance);
        return (new PasswordResetRequestedConsumer(service), sender);
    }

    private static EmailVerificationRequestedEvent VerificationEvent() => new(Guid.NewGuid(), "person@example.com", "Test Person", "https://app.test/verify-email?token=RAW");
    private static PasswordResetRequestedEvent ResetEvent() => new(Guid.NewGuid(), "person@example.com", "Test Person", "https://app.test/reset-password?token=RAW");

    [Fact]
    public async Task The_verification_consumer_sends_with_the_consume_token_and_lets_failures_escape()
    {
        var (consumer, sender) = Verification();
        using var cts = new CancellationTokenSource();

        await consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(VerificationEvent(), cts.Token));

        var message = Assert.Single(sender.Sent);
        Assert.Equal(("person@example.com", "Verify your Smart Solar account"), (message.To, message.Subject));
        Assert.Equal(cts.Token, sender.LastCancellationToken);

        sender.ThrowOnSend = new InvalidOperationException("smtp down");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(VerificationEvent(), CancellationToken.None)));
    }

    [Fact]
    public async Task The_password_reset_consumer_sends_with_the_consume_token_and_lets_failures_escape()
    {
        var (consumer, sender) = Reset();
        using var cts = new CancellationTokenSource();

        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(ResetEvent(), cts.Token));

        Assert.Equal("Reset your Smart Solar password", Assert.Single(sender.Sent).Subject);
        Assert.Equal(cts.Token, sender.LastCancellationToken);

        sender.ThrowOnSend = new InvalidOperationException("smtp down");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(ResetEvent(), CancellationToken.None)));
    }

    [Fact]
    public void Each_email_has_its_own_durable_queue_with_three_retries_five_seconds_apart()
    {
        Assert.Equal("email-verification", EmailVerificationEndpoint.QueueName);
        Assert.Equal("password-reset", PasswordResetEndpoint.QueueName);
        Assert.Equal((3, TimeSpan.FromSeconds(5)), (EmailVerificationEndpoint.RetryLimit, EmailVerificationEndpoint.RetryInterval));
        Assert.Equal((3, TimeSpan.FromSeconds(5)), (PasswordResetEndpoint.RetryLimit, PasswordResetEndpoint.RetryInterval));
    }
}
