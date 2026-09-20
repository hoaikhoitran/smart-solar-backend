using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Email;

public class EmailVerificationRequestedConsumerTests
{
    private static (EmailVerificationRequestedConsumer Consumer, FakeEmailSender Sender) Create()
    {
        var sender = new FakeEmailSender();
        var service = new VerificationEmailService(
            sender,
            new EmailVerificationOptions { LifetimeMinutes = 1440 },
            NullLogger<VerificationEmailService>.Instance);

        return (new EmailVerificationRequestedConsumer(service), sender);
    }

    private static EmailVerificationRequestedEvent Event()
        => new(Guid.NewGuid(), "person@example.com", "Test Person", "https://app.test/verify-email?token=RAW");

    [Fact]
    public async Task Consuming_the_event_sends_the_verification_email()
    {
        var (consumer, sender) = Create();

        await consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(
            Event(), CancellationToken.None));

        var message = Assert.Single(sender.Sent);
        Assert.Equal("person@example.com", message.To);
        Assert.Equal("Verify your Smart Solar account", message.Subject);
    }

    [Fact]
    public async Task Smtp_failures_escape_the_consumer_so_masstransit_can_retry()
    {
        // A catch inside Consume would ack the message and lose the email.
        var (consumer, sender) = Create();
        sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(
                Event(), CancellationToken.None)));
    }

    [Fact]
    public async Task Passes_the_consume_context_cancellation_token_down_to_the_sender()
    {
        var (consumer, sender) = Create();
        using var cts = new CancellationTokenSource();

        await consumer.Consume(new StubConsumeContext<EmailVerificationRequestedEvent>(Event(), cts.Token));

        Assert.Equal(cts.Token, sender.LastCancellationToken);
    }
}
