using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSolar.Infrastructure.Email;
using SmartSolar.Infrastructure.Messaging.RabbitMQ.Consumers;
using SmartSolar.Modules.Identity.Events;
using SmartSolar.Modules.Identity.Options;
using SmartSolar.Tests.TestSupport;

namespace SmartSolar.Tests.Unit.Email;

public class PasswordResetEmailServiceTests
{
    private const string Url = "https://app.test/reset-password?token=RAW-RESET-TOKEN";

    private static PasswordResetRequestedEvent Event(string fullName = "Test Person")
        => new(Guid.NewGuid(), "person@example.com", fullName, Url);

    private static (PasswordResetEmailService Service, FakeEmailSender Sender) Create(int lifetimeMinutes = 60)
    {
        var sender = new FakeEmailSender();
        return (
            new PasswordResetEmailService(
                sender,
                new PasswordResetOptions { LifetimeMinutes = lifetimeMinutes },
                NullLogger<PasswordResetEmailService>.Instance),
            sender);
    }

    [Fact]
    public async Task Sends_to_the_address_with_the_reset_subject_and_link()
    {
        var (service, sender) = Create();

        await service.SendPasswordResetEmailAsync(Event(), CancellationToken.None);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("person@example.com", message.To);
        Assert.Equal("Reset your Smart Solar password", message.Subject);
        Assert.Contains(Url, message.HtmlBody);
        Assert.Contains(Url, message.TextBody!);
    }

    [Fact]
    public async Task Html_encodes_the_full_name()
    {
        var (service, sender) = Create();

        await service.SendPasswordResetEmailAsync(
            Event(fullName: "<script>alert('x')</script>"), CancellationToken.None);

        Assert.DoesNotContain("<script>", sender.Sent[0].HtmlBody);
        Assert.Contains("&lt;script&gt;", sender.Sent[0].HtmlBody);
    }

    [Theory]
    [InlineData(60, "1 hour")]
    [InlineData(30, "30 minutes")]
    public async Task States_the_configured_expiry(int lifetimeMinutes, string expected)
    {
        var (service, sender) = Create(lifetimeMinutes);

        await service.SendPasswordResetEmailAsync(Event(), CancellationToken.None);

        Assert.Contains(expected, sender.Sent[0].HtmlBody);
    }

    [Fact]
    public async Task Propagates_sender_failures_so_the_message_can_be_retried()
    {
        var (service, sender) = Create();
        sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SendPasswordResetEmailAsync(Event(), CancellationToken.None));
    }
}

public class PasswordResetConsumerTests
{
    private static (PasswordResetRequestedConsumer Consumer, FakeEmailSender Sender) Create()
    {
        var sender = new FakeEmailSender();
        var service = new PasswordResetEmailService(
            sender,
            new PasswordResetOptions { LifetimeMinutes = 60 },
            NullLogger<PasswordResetEmailService>.Instance);

        return (new PasswordResetRequestedConsumer(service), sender);
    }

    private static PasswordResetRequestedEvent Event()
        => new(Guid.NewGuid(), "person@example.com", "Test Person", "https://app.test/reset-password?token=RAW");

    [Fact]
    public async Task Consuming_the_event_sends_the_reset_email()
    {
        var (consumer, sender) = Create();

        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(
            Event(), CancellationToken.None));

        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Smtp_failures_escape_the_consumer_so_masstransit_can_retry()
    {
        var (consumer, sender) = Create();
        sender.ThrowOnSend = new InvalidOperationException("smtp down");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(
                Event(), CancellationToken.None)));
    }

    [Fact]
    public async Task Passes_the_cancellation_token_to_the_sender()
    {
        var (consumer, sender) = Create();
        using var cts = new CancellationTokenSource();

        await consumer.Consume(new StubConsumeContext<PasswordResetRequestedEvent>(Event(), cts.Token));

        Assert.Equal(cts.Token, sender.LastCancellationToken);
    }

    [Fact]
    public void Consumer_consumes_the_password_reset_event()
    {
        Assert.Contains(
            typeof(PasswordResetRequestedConsumer).GetInterfaces(),
            i => i.IsGenericType
                 && i.GetGenericTypeDefinition() == typeof(IConsumer<>)
                 && i.GenericTypeArguments[0] == typeof(PasswordResetRequestedEvent));
    }

    [Fact]
    public void Endpoint_uses_its_own_durable_queue_with_the_same_retry_contract()
    {
        Assert.Equal("password-reset", PasswordResetEndpoint.QueueName);
        Assert.NotEqual(EmailVerificationEndpoint.QueueName, PasswordResetEndpoint.QueueName);
        Assert.Equal(3, PasswordResetEndpoint.RetryLimit);
        Assert.Equal(TimeSpan.FromSeconds(5), PasswordResetEndpoint.RetryInterval);
    }
}
